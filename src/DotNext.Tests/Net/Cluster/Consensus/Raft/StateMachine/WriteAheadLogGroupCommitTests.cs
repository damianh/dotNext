using System.Collections.Concurrent;
using System.Diagnostics;
using System.Diagnostics.Metrics;
using System.Text;

namespace DotNext.Net.Cluster.Consensus.Raft.StateMachine;

using Buffers.Binary;
using IO;

// Group commit of concurrent buffered appends (#125).
[Collection(TestCollections.WriteAheadLog)]
public sealed class WriteAheadLogGroupCommitTests : Test
{
    internal const int Concurrency = 8;

    [Fact(Timeout = TestTimeouts.Default)]
    public static async Task ConcurrentAppendsShareOnePersistCycle()
    {
        using var cycles = new AppendCycles();
        await using var wal = new WriteAheadLog(CreateOptions(cycles.Tags), IStateMachine.CreateNoOp());

        var indices = await AppendGroupAsync(wal, cycles);

        Equal(Enumerable.Range(1, Concurrency).Select(static i => (long)i), indices);
        Equal(Concurrency, wal.LastEntryIndex);
        await AssertGroupAsync(wal, 1L);

        // One cycle for the first append, one shared cycle for the appends that waited behind it.
        Equal(2, cycles.Count);
    }

    [Fact(Timeout = TestTimeouts.Default)]
    public static async Task AppendCanceledWhileQueuedIsNotWritten()
    {
        using var cycles = new AppendCycles();
        await using var wal = new WriteAheadLog(CreateOptions(cycles.Tags), IStateMachine.CreateNoOp());
        using var cts = CancellationTokenSource.CreateLinkedTokenSource(TestToken);
        var gate = cycles.HoldCycle(1);
        var first = Task.Run(async () => await wal.AppendAsync(Entry(1), TestToken), TestToken);
        await gate.Entered.WaitAsync(DefaultTimeout, TestToken);

        var canceled = wal.AppendAsync(Entry(2), cts.Token).AsTask();
        var second = wal.AppendAsync(Entry(3), TestToken).AsTask();
        var third = wal.AppendAsync(Entry(4), TestToken).AsTask();
        await cts.CancelAsync();

        // The caller observes its cancellation without waiting for the running cycle.
        await ThrowsAnyAsync<OperationCanceledException>(() => canceled.WaitAsync(DefaultTimeout, TestToken));
        False(second.IsCompleted);
        gate.Release();

        Equal(1L, await first);
        Equal(2L, await second);
        Equal(3L, await third);
        Equal(3L, wal.LastEntryIndex);
        await AssertContentAsync(wal, 1L, "entry 1", "entry 3", "entry 4");
        Equal(2, cycles.Count);
    }

    [Fact(Timeout = TestTimeouts.Default)]
    public static async Task AppendCanceledAfterItsCycleStartsCompletes()
    {
        using var cycles = new AppendCycles();
        await using var wal = new WriteAheadLog(CreateOptions(cycles.Tags), IStateMachine.CreateNoOp());
        using var cts = CancellationTokenSource.CreateLinkedTokenSource(TestToken);
        var firstGate = cycles.HoldCycle(1);
        var batchGate = cycles.HoldCycle(2);
        var first = Task.Run(async () => await wal.AppendAsync(Entry(1), TestToken), TestToken);
        await firstGate.Entered.WaitAsync(DefaultTimeout, TestToken);

        var canceled = wal.AppendAsync(Entry(2), cts.Token).AsTask();
        var other = wal.AppendAsync(Entry(3), TestToken).AsTask();
        firstGate.Release();

        // The entry is written and its cycle is running: cancellation no longer applies.
        await batchGate.Entered.WaitAsync(DefaultTimeout, TestToken);
        await cts.CancelAsync();
        False(canceled.IsCompleted);
        batchGate.Release();

        Equal(1L, await first);
        Equal(2L, await canceled);
        Equal(3L, await other);
        Equal(3L, wal.LastEntryIndex);
        await AssertContentAsync(wal, 1L, "entry 1", "entry 2", "entry 3");
    }

