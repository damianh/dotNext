using System.Collections.Concurrent;
using System.Diagnostics;
using System.Diagnostics.Metrics;
using System.Runtime.CompilerServices;
using System.Text;

namespace DotNext.Net.Cluster.Consensus.Raft.StateMachine;

using static IO.DataTransferObject;

[Collection(TestCollections.WriteAheadLog)]
public sealed class WriteAheadLogFlushTests : Test
{
    [Theory]
    [InlineData(false, false)]
    [InlineData(false, true)]
    [InlineData(true, false)]
    [InlineData(true, true)]
    public static async Task PageFailureFailsExplicitRequests(bool flushOnCommit, bool subsequent)
    {
        var startup = new PausedFlusherContext();
        var options = CreateOptions(flushOnCommit ? TimeSpan.Zero : TimeSpan.FromDays(1));
        await using var wal = startup.CreateLog(options);
        using var cancellation = new CancellationTokenSource();
        var data = Path.Combine(options.Location, "data");
        var unavailable = Path.Combine(options.Location, "data-unavailable");
        Task[] pending = [];
        try
        {
            await wal.AppendAsync(new TestLogEntry("not persisted"), TestToken);
            await wal.CommitAsync(1L, TestToken);
            pending = [wal.FlushAsync(cancellation.Token), wal.FlushAsync(cancellation.Token)];
            All(pending, static request => False(request.IsCompleted));

            Directory.Move(data, unavailable);
            startup.Resume();
            await FlusherTask(wal).WaitAsync(TestToken);

            // Worker termination plus its stored I/O error establishes the failed pass.
            var stored = await ThrowsAsync<WriteAheadLog.InternalException>(
                () => wal.WaitForApplyAsync(0L, TestToken).AsTask());
            IsAssignableFrom<IOException>(stored.InnerException);
            Equal(1L, wal.LastCommittedEntryIndex);
            False(File.Exists(Path.Combine(unavailable, "0")));
            Equal(0L, new FileInfo(Path.Combine(options.Location, "checkpoint")).Length);

            var requests = subsequent ? new[] { wal.FlushAsync(cancellation.Token) } : pending;
            TestContext.Current.TestOutputHelper.WriteLine(
                $"Worker completed: {FlusherTask(wal).IsCompleted}; stored: {stored.InnerException.GetType().Name}; " +
                $"target: 1; checkpoint bytes: 0; subsequent: {subsequent}; " +
                $"requests completed: {string.Join(", ", requests.Select(static request => request.IsCompleted))}");
            foreach (var request in requests)
            {
                var error = await ThrowsAsync<WriteAheadLog.InternalException>(
                    () => request.WaitAsync(TimeSpan.FromSeconds(2), TestToken));
                Same(stored.InnerException, error.InnerException);
            }
        }
        finally
        {
            cancellation.Cancel();
            startup.Resume();
            await Task.WhenAll(pending).ConfigureAwait(ConfigureAwaitOptions.SuppressThrowing);
            if (Directory.Exists(unavailable))
                Directory.Move(unavailable, data);
        }
    }

    [UnsafeAccessor(UnsafeAccessorKind.Field, Name = "flusherTask")]
    private static extern ref Task FlusherTask(WriteAheadLog wal);

    [Theory]
    [InlineData(0)]
    [InlineData(1)]
    [InlineData(2)]
    public static async Task ApplierFailureFailsFlushRequests(int mode)
    {
        var machine = new GatedFailureStateMachine(failApply: true);
        var startup = new PausedFlusherContext();
        var interval = mode switch { 0 => TimeSpan.Zero, 1 => TimeSpan.FromDays(1), _ => Timeout.InfiniteTimeSpan };
        await using var wal = startup.CreateLog(CreateOptions(interval), machine);
        using var cancellation = new CancellationTokenSource();
        Task[] pending = [];
        try
        {
            await wal.AppendAsync(new TestLogEntry("payload"), TestToken);
            await wal.CommitAsync(1L, TestToken);
            await machine.Entered.Task.WaitAsync(TestToken);
            if (mode != 2)
            {
                pending = [wal.FlushAsync(cancellation.Token), wal.FlushAsync(cancellation.Token)];
                All(pending, static request => False(request.IsCompleted));
            }

            machine.Release.TrySetResult();
            await ApplierTask(wal).WaitAsync(TestToken);
            var stored = await ThrowsAsync<WriteAheadLog.InternalException>(
                () => wal.WaitForApplyAsync(0L, TestToken).AsTask());
            Same(machine.Error, stored.InnerException);

            foreach (var request in pending.Append(wal.FlushAsync(cancellation.Token)))
            {
                var error = await ThrowsAsync<WriteAheadLog.InternalException>(
                    () => request.WaitAsync(TimeSpan.FromSeconds(2), TestToken));
                Same(machine.Error, error.InnerException);
            }
        }
        finally
        {
            machine.Release.TrySetResult();
            cancellation.Cancel();
            startup.Resume();
            await Task.WhenAll(pending).ConfigureAwait(ConfigureAwaitOptions.SuppressThrowing);
        }
    }

