using System.Buffers.Binary;
using System.Collections.Concurrent;
using System.Diagnostics;
using System.Diagnostics.Metrics;
using System.IO.Hashing;
using System.Runtime.CompilerServices;
using System.Text;

namespace DotNext.Net.Cluster.Consensus.Raft.StateMachine;

using AsyncAutoResetEventSlim = Threading.AsyncAutoResetEventSlim;
using static IO.DataTransferObject;

[Collection(TestCollections.WriteAheadLog)]
public sealed class WriteAheadLogFlushTests : Test
{
    [Theory]
    [InlineData(false, false)]
    [InlineData(false, true)]
    [InlineData(true, false)]
    [InlineData(true, true)]
    public static async Task CheckpointFailureFailsExplicitRequests(bool flushOnCommit, bool subsequent)
    {
        var startup = new PausedFlusherContext();
        var options = CreateOptions(flushOnCommit ? TimeSpan.Zero : TimeSpan.FromDays(1));
        await using var wal = startup.CreateLog(options);
        using var failure = new CheckpointFailure(options.Location);
        using var cancellation = new CancellationTokenSource();
        Task[] pending = [];
        Task[] requests = [];
        try
        {
            await wal.AppendAsync(new TestLogEntry("durable append, unpersisted commit"), TestToken);
            await wal.CommitAsync(1L, TestToken);
            pending = [wal.FlushAsync(cancellation.Token), wal.FlushAsync(cancellation.Token)];
            All(pending, static request => False(request.IsCompleted));

            failure.Inject();
            startup.Resume();
            await FlusherTask(wal).WaitAsync(TestToken);

            // Worker termination plus its stored I/O error establishes the failed pass.
            var stored = await ThrowsAsync<WriteAheadLog.InternalException>(
                () => wal.WaitForApplyAsync(0L, TestToken).AsTask());
            IsAssignableFrom<IOException>(stored.InnerException);
            Equal(1L, wal.LastCommittedEntryIndex);
            True(File.Exists(Path.Combine(options.Location, "data", "0")));
            Equal(0L, ReadCommittedCheckpoint(options.Location));
            failure.AssertUnchanged();

            requests = subsequent ? new[] { wal.FlushAsync(cancellation.Token) } : pending;
            TestContext.Current.TestOutputHelper.WriteLine(
                $"Worker completed: {FlusherTask(wal).IsCompleted}; stored: {stored.InnerException.GetType().Name}; " +
                $"target: 1; durable committed index: 0; subsequent: {subsequent}; " +
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
            await Task.WhenAll(pending.Concat(requests)).ConfigureAwait(ConfigureAwaitOptions.SuppressThrowing);
        }
    }

    [UnsafeAccessor(UnsafeAccessorKind.Field, Name = "flusherTask")]
    private static extern ref Task FlusherTask(WriteAheadLog wal);

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public static async Task FlusherFailureStopsIdleApplierWithoutAnotherCommit(bool flushOnCommit)
    {
        var startup = new PausedFlusherContext();
        var options = CreateOptions(flushOnCommit ? TimeSpan.Zero : TimeSpan.FromDays(1));
        await using var wal = startup.CreateLog(options);
        using var failure = new CheckpointFailure(options.Location);
        try
        {
            await wal.AppendAsync(new TestLogEntry("durable append, unpersisted commit"), TestToken);
            await wal.CommitAsync(1L, TestToken);
            await wal.WaitForApplyAsync(1L, TestToken);
            AssertApplierIsWaiting(wal);

            failure.Inject();
            startup.Resume();
            await FlusherTask(wal).WaitAsync(TestToken);
            var stored = await ThrowsAsync<WriteAheadLog.InternalException>(() => wal.FlushAsync(TestToken));
            IsAssignableFrom<IOException>(stored.InnerException);
            True(FlusherTask(wal).IsCompletedSuccessfully);
            Equal(1L, wal.LastCommittedEntryIndex);
            Equal(1L, wal.LastAppliedIndex);
            True(File.Exists(Path.Combine(options.Location, "data", "0")));
            Equal(0L, ReadCommittedCheckpoint(options.Location));
            failure.AssertUnchanged();
            TestContext.Current.TestOutputHelper.WriteLine(
                $"Flusher terminated with {stored.InnerException.GetType().Name}; flush-on-commit: {flushOnCommit}; " +
                $"applier was parked; no new commits or disposal; applier completed: {ApplierTask(wal).IsCompleted}");

            await ApplierTask(wal).WaitAsync(TimeSpan.FromSeconds(2), TestToken);
            True(ApplierTask(wal).IsCompletedSuccessfully);
            var retained = await ThrowsAsync<WriteAheadLog.InternalException>(() => wal.FlushAsync(TestToken));
            Same(stored.InnerException, retained.InnerException);
        }
        finally
        {
            startup.Resume();
        }
    }

    [Fact]
    public static async Task CleanerFailureStopsIdleApplierWithoutAnotherCommit()
    {
        var machine = new GatedFailureStateMachine(failApply: false);
        var startup = new PausedFlusherContext();
        await using var wal = startup.CreateLog(CreateOptions(TimeSpan.Zero), machine);
        try
        {
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
            await wal.WaitForApplyAsync(4L, TestToken);
            AssertApplierIsWaiting(wal);

            machine.Release.TrySetResult();
            await cleanup.WaitAsync(TestToken);
            var stored = await ThrowsAsync<WriteAheadLog.InternalException>(() => wal.FlushAsync(TestToken));
            Same(machine.Error, stored.InnerException);
            True(cleanup.IsCompletedSuccessfully);
            Equal(4L, wal.LastCommittedEntryIndex);
            Equal(4L, wal.LastAppliedIndex);
            TestContext.Current.TestOutputHelper.WriteLine(
                $"Cleaner terminated with the injected storage error; applier was parked; " +
                $"no new commits or disposal; applier completed: {ApplierTask(wal).IsCompleted}");

            await ApplierTask(wal).WaitAsync(TimeSpan.FromSeconds(2), TestToken);
            True(ApplierTask(wal).IsCompletedSuccessfully);
            var retained = await ThrowsAsync<WriteAheadLog.InternalException>(() => wal.FlushAsync(TestToken));
            Same(machine.Error, retained.InnerException);
        }
        finally
        {
            machine.Release.TrySetResult();
            startup.Resume();
        }
    }

    private static void AssertApplierIsWaiting(WriteAheadLog wal)
    {
        // Read the trigger's CallbackAttachedState without signaling or replacing it.
        True(SpinWait.SpinUntil(() => Volatile.Read(ref TriggerState(ApplyTrigger(wal))) is 2,
            TimeSpan.FromSeconds(2)));
        False(ApplierTask(wal).IsCompleted);
    }

    [UnsafeAccessor(UnsafeAccessorKind.Field, Name = "applyTrigger")]
    private static extern ref AsyncAutoResetEventSlim ApplyTrigger(WriteAheadLog wal);

    [UnsafeAccessor(UnsafeAccessorKind.Field, Name = "state")]
    private static extern ref int TriggerState(AsyncAutoResetEventSlim trigger);

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public static async Task ApplierFailureStopsIdleFlusherWithoutAnotherCommit(bool flushOnCommit)
    {
        var options = CreateOptions(Timeout.InfiniteTimeSpan);
        await using (var seed = new WriteAheadLog(options, IStateMachine.CreateNoOp()))
        {
            await seed.AppendAsync(new TestLogEntry("persisted payload"), TestToken);
            await seed.CommitAsync(1L, TestToken);
            await seed.FlushAsync(TestToken);
        }

        var machine = new GatedFailureStateMachine(failApply: true);
        var startup = new PausedFlusherContext();
        await using var wal = startup.CreateLog(new()
        {
            Location = options.Location,
            MemoryManagement = options.MemoryManagement,
            FlushInterval = flushOnCommit ? TimeSpan.Zero : TimeSpan.FromDays(1),
        }, machine);
        try
        {
            await machine.Entered.Task.WaitAsync(TestToken);
            // Recovery already persisted the target. The initial callback has no I/O
            // to await, so Resume returns only after the flusher parks on its trigger.
            startup.Resume();
            False(FlusherTask(wal).IsCompleted);
            var completed = wal.FlushAsync(TestToken);
            await completed.WaitAsync(TestToken);

            machine.Release.TrySetResult();
            await ApplierTask(wal).WaitAsync(TestToken);
            var error = await ThrowsAsync<WriteAheadLog.InternalException>(() => wal.FlushAsync(TestToken));
            Same(machine.Error, error.InnerException);
            True(completed.IsCompletedSuccessfully);
            Equal(1L, wal.LastCommittedEntryIndex);
            TestContext.Current.TestOutputHelper.WriteLine(
                $"Applier failed; flush-on-commit: {flushOnCommit}; no new commits or disposal; " +
                $"flusher completed: {FlusherTask(wal).IsCompleted}");

            await FlusherTask(wal).WaitAsync(TimeSpan.FromSeconds(2), TestToken);
            True(FlusherTask(wal).IsCompletedSuccessfully);
        }
        finally
        {
            machine.Release.TrySetResult();
            startup.Resume();
        }
    }

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
        using var passes = new FlushPasses();
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

            // Append persistence must finish before holding the committed checkpoint pass.
            await wal.AppendAsync(new TestLogEntry("pending target"), TestToken);
            passes.Enable();
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

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public static async Task FirstWorkerFailureIsRetained(bool flusherFirst)
    {
        var machine = new GatedFailureStateMachine(failApply: true);
        var startup = new PausedFlusherContext();
        using var passes = new FlushPasses();
        var options = CreateOptions(TimeSpan.Zero, passes.Tags);
        await using var wal = startup.CreateLog(options, machine);
        using var failure = new CheckpointFailure(options.Location);
        Task resumed = Task.CompletedTask;
        try
        {
            await wal.AppendAsync(new TestLogEntry("payload"), TestToken);
            passes.Enable();
            await wal.CommitAsync(1L, TestToken);
            await machine.Entered.Task.WaitAsync(TestToken);
            failure.Inject();
            resumed = Task.Run(startup.Resume, TestToken);
            await passes.First.Entered.Task.WaitAsync(TestToken);

            if (flusherFirst)
                passes.First.Release();
            else
                machine.Release.TrySetResult();
            await (flusherFirst ? FlusherTask(wal) : ApplierTask(wal)).WaitAsync(TestToken);
            var first = await ThrowsAsync<WriteAheadLog.InternalException>(() => wal.FlushAsync(TestToken));
            if (flusherFirst)
                IsAssignableFrom<IOException>(first.InnerException);
            else
                Same(machine.Error, first.InnerException);

            passes.First.Release();
            machine.Release.TrySetResult();
            await Task.WhenAll(FlusherTask(wal), ApplierTask(wal)).WaitAsync(TestToken);
            var afterBoth = await ThrowsAsync<WriteAheadLog.InternalException>(() => wal.FlushAsync(TestToken));
            Same(first.InnerException, afterBoth.InnerException);
        }
        finally
        {
            passes.First.Release();
            passes.Second.Release();
            machine.Release.TrySetResult();
            startup.Resume();
            await resumed;
        }
    }

    [Fact]
    public static async Task FailedManualRequestDoesNotWaitForActivePass()
    {
        var machine = new GatedFailureStateMachine(failApply: true);
        using var passes = new FlushPasses();
        await using var wal = new WriteAheadLog(CreateOptions(Timeout.InfiniteTimeSpan, passes.Tags), machine);
        Task active = Task.CompletedTask, queued = Task.CompletedTask, later = Task.CompletedTask;
        using var cancellation = new CancellationTokenSource();
        try
        {
            await wal.AppendAsync(new TestLogEntry("payload"), TestToken);
            passes.Enable();
            await wal.CommitAsync(1L, TestToken);
            await machine.Entered.Task.WaitAsync(TestToken);
            active = Task.Run(() => wal.FlushAsync(TestToken), TestToken);
            await passes.First.Entered.Task.WaitAsync(TestToken);
            queued = wal.FlushAsync(cancellation.Token);
            False(queued.IsCompleted);
            machine.Release.TrySetResult();
            await ApplierTask(wal).WaitAsync(TestToken);

            later = wal.FlushAsync(cancellation.Token);
            var error = await ThrowsAsync<WriteAheadLog.InternalException>(
                () => later.WaitAsync(TimeSpan.FromSeconds(2), TestToken));
            Same(machine.Error, error.InnerException);
            False(active.IsCompleted);
            TestContext.Current.TestOutputHelper.WriteLine(
                $"Applier failed; active pass held; queued request completed: {queued.IsCompleted}");
            var queuedError = await ThrowsAsync<WriteAheadLog.InternalException>(
                () => queued.WaitAsync(TimeSpan.FromSeconds(2), TestToken));
            Same(machine.Error, queuedError.InnerException);
            False(active.IsCompleted);
            passes.First.Release();
            await ThrowsAsync<WriteAheadLog.InternalException>(() => active.WaitAsync(TestToken));
            await ThrowsAsync<WriteAheadLog.InternalException>(() => queued.WaitAsync(TestToken));
        }
        finally
        {
            machine.Release.TrySetResult();
            cancellation.Cancel();
            passes.First.Release();
            passes.Second.Release();
            await Task.WhenAll(active, queued, later).ConfigureAwait(ConfigureAwaitOptions.SuppressThrowing);
        }
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public static async Task ManualFailurePreservesCancellationAndDisposalOwnership(bool cancelFirst)
    {
        var machine = new GatedFailureStateMachine(failApply: true);
        using var passes = new FlushPasses();
        using var cancellation = new CancellationTokenSource();
        await using var wal = new WriteAheadLog(CreateOptions(Timeout.InfiniteTimeSpan, passes.Tags), machine);
        Task active = Task.CompletedTask, canceled = Task.CompletedTask, pending = Task.CompletedTask;
        Task disposal = Task.CompletedTask;
        try
        {
            await wal.AppendAsync(new TestLogEntry("payload"), TestToken);
            passes.Enable();
            await wal.CommitAsync(1L, TestToken);
            await machine.Entered.Task.WaitAsync(TestToken);
            active = Task.Run(() => wal.FlushAsync(TestToken), TestToken);
            await passes.First.Entered.Task.WaitAsync(TestToken);
            canceled = wal.FlushAsync(cancellation.Token);
            pending = wal.FlushAsync(TestToken);
            False(canceled.IsCompleted);
            False(pending.IsCompleted);
            if (cancelFirst)
            {
                cancellation.Cancel();
                var error = await ThrowsAnyAsync<OperationCanceledException>(
                    () => canceled.WaitAsync(TimeSpan.FromSeconds(2), TestToken));
                Equal(cancellation.Token, error.CancellationToken);
                False(pending.IsCompleted);
            }

            machine.Release.TrySetResult();
            await ApplierTask(wal).WaitAsync(TestToken);
            if (!cancelFirst)
            {
                cancellation.Cancel();
                var error = await ThrowsAsync<WriteAheadLog.InternalException>(
                    () => canceled.WaitAsync(TimeSpan.FromSeconds(2), TestToken));
                Same(machine.Error, error.InnerException);
            }
            var fatal = await ThrowsAsync<WriteAheadLog.InternalException>(
                () => pending.WaitAsync(TimeSpan.FromSeconds(2), TestToken));
            Same(machine.Error, fatal.InnerException);
            False(active.IsCompleted);

            disposal = wal.DisposeAsync().AsTask();
            False(disposal.IsCompleted);
            passes.First.Release();
            var activeError = await Record.ExceptionAsync(() => active.WaitAsync(TimeSpan.FromSeconds(2), TestToken));
            True(activeError is WriteAheadLog.InternalException or ObjectDisposedException);
            if (activeError is WriteAheadLog.InternalException activeFatal)
                Same(machine.Error, activeFatal.InnerException);
            await disposal.WaitAsync(TimeSpan.FromSeconds(2), TestToken);
        }
        finally
        {
            machine.Release.TrySetResult();
            cancellation.Cancel();
            passes.First.Release();
            passes.Second.Release();
            await Task.WhenAll(active, canceled, pending).ConfigureAwait(ConfigureAwaitOptions.SuppressThrowing);
            await disposal.WaitAsync(TestToken);
        }
    }

    [Theory]
    [InlineData(0)]
    [InlineData(1)]
    [InlineData(2)]
    public static async Task DisposedFlushCannotReportSuccess(int mode)
    {
        var interval = mode switch { 0 => TimeSpan.Zero, 1 => TimeSpan.FromDays(1), _ => Timeout.InfiniteTimeSpan };
        var wal = new WriteAheadLog(CreateOptions(interval), IStateMachine.CreateNoOp());
        await wal.FlushAsync(TestToken);
        await wal.DisposeAsync();
        await ThrowsAsync<ObjectDisposedException>(() => wal.FlushAsync(TestToken));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public static async Task FailurePublicationRacesWithCallers(bool flushOnCommit)
    {
        var startup = new PausedFlusherContext();
        var options = CreateOptions(flushOnCommit ? TimeSpan.Zero : TimeSpan.FromDays(1));
        await using var wal = startup.CreateLog(options);
        using var failure = new CheckpointFailure(options.Location);
        using var cancellation = new CancellationTokenSource();
        var start = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        Task[] callers = [];
        try
        {
            await wal.AppendAsync(new TestLogEntry("payload"), TestToken);
            await wal.CommitAsync(1L, TestToken);
            var canceled = wal.FlushAsync(cancellation.Token);
            var pending = wal.FlushAsync(TestToken);
            cancellation.Cancel();
            var canceledError = await ThrowsAsync<OperationCanceledException>(() => canceled.WaitAsync(TestToken));
            Equal(cancellation.Token, canceledError.CancellationToken);
            False(pending.IsCompleted);
            failure.Inject();
            callers = Enumerable.Range(0, 32).Select(_ => Task.Run(async () =>
            {
                await start.Task.WaitAsync(TestToken);
                await ThrowsAsync<WriteAheadLog.InternalException>(
                    () => wal.FlushAsync(TestToken).WaitAsync(TimeSpan.FromSeconds(10), TestToken));
            }, TestToken)).ToArray();
            start.SetResult();
            startup.Resume();
            await FlusherTask(wal).WaitAsync(TestToken);
            var error = await ThrowsAsync<WriteAheadLog.InternalException>(() => pending.WaitAsync(TestToken));
            IsAssignableFrom<IOException>(error.InnerException);
            var later = await ThrowsAsync<WriteAheadLog.InternalException>(() => wal.FlushAsync(TestToken));
            Same(error.InnerException, later.InnerException);
            await Task.WhenAll(callers).WaitAsync(TestToken);
        }
        finally
        {
            start.TrySetResult();
            startup.Resume();
            await Task.WhenAll(callers).ConfigureAwait(ConfigureAwaitOptions.SuppressThrowing);
        }
    }

    [Theory]
    [InlineData(false, 0)]
    [InlineData(false, 1)]
    [InlineData(false, 2)]
    [InlineData(true, 0)]
    [InlineData(true, 1)]
    [InlineData(true, 2)]
    public static async Task FailureAndDisposalCompleteEveryCaller(bool flushOnCommit, int order)
    {
        var startup = new PausedFlusherContext();
        using var passes = new FlushPasses();
        var options = CreateOptions(flushOnCommit ? TimeSpan.Zero : TimeSpan.FromDays(1), passes.Tags);
        var wal = startup.CreateLog(options);
        using var failure = new CheckpointFailure(options.Location);
        Task resumed = Task.CompletedTask, disposal = Task.CompletedTask;
        try
        {
            await wal.AppendAsync(new TestLogEntry("payload"), TestToken);
            passes.Enable();
            await wal.CommitAsync(1L, TestToken);
            var callers = Enumerable.Range(0, 8).Select(_ => wal.FlushAsync(TestToken)).ToArray();
            All(callers, static caller => False(caller.IsCompleted));
            failure.Inject();
            resumed = Task.Run(startup.Resume, TestToken);
            await passes.First.Entered.Task.WaitAsync(TestToken);
            if (order == 0)
            {
                disposal = wal.DisposeAsync().AsTask();
                foreach (var caller in callers)
                    await ThrowsAsync<ObjectDisposedException>(() => caller.WaitAsync(TestToken));
                False(disposal.IsCompleted);
                passes.First.Release();
            }
            else if (order == 1)
            {
                passes.First.Release();
                await FlusherTask(wal).WaitAsync(TestToken);
                foreach (var caller in callers)
                    await ThrowsAsync<WriteAheadLog.InternalException>(() => caller.WaitAsync(TestToken));
                disposal = wal.DisposeAsync().AsTask();
            }
            else
            {
                disposal = Task.Run(async () => await wal.DisposeAsync(), TestToken);
                passes.First.Release();
                foreach (var caller in callers)
                {
                    var error = await Record.ExceptionAsync(() => caller.WaitAsync(TimeSpan.FromSeconds(10), TestToken));
                    True(error is ObjectDisposedException or WriteAheadLog.InternalException);
                    if (error is WriteAheadLog.InternalException fatal)
                        IsAssignableFrom<IOException>(fatal.InnerException);
                }
            }
            await disposal.WaitAsync(TestToken);
            await ThrowsAsync<ObjectDisposedException>(() => wal.FlushAsync(TestToken));
        }
        finally
        {
            passes.First.Release();
            passes.Second.Release();
            startup.Resume();
            await resumed;
            await disposal;
            await wal.DisposeAsync();
        }
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public static async Task FatalErrorRejectsAlreadyPersistedTarget(bool manual)
    {
        var machine = new GatedFailureStateMachine(failApply: true);
        await using var wal = new WriteAheadLog(
            CreateOptions(manual ? Timeout.InfiniteTimeSpan : TimeSpan.Zero), machine);
        try
        {
            await wal.AppendAsync(new TestLogEntry("persisted"), TestToken);
            await wal.CommitAsync(1L, TestToken);
            await machine.Entered.Task.WaitAsync(TestToken);
            var completed = wal.FlushAsync(TestToken);
            await completed.WaitAsync(TestToken);
            machine.Release.TrySetResult();
            await ApplierTask(wal).WaitAsync(TestToken);
            True(completed.IsCompletedSuccessfully);
            var error = await ThrowsAsync<WriteAheadLog.InternalException>(() => wal.FlushAsync(TestToken));
            Same(machine.Error, error.InnerException);
        }
        finally
        {
            machine.Release.TrySetResult();
        }
    }

    [Fact]
    public static async Task FailedLaterCommitPreservesDurableAppendedTail()
    {
        var options = CreateOptions(TimeSpan.Zero);
        await using (var wal = new WriteAheadLog(options, IStateMachine.CreateNoOp()))
        {
            using var failure = new CheckpointFailure(options.Location);
            await wal.AppendAsync(new TestLogEntry("durable prefix"), TestToken);
            await wal.CommitAsync(1L, TestToken);
            var completed = wal.FlushAsync(TestToken);
            await completed.WaitAsync(TestToken);
            await wal.AppendAsync(new TestLogEntry("durable uncommitted tail"), TestToken);
            failure.Inject();
            await wal.CommitAsync(2L, TestToken);
            var failed = wal.FlushAsync(TestToken);
            await FlusherTask(wal).WaitAsync(TestToken);
            var error = await ThrowsAsync<WriteAheadLog.InternalException>(() => failed.WaitAsync(TestToken));
            IsAssignableFrom<IOException>(error.InnerException);
            True(completed.IsCompletedSuccessfully);
            Equal(2L, wal.LastCommittedEntryIndex);
            Equal(1L, ReadCommittedCheckpoint(options.Location));
            failure.AssertUnchanged();
        }

        await using var reopened = new WriteAheadLog(options, IStateMachine.CreateNoOp());
        Equal(2L, reopened.LastEntryIndex);
        Equal(1L, reopened.LastCommittedEntryIndex);
        using var reader = await reopened.ReadAsync(1L, 2L, TestToken);
        Equal("durable prefix", await reader[0].ToStringAsync(Encoding.UTF8, token: TestToken));
        Equal("durable uncommitted tail", await reader[1].ToStringAsync(Encoding.UTF8, token: TestToken));
    }

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
            passes.Enable();
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
        Equal(2L, reopened.LastEntryIndex);
        Equal(1L, reopened.LastCommittedEntryIndex);
        using var reader = await reopened.ReadAsync(1L, 2L, TestToken);
        Equal("committed", await reader[0].ToStringAsync(Encoding.UTF8, token: TestToken));
        Equal("uncommitted", await reader[1].ToStringAsync(Encoding.UTF8, token: TestToken));
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
            passes.Enable();
            await wal.CommitAsync(1L, TestToken);
            var first = wal.FlushAsync(TestToken);
            resumed = Task.Run(startup.Resume, TestToken);
            await passes.First.Entered.Task.WaitAsync(TestToken);
            False(first.IsCompleted);
            passes.First.Release();
            await first.WaitAsync(TestToken);
            await wal.FlushAsync(TestToken);

            passes.Disable();
            await wal.AppendAsync(new TestLogEntry("second"), TestToken);
            passes.Enable();
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
                passes.Enable();
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
            passes.Enable();
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
            passes.Enable();
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

    private static byte[] ReadCheckpointBytes(string location)
    {
        using var handle = File.OpenHandle(
            Path.Combine(location, "checkpoint"),
            access: FileAccess.Read,
            share: FileShare.ReadWrite | FileShare.Delete);
        var content = new byte[checked((int)RandomAccess.GetLength(handle))];
        for (var offset = 0; offset < content.Length;)
        {
            var count = RandomAccess.Read(handle, content.AsSpan(offset), offset);
            True(count > 0);
            offset += count;
        }

        return content;
    }

    internal static long ReadCommittedCheckpoint(string location)
    {
        ReadOnlySpan<byte> content = ReadCheckpointBytes(location);
        switch (content.Length)
        {
            case 0:
                return 0L;
            case sizeof(long):
                return BinaryPrimitives.ReadInt64LittleEndian(content);
            case sizeof(uint) + sizeof(long):
                Equal(0U, BinaryPrimitives.ReadUInt32LittleEndian(content));
                return BinaryPrimitives.ReadInt64LittleEndian(content.Slice(sizeof(uint)));
        }

        Equal(1U, BinaryPrimitives.ReadUInt32LittleEndian(content));
        var blockSize = BinaryPrimitives.ReadInt32LittleEndian(content.Slice(12));
        Equal(blockSize * 3, content.Length);
        var first = ReadSlot(content.Slice(blockSize, blockSize));
        var second = ReadSlot(content.Slice(blockSize * 2, blockSize));
        return first.Generation > second.Generation ? first.CommittedIndex : second.CommittedIndex;

        static (long Generation, long CommittedIndex) ReadSlot(ReadOnlySpan<byte> slot)
        {
            Equal(1U, BinaryPrimitives.ReadUInt32LittleEndian(slot));
            Equal(Crc64.HashToUInt64(slot[..^sizeof(ulong)]),
                BinaryPrimitives.ReadUInt64LittleEndian(slot[^sizeof(ulong)..]));
            return (BinaryPrimitives.ReadInt64LittleEndian(slot.Slice(64)),
                BinaryPrimitives.ReadInt64LittleEndian(slot.Slice(32)));
        }
    }

    private sealed class CheckpointFailure(string location) : IDisposable
    {
        private readonly string pendingPath = Path.Combine(location, "checkpoint.pending");
        private byte[] before = [];

        internal void Inject()
        {
            before = ReadCheckpointBytes(location);
            // Appends already persisted the pages. Fail publication of the next commit's intent
            // before either checkpoint slot can change, retaining the actual filesystem exception.
            Directory.CreateDirectory(pendingPath);
        }

        internal void AssertUnchanged() => Equal(before, ReadCheckpointBytes(location));

        public void Dispose()
        {
            if (Directory.Exists(pendingPath))
                Directory.Delete(pendingPath);
        }
    }

    internal sealed class FlushPasses : IDisposable
    {
        private readonly string id = Guid.NewGuid().ToString();
        private readonly MeterListener listener = new();
        private int count;
        private volatile bool enabled;

        internal readonly Pass First = new(), Second = new();

        internal TagList Tags => new() { { "flush-test", id } };

        internal void Enable() => enabled = true;

        internal void Disable() => enabled = false;

        internal FlushPasses()
        {
            // Append also emits this synchronous metric. Arm the gate after appends complete
            // to hold only the committed flush pass, after target capture and before checkpoint persistence.
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