    [Fact(Timeout = TestTimeouts.Default)]
    public static async Task FailedCycleFaultsEveryAppendItCovers()
    {
        using var cycles = new AppendCycles();
        var options = CreateOptions(cycles.Tags);
        var pending = Path.Combine(options.Location, "checkpoint.pending");
        await using (var wal = new WriteAheadLog(options, IStateMachine.CreateNoOp()))
        {
            // The first cycle upgrades the checkpoint; the next ones publish an intent file.
            Equal(1L, await wal.AppendAsync(Entry(1), TestToken));
            var gate = cycles.HoldCycle(2, WriteAheadLog.CheckpointCommitPhase);
            var first = Task.Run(async () => await wal.AppendAsync(Entry(2), TestToken), TestToken);
            await gate.Entered.WaitAsync(DefaultTimeout, TestToken);
            var batch = Enumerable.Range(3, Concurrency - 1)
                .Select(i => wal.AppendAsync(Entry(i), TestToken).AsTask())
                .ToArray();

            // Fail the intent publication of the shared cycle.
            Directory.CreateDirectory(pending);
            gate.Release();

            Equal(2L, await first);
            var errors = new Exception[batch.Length];
            for (var i = 0; i < batch.Length; i++)
                errors[i] = await ThrowsAnyAsync<Exception>(() => batch[i].WaitAsync(DefaultTimeout, TestToken));

            // One failed cycle: every covered caller observes its failure, none is acknowledged.
            IsAssignableFrom<IOException>(errors[0]);
            All(errors, e => Same(errors[0], e));
            Equal(2L, wal.LastEntryIndex);
            Equal(3, cycles.Count);

            var later = await ThrowsAsync<WriteAheadLog.InternalException>(() => wal.AppendAsync(Entry(99), TestToken).AsTask());
            IsAssignableFrom<IOException>(later.InnerException);
        }

        Directory.Delete(pending);
        await using var reopened = new WriteAheadLog(options, IStateMachine.CreateNoOp());
        await reopened.InitializeAsync(TestToken);
        Equal(2L, reopened.LastEntryIndex);
        await AssertContentAsync(reopened, 1L, "entry 1", "entry 2");
        Equal(3L, await reopened.AppendAsync(Entry(3), TestToken));
    }

    [Fact(Timeout = TestTimeouts.Default)]
    public static async Task TermGuardFaultsOnlyTheStaleProposal()
    {
        using var cycles = new AppendCycles();
        await using var wal = new WriteAheadLog(CreateOptions(cycles.Tags), IStateMachine.CreateNoOp());
        await ((IPersistentState)wal).UpdateTermAsync(2L, resetLastVote: false, TestToken);
        ITermGuardedAuditTrail guarded = wal;
        var gate = cycles.HoldCycle(1);
        var first = Task.Run(async () => await guarded.AppendInCurrentTermAsync(Entry(1, 2L), TestToken), TestToken);
        await gate.Entered.WaitAsync(DefaultTimeout, TestToken);

        var current = guarded.AppendInCurrentTermAsync(Entry(2, 2L), TestToken).AsTask();
        var stale = guarded.AppendInCurrentTermAsync(Entry(3, 1L), TestToken).AsTask();
        var next = guarded.AppendInCurrentTermAsync(Entry(4, 2L), TestToken).AsTask();
        gate.Release();

        Equal(1L, await first);
        Equal(2L, await current);
        await ThrowsAsync<NotLeaderException>(() => stale);
        Equal(3L, await next);
        Equal(3L, wal.LastEntryIndex);
        await AssertContentAsync(wal, 1L, "entry 1", "entry 2", "entry 4");
        Equal(2, cycles.Count);
    }