    [Fact]
    public static async Task CleanerFailureFailsPendingFlushRequests()
    {
        var machine = new GatedFailureStateMachine(failApply: false);
        var startup = new PausedFlusherContext();
        using var passes = new FlushPasses(initiallyEnabled: false);
        await using var wal = startup.CreateLog(CreateOptions(TimeSpan.Zero, passes.Tags), machine);
        using var cancellation = new CancellationTokenSource();
        Task pending = Task.CompletedTask;
        try
        {
            // Start before any snapshot exists so a later pass schedules cleanup.
            startup.Resume();
            for (var i = 0; i < 3; i++)
                await wal.AppendAsync(new TestLogEntry("snapshot payload"), TestToken);
            await wal.CommitAsync(3L, TestToken);
            await wal.WaitForApplyAsync(3L, TestToken);
            await wal.FlushAsync(TestToken);
            await wal.AppendAsync(new TestLogEntry("trigger cleanup"), TestToken);
            await wal.CommitAsync(4L, TestToken);
            await wal.FlushAsync(TestToken);
            await machine.Entered.Task.WaitAsync(TestToken);
            True(CleanupTask(wal).TryGetTarget(out var cleanup));

            // Hold the next page flush before its checkpoint, while cleanup fails.
            passes.Enable();
            await wal.AppendAsync(new TestLogEntry("pending target"), TestToken);
            await wal.CommitAsync(5L, TestToken);
            await passes.First.Entered.Task.WaitAsync(TestToken);
            pending = wal.FlushAsync(cancellation.Token);
            False(pending.IsCompleted);
            machine.Release.TrySetResult();
            await cleanup.WaitAsync(TestToken);
            var stored = await ThrowsAsync<WriteAheadLog.InternalException>(
                () => wal.WaitForApplyAsync(0L, TestToken).AsTask());
            Same(machine.Error, stored.InnerException);
            var error = await ThrowsAsync<WriteAheadLog.InternalException>(
                () => pending.WaitAsync(TimeSpan.FromSeconds(2), TestToken));
            Same(machine.Error, error.InnerException);
        }
        finally
        {
            machine.Release.TrySetResult();
            cancellation.Cancel();
            passes.First.Release();
            passes.Second.Release();
            await pending.ConfigureAwait(ConfigureAwaitOptions.SuppressThrowing);
        }
    }

    [UnsafeAccessor(UnsafeAccessorKind.Field, Name = "appenderTask")]
    private static extern ref Task ApplierTask(WriteAheadLog wal);

    [UnsafeAccessor(UnsafeAccessorKind.Field, Name = "cleanupTask")]
    private static extern ref WeakReference<Task> CleanupTask(WriteAheadLog wal);