    // Finding 10: an overwrite upgrades the append lock. It must not deadlock with the committer.
    [Fact(Timeout = TestTimeouts.Default)]
    public static async Task OverwriteQueuedBehindGroupCommitDoesNotDeadlock()
    {
        using var cycles = new AppendCycles();
        await using var wal = new WriteAheadLog(CreateOptions(cycles.Tags), IStateMachine.CreateNoOp());
        Equal(1L, await wal.AppendAsync(Entry(1, 1L), TestToken));
        Equal(2L, await wal.AppendAsync(Entry(2, 2L), TestToken));
        var gate = cycles.HoldCycle(3);
        var append = Task.Run(async () => await wal.AppendAsync(Entry(3, 3L), TestToken), TestToken);
        await gate.Entered.WaitAsync(DefaultTimeout, TestToken);

        var overwrite = wal.AppendAsync(Entry(4, 4L), 2L, TestToken).AsTask();
        var tail = wal.AppendAsync(Entry(5, 5L), TestToken).AsTask();
        False(overwrite.IsCompleted);
        False(tail.IsCompleted);
        gate.Release();

        await Task.WhenAll(append, overwrite, tail).WaitAsync(DefaultTimeout, TestToken);
        Equal(3L, await append);
        Equal(3L, await tail);
        Equal(3L, wal.LastEntryIndex);
        await AssertContentAsync(wal, 1L, "entry 1", "entry 4", "entry 5");
        await AssertTermsAsync(wal, startIndex: 1L, 1L, 4L, 5L);
    }

    [Fact(Timeout = TestTimeouts.Default)]
    public static async Task AppendsQueuedBehindOverwriteShareOneCycle()
    {
        using var cycles = new AppendCycles();
        await using var wal = new WriteAheadLog(CreateOptions(cycles.Tags), IStateMachine.CreateNoOp());
        Equal(1L, await wal.AppendAsync(Entry(1, 1L), TestToken));
        Equal(2L, await wal.AppendAsync(Entry(2, 1L), TestToken));
        var gate = cycles.HoldCycle(3);
        var overwrite = Task.Run(async () => await wal.AppendAsync(Entry(3, 2L), 2L, TestToken), TestToken);
        await gate.Entered.WaitAsync(DefaultTimeout, TestToken);

        var appends = new[]
        {
            wal.AppendAsync(Entry(4, 2L), TestToken).AsTask(),
            wal.AppendAsync(Entry(5, 2L), TestToken).AsTask(),
        };
        gate.Release();

        await overwrite.WaitAsync(DefaultTimeout, TestToken);
        var indices = await Task.WhenAll(appends).WaitAsync(DefaultTimeout, TestToken);
        Equal(new[] { 3L, 4L }, indices);
        Equal(4L, wal.LastEntryIndex);
        await AssertContentAsync(wal, 1L, "entry 1", "entry 3", "entry 4", "entry 5");
        Equal(4, cycles.Count);
    }

    // Every buffered kind joins the group; an unbuffered entry keeps its own cycle.
    [Fact(Timeout = TestTimeouts.Default)]
    public static async Task BufferedEntryKindsShareTheCycle()
    {
        using var cycles = new AppendCycles();
        await using var wal = new WriteAheadLog(CreateOptions(cycles.Tags), IStateMachine.CreateNoOp());
        var gate = cycles.HoldCycle(1);
        var first = Task.Run(async () => await wal.AppendAsync(Entry(1), TestToken), TestToken);
        await gate.Entered.WaitAsync(DefaultTimeout, TestToken);

        var empty = wal.AppendAsync(new EmptyLogEntry { Term = 0L }, TestToken).AsTask();
        var formatted = wal.AppendAsync(new BinaryLogEntry<Blittable<long>> { Content = new() { Value = 42L }, Term = 0L }, TestToken).AsTask();
        var unbuffered = wal.AppendAsync(new TestLogEntry("unbuffered"), TestToken).AsTask();
        var last = wal.AppendAsync(Entry(5), TestToken).AsTask();
        gate.Release();

        long[] indices = [await first, await empty, await formatted, await unbuffered, await last];
        Equal(Enumerable.Range(1, 5).Select(static i => (long)i), indices.Order());
        Equal(5L, wal.LastEntryIndex);
        Equal(3, cycles.Count);

        using var entries = await wal.ReadAsync(1L, 5L, TestToken);
        Equal("entry 1", await entries[(int)indices[0] - 1].ToStringAsync(Encoding.UTF8, token: TestToken));
        Empty(await entries[(int)indices[1] - 1].ToStringAsync(Encoding.UTF8, token: TestToken));
        Equal(42L, (await entries[(int)indices[2] - 1].TransformAsync(
            static (reader, token) => reader.ReadAsync<Blittable<long>>(token), TestToken)).Value);
        Equal("unbuffered", await entries[(int)indices[3] - 1].ToStringAsync(Encoding.UTF8, token: TestToken));
        Equal("entry 5", await entries[(int)indices[4] - 1].ToStringAsync(Encoding.UTF8, token: TestToken));
    }

    // The cluster activates a configuration after its append completes, so a configuration entry is not grouped:
    // the direct path resumes its caller inline, before a replication round can take the tail with the old membership.
    [Fact(Timeout = TestTimeouts.Default)]
    public static async Task ConfigurationEntryKeepsItsOwnCycle()
    {
        using var cycles = new AppendCycles();
        await using var wal = new WriteAheadLog(CreateOptions(cycles.Tags), IStateMachine.CreateNoOp());
        var gate = cycles.HoldCycle(1);
        var first = Task.Run(async () => await wal.AppendAsync(Entry(1), TestToken), TestToken);
        await gate.Entered.WaitAsync(DefaultTimeout, TestToken);

        var before = wal.AppendAsync(Entry(2), TestToken).AsTask();
        var configuration = wal.AppendAsync(
            new BinaryLogEntry { Term = 0L, Content = Encoding.UTF8.GetBytes("configuration"), IsConfiguration = true },
            TestToken).AsTask();
        var after = new[]
        {
            wal.AppendAsync(Entry(4), TestToken).AsTask(),
            wal.AppendAsync(Entry(5), TestToken).AsTask(),
        };
        gate.Release();

        Equal(1L, await first);
        long[] indices = [await before, await configuration, .. await Task.WhenAll(after).WaitAsync(DefaultTimeout, TestToken)];
        Equal(new[] { 2L, 3L, 4L, 5L }, indices.Order());
        Equal(5L, wal.LastEntryIndex);

        // One cycle for the first append, one for the configuration, one shared by the other three.
        Equal(3, cycles.Count);
        using var entries = await wal.ReadAsync(1L, 5L, TestToken);
        var configurationEntry = entries[(int)indices[1] - 1];
        True(configurationEntry.IsConfiguration);
        Equal("configuration", await configurationEntry.ToStringAsync(Encoding.UTF8, token: TestToken));
    }

    // Options.Allocator may return an owner whose disposal throws: every request of the batch is still settled.
    [Fact(Timeout = TestTimeouts.Default)]
    public static async Task ThrowingBufferReleaseDoesNotStrandTheBatch()
    {
        using var cycles = new AppendCycles();
        var throwing = true;
        var options = CreateOptions(cycles.Tags, length => Volatile.Read(in throwing)
            ? new(static length => new ThrowingOwner(length), length)
            : new(new byte[length]));

        await using var wal = new WriteAheadLog(options, IStateMachine.CreateNoOp());
        var gate = cycles.HoldCycle(1);
        var first = Task.Run(async () => await wal.AppendAsync(Entry(1), TestToken), TestToken);
        await gate.Entered.WaitAsync(DefaultTimeout, TestToken);

        var formatted = new[]
        {
            wal.AppendAsync(new BinaryLogEntry<Blittable<long>> { Content = new() { Value = 2L }, Term = 0L }, TestToken).AsTask(),
            wal.AppendAsync(new BinaryLogEntry<Blittable<long>> { Content = new() { Value = 3L }, Term = 0L }, TestToken).AsTask(),
        };
        var last = wal.AppendAsync(Entry(4), TestToken).AsTask();
        gate.Release();

        Equal(1L, await first);
        Equal(new[] { 2L, 3L }, await Task.WhenAll(formatted).WaitAsync(DefaultTimeout, TestToken));
        Equal(4L, await last.WaitAsync(DefaultTimeout, TestToken));
        Volatile.Write(ref throwing, false);
        Equal(4L, wal.LastEntryIndex);
        Equal(5L, await wal.AppendAsync(Entry(5), TestToken));
    }

    private sealed class ThrowingOwner(int length) : System.Buffers.IMemoryOwner<byte>
    {
        public Memory<byte> Memory { get; } = new byte[length];

        public void Dispose() => throw new InvalidOperationException("release failure");
    }