    [Fact]
    public static async Task SingleCommittedEntryMustBeFlushed()
    {
        var options = CreateOptions(TimeSpan.FromDays(1));
        var entry = new TestLogEntry("committed payload") { Term = 42L };
        var startup = new PausedFlusherContext();
        await using (var wal = startup.CreateLog(options))
        {
            try
            {
                Equal(1L, await wal.AppendAsync(entry, TestToken));
                Equal(1L, await wal.CommitAsync(1L, TestToken));

                var flush = wal.FlushAsync(TestToken);
                False(flush.IsCompletedSuccessfully);
                False(flush.IsCompleted);

                startup.Resume();
                await flush.WaitAsync(TestToken);
            }
            finally
            {
                startup.Resume();
            }
        }

        await using var reopened = new WriteAheadLog(options, IStateMachine.CreateNoOp());
        Equal(1L, reopened.LastCommittedEntryIndex);
        Equal(1L, reopened.LastEntryIndex);
        using var reader = await reopened.ReadAsync(1L, 1L, TestToken);
        Equal(entry.Content, await reader[0].ToStringAsync(Encoding.UTF8, token: TestToken));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public static async Task MultipleCallersWaitForSameTarget(bool flushOnCommit)
    {
        var startup = new PausedFlusherContext();
        await using var wal = startup.CreateLog(CreateOptions(flushOnCommit ? TimeSpan.Zero : TimeSpan.FromDays(1)));
        try
        {
            await wal.AppendAsync(new TestLogEntry("payload"), TestToken);
            await wal.CommitAsync(1L, TestToken);
            var first = wal.FlushAsync(TestToken);
            var second = wal.FlushAsync(TestToken);
            False(first.IsCompleted);
            False(second.IsCompleted);

            startup.Resume();
            await Task.WhenAll(first, second).WaitAsync(TestToken);
        }
        finally
        {
            startup.Resume();
        }
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public static async Task CallersKeepTheirCommittedTarget(bool manual)
    {
        using var passes = new FlushPasses();
        var startup = new PausedFlusherContext();
        await using var wal = startup.CreateLog(CreateOptions(manual ? Timeout.InfiniteTimeSpan : TimeSpan.Zero, passes.Tags));
        Task resumed = Task.CompletedTask;
        try
        {
            await wal.AppendAsync(new TestLogEntry("first"), TestToken);
            await wal.AppendAsync(new TestLogEntry("second"), TestToken);
            await wal.CommitAsync(1L, TestToken);
            Task first;
            if (manual)
            {
                first = Task.Run(() => wal.FlushAsync(TestToken), TestToken);
                await passes.First.Entered.Task.WaitAsync(TestToken);
            }
            else
            {
                first = wal.FlushAsync(TestToken);
            }

            var sameTarget = wal.FlushAsync(TestToken);
            False(first.IsCompleted);
            False(sameTarget.IsCompleted);

            resumed = Task.Run(startup.Resume, TestToken);
            await passes.First.Entered.Task.WaitAsync(TestToken);
            await wal.CommitAsync(2L, TestToken);
            var laterTarget = wal.FlushAsync(TestToken);
            False(laterTarget.IsCompleted);
            passes.First.Release();

            // Pass 1 has published its checkpoint and watermark before pass 2 enters.
            await passes.Second.Entered.Task.WaitAsync(TestToken);
            await Task.WhenAll(first, sameTarget).WaitAsync(TimeSpan.FromSeconds(10), TestToken);
            False(laterTarget.IsCompleted);
            passes.Second.Release();
            await laterTarget.WaitAsync(TestToken);
        }
        finally
        {
            passes.First.Release();
            passes.Second.Release();
            startup.Resume();
            await resumed;
        }
    }

    [Fact]
    public static async Task UncommittedAppendDoesNotExpandFlushTarget()
    {
        var options = CreateOptions(TimeSpan.FromDays(1));
        var startup = new PausedFlusherContext();
        await using (var wal = startup.CreateLog(options))
        {
            try
            {
                await wal.FlushAsync(TestToken);
                await wal.AppendAsync(new TestLogEntry("committed"), TestToken);
                await wal.AppendAsync(new TestLogEntry("uncommitted"), TestToken);
                await wal.CommitAsync(1L, TestToken);
                var flush = wal.FlushAsync(TestToken);
                False(flush.IsCompleted);
                startup.Resume();
                await flush.WaitAsync(TestToken);
                await wal.FlushAsync(TestToken);
                Equal(2L, wal.LastEntryIndex);
                Equal(1L, wal.LastCommittedEntryIndex);
            }
            finally
            {
                startup.Resume();
            }
        }

        await using var reopened = new WriteAheadLog(options, IStateMachine.CreateNoOp());
        Equal(1L, reopened.LastEntryIndex);
        Equal(1L, reopened.LastCommittedEntryIndex);
        using var reader = await reopened.ReadAsync(1L, 1L, TestToken);
        Equal("committed", await reader[0].ToStringAsync(Encoding.UTF8, token: TestToken));
    }

    [Fact]
    public static async Task RepeatedSingleEntryBoundaries()
    {
        using var passes = new FlushPasses();
        var startup = new PausedFlusherContext();
        await using var wal = startup.CreateLog(CreateOptions(TimeSpan.Zero, passes.Tags));
        Task resumed = Task.CompletedTask;
        try
        {
            await wal.AppendAsync(new TestLogEntry("first"), TestToken);
            await wal.CommitAsync(1L, TestToken);
            var first = wal.FlushAsync(TestToken);
            resumed = Task.Run(startup.Resume, TestToken);
            await passes.First.Entered.Task.WaitAsync(TestToken);
            False(first.IsCompleted);
            passes.First.Release();
            await first.WaitAsync(TestToken);
            await wal.FlushAsync(TestToken);

            await wal.AppendAsync(new TestLogEntry("second"), TestToken);
            await wal.CommitAsync(2L, TestToken);
            await passes.Second.Entered.Task.WaitAsync(TestToken);
            var second = wal.FlushAsync(TestToken);
            False(second.IsCompleted);
            passes.Second.Release();
            await second.WaitAsync(TestToken);
        }
        finally
        {
            passes.First.Release();
            passes.Second.Release();
            startup.Resume();
            await resumed;
        }
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public static async Task CancelingOneCallerDoesNotCompleteOthers(bool flushOnCommit)
    {
        using var cancellation = new CancellationTokenSource();
        var startup = new PausedFlusherContext();
        await using var wal = startup.CreateLog(CreateOptions(flushOnCommit ? TimeSpan.Zero : TimeSpan.FromDays(1)));
        try
        {
            await wal.AppendAsync(new TestLogEntry("payload"), TestToken);
            await wal.CommitAsync(1L, TestToken);
            var canceled = wal.FlushAsync(cancellation.Token);
            var first = wal.FlushAsync(TestToken);
            var second = wal.FlushAsync(TestToken);
            cancellation.Cancel();
            var error = await ThrowsAnyAsync<OperationCanceledException>(() => canceled.WaitAsync(TestToken));
            Equal(cancellation.Token, error.CancellationToken);
            False(first.IsCompleted);
            False(second.IsCompleted);
            startup.Resume();
            await Task.WhenAll(first, second).WaitAsync(TestToken);
        }
        finally
        {
            startup.Resume();
        }
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public static async Task DisposalFailsPendingCallers(bool flushOnCommit)
    {
        var startup = new PausedFlusherContext();
        var wal = startup.CreateLog(CreateOptions(flushOnCommit ? TimeSpan.Zero : TimeSpan.FromDays(1)));
        try
        {
            await wal.AppendAsync(new TestLogEntry("payload"), TestToken);
            await wal.CommitAsync(1L, TestToken);
            var first = wal.FlushAsync(TestToken);
            var second = wal.FlushAsync(TestToken);
            False(first.IsCompleted);
            False(second.IsCompleted);
            var disposal = wal.DisposeAsync().AsTask();
            await ThrowsAsync<ObjectDisposedException>(() => first.WaitAsync(TestToken));
            await ThrowsAsync<ObjectDisposedException>(() => second.WaitAsync(TestToken));
            startup.Resume();
            await disposal.WaitAsync(TestToken);
        }
        finally
        {
            startup.Resume();
            await wal.DisposeAsync();
        }
    }

    [Fact]
    public static async Task ConcurrentManualFlushesDoNotRegressCheckpoint()
    {
        using var passes = new FlushPasses();
        var options = CreateOptions(Timeout.InfiniteTimeSpan, passes.Tags);
        await using (var wal = new WriteAheadLog(options, IStateMachine.CreateNoOp()))
        {
            Task first = Task.CompletedTask, second = Task.CompletedTask;
            try
            {
                await wal.AppendAsync(new TestLogEntry("first"), TestToken);
                await wal.AppendAsync(new TestLogEntry("second"), TestToken);
                await wal.CommitAsync(1L, TestToken);
                first = Task.Run(() => wal.FlushAsync(TestToken), TestToken);
                await passes.First.Entered.Task.WaitAsync(TestToken);

                await wal.CommitAsync(2L, TestToken);
                var secondReturned = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
                second = Task.Run(() =>
                {
                    var flush = wal.FlushAsync(TestToken);
                    secondReturned.SetResult();
                    return flush;
                }, TestToken);

                // Force the newer checkpoint to finish first if concurrent passes are allowed.
                var next = await Task.WhenAny(secondReturned.Task, passes.Second.Entered.Task).WaitAsync(TestToken);
                if (next == passes.Second.Entered.Task)
                {
                    passes.Second.Release();
                    await second.WaitAsync(TestToken);
                    passes.First.Release();
                }
                else
                {
                    passes.First.Release();
                    await passes.Second.Entered.Task.WaitAsync(TestToken);
                    passes.Second.Release();
                }

                await Task.WhenAll(first, second).WaitAsync(TestToken);
            }
            finally
            {
                passes.First.Release();
                passes.Second.Release();
                await Task.WhenAll(first, second);
            }
        }

        await using var reopened = new WriteAheadLog(options, IStateMachine.CreateNoOp());
        Equal(2L, reopened.LastCommittedEntryIndex);
        Equal(2L, reopened.LastEntryIndex);
        using var reader = await reopened.ReadAsync(2L, 2L, TestToken);
        Equal("second", await reader[0].ToStringAsync(Encoding.UTF8, token: TestToken));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public static async Task PendingManualFlushLifecycle(bool dispose)
    {
        using var passes = new FlushPasses();
        using var cancellation = new CancellationTokenSource();
        await using var wal = new WriteAheadLog(CreateOptions(Timeout.InfiniteTimeSpan, passes.Tags), IStateMachine.CreateNoOp());
        try
        {
            await wal.AppendAsync(new TestLogEntry("payload"), TestToken);
            await wal.CommitAsync(1L, TestToken);
            var active = Task.Run(() => wal.FlushAsync(TestToken), TestToken);
            await passes.First.Entered.Task.WaitAsync(TestToken);
            var pending = wal.FlushAsync(cancellation.Token);
            var other = wal.FlushAsync(TestToken);
            False(pending.IsCompleted);
            False(other.IsCompleted);

            if (dispose)
            {
                var disposal = wal.DisposeAsync().AsTask();
                await ThrowsAsync<ObjectDisposedException>(() => pending.WaitAsync(TestToken));
                await ThrowsAsync<ObjectDisposedException>(() => other.WaitAsync(TestToken));
                False(disposal.IsCompleted);
                passes.First.Release();
                await ThrowsAsync<ObjectDisposedException>(() => active.WaitAsync(TestToken));
                await disposal.WaitAsync(TestToken);
            }
            else
            {
                cancellation.Cancel();
                var error = await ThrowsAnyAsync<OperationCanceledException>(() => pending.WaitAsync(TestToken));
                Equal(cancellation.Token, error.CancellationToken);
                False(active.IsCompleted);
                False(other.IsCompleted);
                passes.First.Release();
                await Task.WhenAll(active, other).WaitAsync(TestToken);
            }
        }
        finally
        {
            passes.First.Release();
            passes.Second.Release();
        }
    }

    [Fact]
    public static async Task CallersRacingWithPublicationDoNotMissCompletion()
    {
        using var passes = new FlushPasses();
        var startup = new PausedFlusherContext();
        await using var wal = startup.CreateLog(CreateOptions(TimeSpan.FromDays(1), passes.Tags));
        var start = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        Task resumed = Task.CompletedTask;
        try
        {
            await wal.AppendAsync(new TestLogEntry("payload"), TestToken);
            await wal.CommitAsync(1L, TestToken);
            resumed = Task.Run(startup.Resume, TestToken);
            await passes.First.Entered.Task.WaitAsync(TestToken);
            var callers = Enumerable.Range(0, 32).Select(_ => Task.Run(async () =>
            {
                await start.Task.WaitAsync(TestToken);
                await wal.FlushAsync(TestToken);
            }, TestToken)).ToArray();

            start.SetResult();
            passes.First.Release();
            await Task.WhenAll(callers).WaitAsync(TimeSpan.FromSeconds(10), TestToken);
        }
        finally
        {
            start.TrySetResult();
            passes.First.Release();
            passes.Second.Release();
            startup.Resume();
            await resumed;
        }
    }

    private static WriteAheadLog.Options CreateOptions(TimeSpan interval, TagList tags = default)
        => new()
        {
            Location = GetTempPath(),
            MemoryManagement = WriteAheadLog.MemoryManagementStrategy.PrivateMemory,
            FlushInterval = interval,
            MeasurementTags = tags,
        };

    private sealed class FlushPasses : IDisposable
    {
        private readonly string id = Guid.NewGuid().ToString();
        private readonly MeterListener listener = new();
        private int count;
        private volatile bool enabled;

        internal readonly Pass First = new(), Second = new();

        internal TagList Tags => new() { { "flush-test", id } };

        internal void Enable() => enabled = true;

        internal FlushPasses(bool initiallyEnabled = true)
        {
            enabled = initiallyEnabled;
            // This synchronous metric runs after target capture, before checkpoint persistence.
            listener.InstrumentPublished = (instrument, listener) =>
            {
                if (instrument.Meter.Name == "DotNext.IO.WriteAheadLog" && instrument.Name == "entries-flush-count")
                    listener.EnableMeasurementEvents(instrument);
            };
            listener.SetMeasurementEventCallback<long>((_, _, tags, _) =>
            {
                if (!enabled)
                    return;
                foreach (var tag in tags)
                {
                    if (tag.Key == "flush-test" && Equals(tag.Value, id))
                    {
                        switch (Interlocked.Increment(ref count))
                        {
                            case 1:
                                First.Enter();
                                break;
                            case 2:
                                Second.Enter();
                                break;
                        }
                        break;
                    }
                }
            });
            listener.Start();
        }

        public void Dispose()
        {
            First.Release();
            Second.Release();
            listener.Dispose();
            First.Dispose();
            Second.Dispose();
        }

        internal sealed class Pass : IDisposable
        {
            private readonly ManualResetEventSlim released = new();
            internal readonly TaskCompletionSource Entered = new(TaskCreationOptions.RunContinuationsAsynchronously);

            internal void Enter()
            {
                Entered.SetResult();
                released.Wait(TestToken);
            }

            internal void Release() => released.Set();

            public void Dispose() => released.Dispose();
        }
    }

    private sealed class PausedFlusherContext : SynchronizationContext
    {
        // Even the periodic worker yields once before its initial, untimed pass.
        private readonly ConcurrentQueue<(SendOrPostCallback Callback, object State)> callbacks = new();

        public override void Post(SendOrPostCallback d, object state)
            => callbacks.Enqueue((d, state));

        internal WriteAheadLog CreateLog(WriteAheadLog.Options options, IStateMachine machine = null)
        {
            var previous = Current;
            SetSynchronizationContext(this);
            try
            {
                return new(options, machine ?? IStateMachine.CreateNoOp());
            }
            finally
            {
                SetSynchronizationContext(previous);
            }

        }

        internal void Resume()
        {
            while (callbacks.TryDequeue(out var callback))
                callback.Callback(callback.State);
        }
    }

    private sealed class GatedFailureStateMachine(bool failApply) : IStateMachine
    {
        private readonly IStateMachine inner = IStateMachine.CreateNoOp(2L);
        internal readonly IOException Error = new("Injected state machine storage failure.");
        internal readonly TaskCompletionSource Entered = new(TaskCreationOptions.RunContinuationsAsynchronously);
        internal readonly TaskCompletionSource Release = new(TaskCreationOptions.RunContinuationsAsynchronously);

        public ISnapshot Snapshot => inner.Snapshot;

        public async ValueTask<long> ApplyAsync(LogEntry entry, CancellationToken token)
        {
            if (failApply)
                await FailAsync(token);
            return await inner.ApplyAsync(entry, token);
        }

        public async ValueTask ReclaimGarbageAsync(long watermark, CancellationToken token)
        {
            if (!failApply)
                await FailAsync(token);
        }

        private async Task FailAsync(CancellationToken token)
        {
            Entered.TrySetResult();
            await Release.Task.WaitAsync(token);
            throw Error;
        }
    }
}