    // A rejected request releases its buffer after the committer has released the WAL locks,
    // so an owner that re-enters the WAL from Dispose cannot block the committer.
    [Fact(Timeout = TestTimeouts.Default)]
    public static async Task RejectedRequestReleasesItsBufferOutsideTheLocks()
    {
        using var cycles = new AppendCycles();
        WriteAheadLog wal = null;
        Task<long> probe = null;
        var armed = false;
        var probeCompleted = false;
        var options = CreateOptions(cycles.Tags, length => Volatile.Read(in armed)
            ? new(_ => new CallbackOwner(length, OnRelease), length)
            : new(new byte[length]));

        await using (wal = new WriteAheadLog(options, IStateMachine.CreateNoOp()))
        {
            await ((IPersistentState)wal).UpdateTermAsync(2L, resetLastVote: false, TestToken);
            ITermGuardedAuditTrail guarded = wal;
            var gate = cycles.HoldCycle(1);
            var first = Task.Run(async () => await guarded.AppendInCurrentTermAsync(Entry(1, 2L), TestToken), TestToken);
            await gate.Entered.WaitAsync(DefaultTimeout, TestToken);

            Volatile.Write(ref armed, true);
            var stale = guarded.AppendInCurrentTermAsync(new BinaryLogEntry<Blittable<long>> { Content = new() { Value = 2L }, Term = 1L }, TestToken).AsTask();
            Volatile.Write(ref armed, false);
            var next = guarded.AppendInCurrentTermAsync(Entry(3, 2L), TestToken).AsTask();
            gate.Release();

            Equal(1L, await first);
            await ThrowsAsync<NotLeaderException>(() => stale.WaitAsync(DefaultTimeout, TestToken));
            Equal(2L, await next.WaitAsync(DefaultTimeout, TestToken));
            NotNull(probe);
            True(Volatile.Read(in probeCompleted));
            Equal(3L, await probe);
        }

        void OnRelease()
        {
            // An unbuffered append needs the append and persistence locks.
            probe = Task.Run(async () => await wal.AppendAsync(
                new BinaryLogEntry { Term = 2L, Content = Encoding.UTF8.GetBytes("probe"), IsConfiguration = true },
                TestToken), TestToken);
            Volatile.Write(ref probeCompleted, probe.Wait(TimeSpan.FromSeconds(5)));
        }
    }

    private sealed class CallbackOwner(int length, Action onDispose) : System.Buffers.IMemoryOwner<byte>
    {
        public Memory<byte> Memory { get; } = new byte[length];

        public void Dispose() => onDispose();
    }

    [Fact(Timeout = TestTimeouts.Default)]
    public static async Task DisposalFailsQueuedAppends()
    {
        using var cycles = new AppendCycles();
        var wal = new WriteAheadLog(CreateOptions(cycles.Tags), IStateMachine.CreateNoOp());
        var gate = cycles.HoldCycle(1);
        var first = Task.Run(async () => await wal.AppendAsync(Entry(1), TestToken), TestToken);
        await gate.Entered.WaitAsync(DefaultTimeout, TestToken);
        var queued = wal.AppendAsync(Entry(2), TestToken).AsTask();

        var disposal = wal.DisposeAsync().AsTask();
        gate.Release();
        await disposal.WaitAsync(DefaultTimeout, TestToken);

        Equal(1L, await first);
        await ThrowsAsync<ObjectDisposedException>(() => queued.WaitAsync(DefaultTimeout, TestToken));
        await ThrowsAsync<ObjectDisposedException>(() => wal.AppendAsync(Entry(3), TestToken).AsTask());
    }

    // Holds the next cycle, then queues the rest of the group behind it. The entries are "group 0" to "group N-1".
    internal static async Task<long[]> AppendGroupAsync(WriteAheadLog wal, AppendCycles cycles)
    {
        var gate = cycles.HoldCycle(cycles.Count + 1);
        var first = Task.Run(async () => await wal.AppendAsync(GroupEntry(0), TestToken), TestToken);
        await gate.Entered.WaitAsync(DefaultTimeout, TestToken);

        // These appends arrive while the first cycle runs under the persistence lock.
        var waiting = new Task<long>[Concurrency - 1];
        for (var i = 0; i < waiting.Length; i++)
            waiting[i] = wal.AppendAsync(GroupEntry(i + 1), TestToken).AsTask();

        All(waiting, static task => False(task.IsCompleted));
        gate.Release();
        return [await first, .. await Task.WhenAll(waiting)];

        static BinaryLogEntry GroupEntry(int value)
            => new() { Term = 1L, Content = Encoding.UTF8.GetBytes($"group {value}") };
    }

    internal static async Task AssertGroupAsync(WriteAheadLog wal, long startIndex)
        => await AssertContentAsync(wal, startIndex, Enumerable.Range(0, Concurrency).Select(static i => $"group {i}").ToArray());

    private static async Task AssertContentAsync(WriteAheadLog wal, long startIndex, params string[] expected)
    {
        using var entries = await wal.ReadAsync(startIndex, startIndex + expected.Length - 1L, TestToken);
        Equal(expected.Length, entries.Count);
        for (var i = 0; i < expected.Length; i++)
            Equal(expected[i], await entries[i].ToStringAsync(Encoding.UTF8, token: TestToken));
    }

    private static async Task AssertTermsAsync(WriteAheadLog wal, long startIndex, params long[] expected)
    {
        for (var i = 0; i < expected.Length; i++)
            Equal(expected[i], await wal.GetTermAsync(startIndex + i, TestToken));
    }

    private static BinaryLogEntry Entry(int value, long term = 0L)
        => new() { Term = term, Content = Encoding.UTF8.GetBytes($"entry {value}") };

    private static WriteAheadLog.Options CreateOptions(TagList tags, Buffers.MemoryAllocator<byte> allocator = null)
        => new()
        {
            Location = GetTempPath(),
            MemoryManagement = WriteAheadLog.MemoryManagementStrategy.PrivateMemory,
            FlushInterval = TimeSpan.FromDays(1),
            MeasurementTags = tags,
            Allocator = allocator,
        };

    // Counts append persist cycles by their "pages" phase, which every cycle reports once (#124).
    // A cycle can be held inside one of its phases, under the persistence lock and before it publishes LastEntryIndex.
    internal sealed class AppendCycles : IDisposable
    {
        internal const string DefaultTagName = "group-commit-test";
        private readonly string tagName, id;
        private readonly MeterListener listener = new();
        private readonly ConcurrentDictionary<(int, string), Gate> gates = new();
        private int count;

        internal AppendCycles(string tagName = DefaultTagName, string id = null)
        {
            this.tagName = tagName;
            this.id = id ?? Guid.NewGuid().ToString();
            listener.InstrumentPublished = static (instrument, listener) =>
            {
                if (instrument is { Meter.Name: "DotNext.IO.WriteAheadLog", Name: "persist-phase-duration" })
                    listener.EnableMeasurementEvents(instrument);
            };
            listener.SetMeasurementEventCallback<double>(OnMeasurement);
            listener.Start();
        }

        internal TagList Tags => new() { { tagName, id } };

        internal int Count => Volatile.Read(ref count);

        // Cycles are numbered from 1 in the order they report the pages phase.
        internal Gate HoldCycle(int cycle, string phase = WriteAheadLog.PagesPhase)
            => gates[(cycle, phase)] = new();

        private void OnMeasurement(Instrument instrument, double value, ReadOnlySpan<KeyValuePair<string, object>> tags, object state)
        {
            bool owned = false, append = false;
            string phase = null;
            foreach (var (key, tag) in tags)
            {
                if (key == tagName)
                    owned = Equals(tag, id);
                else if (key is "dotnext.wal.phase")
                    phase = tag as string;
                else if (key is "dotnext.wal.cause")
                    append = Equals(tag, WriteAheadLog.AppendCause);
            }

            if (!owned || !append || phase is null)
                return;

            // Cycles are serialized by the persistence lock, so a later phase belongs to the last counted cycle.
            var cycle = phase is WriteAheadLog.PagesPhase ? Interlocked.Increment(ref count) : Count;
            if (gates.TryRemove((cycle, phase), out var gate))
                gate.Enter();
        }

        public void Dispose()
        {
            listener.Dispose();
            foreach (var gate in gates.Values)
                gate.Release();
        }

        internal sealed class Gate
        {
            private readonly TaskCompletionSource entered = new(TaskCreationOptions.RunContinuationsAsynchronously);
            private readonly ManualResetEventSlim released = new();

            internal Task Entered => entered.Task;

            internal void Release() => released.Set();

            internal void Enter()
            {
                entered.TrySetResult();
                released.Wait(DefaultTimeout);
            }
        }
    }
}
