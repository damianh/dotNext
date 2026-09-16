using System.Buffers.Binary;
using System.Collections.Concurrent;
using System.Runtime.CompilerServices;
using static System.Threading.Timeout;

namespace DotNext.Net.Cluster.Consensus.Raft.StateMachine;

using IO;
using AsyncAutoResetEventSlim = Threading.AsyncAutoResetEventSlim;

/// <summary>
/// Covers issue #15: the applier used to sample its target before acquiring the read lock and publish the
/// applied index after releasing it, so a snapshot installation could advance applied progress and then have
/// it overwritten by the applier's stale target.
/// </summary>
[Collection(TestCollections.WriteAheadLog)]
public sealed class RegressionIssue15 : Test
{
    private const long SnapshotIndex = 1000L;
    private const long SnapshotTerm = 7L;
    private const int TailLength = 5;

    // The trigger parks the applier by attaching a callback; see AsyncAutoResetEventSlim.CallbackAttachedState.
    private const int ApplierParkedState = 2;

    /// <summary>
    /// Race window A: the applier samples its target, then suspends on the read lock behind an installation.
    /// </summary>
    [Fact]
    public static async Task StaleApplierTargetCannotRegressAnInstalledSnapshot()
    {
        var options = CreateOptions(InfiniteTimeSpan);
        var machineLocation = new DirectoryInfo(GetTempPath());
        byte[] snapshotState = [1, 2, 3, 4];

        await using var inner = new ByteArrayStateMachine(machineLocation);
        await inner.RestoreAsync(TestToken);
        var machine = new GatedStateMachine(inner);
        await using var wal = new WriteAheadLog(options, machine);
        LockManagerOf(wal).TrackSuspendedCallers();
        WaitForApplierParked(wal);

        await AppendTailAsync(wal);

        // The applier wakes up, samples the commit index, takes the read lock and parks inside the state
        // machine while applying the first entry.
        await wal.CommitAsync(TailLength, TestToken);
        await machine.RegularEntered.Task.WaitAsync(TestToken);

        // The installation takes the append lock and queues behind the applier's read lock.
        var installation = wal
            .AppendAsync(new SnapshotEntry(snapshotState, SnapshotTerm), SnapshotIndex, TestToken)
            .AsTask();
        WaitForSuspendedCaller(wal, "Install Snapshot");

        // ReleaseReadLock hands the overwrite lock to the installation synchronously, so the applier's next
        // iteration samples the pre-installation commit index and then parks on the read lock. The
        // installation is still inside the state machine, so the commit index cannot move in between.
        machine.RegularRelease.SetResult();
        await machine.SnapshotEntered.Task.WaitAsync(TestToken);
        WaitForSuspendedCaller(wal, WriteAheadLog.ApplierCallerInfo);

        Equal(TailLength, wal.LastCommittedEntryIndex);
        Equal(TailLength, wal.LastAppliedIndex);

        machine.SnapshotRelease.SetResult();
        await installation;

        // Let the applier complete the iteration that captured the stale target.
        WaitForApplierParked(wal);

        Equal(SnapshotIndex, wal.LastCommittedEntryIndex);
        Equal(SnapshotIndex, wal.LastAppliedIndex);

        // A waiter for the installed snapshot completes without a subsequent commit.
        await wal.WaitForApplyAsync(SnapshotIndex, TestToken).AsTask().WaitAsync(DefaultTimeout, TestToken);

        // Nothing below the snapshot boundary is replayed.
        Equal(Enumerable.Range(1, TailLength).Select(static i => (long)i), machine.AppliedEntries);
        Equal(snapshotState, inner.State);
    }

    /// <summary>
    /// Race window B: the applier publishes its target after releasing the read lock, by which time an
    /// installation may already have advanced the applied index.
    /// </summary>
    /// <remarks>
    /// There is no suspension point between the release and the publication, and lock-release resumption is
    /// asynchronous, so the interleaving cannot be scheduled from a test without a production seam. This
    /// reproduces the state that window B produces with real components and then drives the production
    /// publication path with the stale target the applier would have carried.
    /// </remarks>
    [Fact]
    public static async Task StalePublicationAfterInstallationCannotLowerAppliedProgress()
    {
        var options = CreateOptions(InfiniteTimeSpan);
        var machineLocation = new DirectoryInfo(GetTempPath());
        byte[] snapshotState = [1, 2, 3, 4];

        await using var machine = new ByteArrayStateMachine(machineLocation);
        await machine.RestoreAsync(TestToken);
        await using var wal = new WriteAheadLog(options, machine);
        WaitForApplierParked(wal);

        await AppendTailAsync(wal);
        await wal.CommitAsync(TailLength, TestToken);
        await wal.WaitForApplyAsync(TailLength, TestToken);
        WaitForApplierParked(wal);
        Equal(TailLength, wal.LastAppliedIndex);

        await wal.AppendAsync(new SnapshotEntry(snapshotState, SnapshotTerm), SnapshotIndex, TestToken);
        Equal(SnapshotIndex, wal.LastAppliedIndex);

        // Exactly what the applier executes once it leaves the read lock carrying the stale target.
        PublishAppliedIndex(wal, TailLength);

        Equal(SnapshotIndex, wal.LastAppliedIndex);
        await wal.WaitForApplyAsync(SnapshotIndex, TestToken).AsTask().WaitAsync(DefaultTimeout, TestToken);
    }

    /// <summary>
    /// Validates the combined installation, flush and application behavior: an installation queued behind an
    /// in-flight application must leave a consistent checkpoint and must not push the applier back over
    /// metadata that reclamation already removed.
    /// </summary>
    [Fact]
    public static async Task InstallationBehindInFlightApplicationSurvivesFlushAndRestart()
    {
        var options = CreateOptions(InfiniteTimeSpan);
        var machineLocation = new DirectoryInfo(GetTempPath());
        var metadataPages = new DirectoryInfo(Path.Combine(options.Location, "metadata"));
        byte[] snapshotState = [1, 2, 3, 4];

        await using (var inner = new ByteArrayStateMachine(machineLocation))
        {
            await inner.RestoreAsync(TestToken);
            var machine = new GatedStateMachine(inner);
            await using var wal = new WriteAheadLog(options, machine);
            LockManagerOf(wal).TrackSuspendedCallers();
            WaitForApplierParked(wal);

            await AppendTailAsync(wal);
            await wal.CommitAsync(TailLength, TestToken);
            await machine.RegularEntered.Task.WaitAsync(TestToken);

            var installation = wal
                .AppendAsync(new SnapshotEntry(snapshotState, SnapshotTerm), SnapshotIndex, TestToken)
                .AsTask();
            WaitForSuspendedCaller(wal, "Install Snapshot");

            machine.RegularRelease.SetResult();
            await machine.SnapshotEntered.Task.WaitAsync(TestToken);
            WaitForSuspendedCaller(wal, WriteAheadLog.ApplierCallerInfo);

            machine.SnapshotRelease.SetResult();
            await installation;
            WaitForApplierParked(wal);

            await wal.FlushAsync(TestToken);
            Equal(SnapshotIndex, ReadCheckpoint(options.Location));
            Equal(SnapshotIndex, wal.LastAppliedIndex);

            // The squashed metadata page is reclaimed, so an applier that walked back below the boundary
            // would read removed metadata.
            await SpinWaitAsync(() => !File.Exists(Path.Combine(metadataPages.FullName, "0")));

            Equal(
                SnapshotIndex + 1L,
                await wal.AppendAsync(
                    new BinaryLogEntry { Content = BitConverter.GetBytes(42), Term = SnapshotTerm },
                    TestToken));
            await wal.CommitAsync(SnapshotIndex + 1L, TestToken);
            await wal.WaitForApplyAsync(SnapshotIndex + 1L, TestToken).AsTask().WaitAsync(DefaultTimeout, TestToken);
            await wal.FlushAsync(TestToken);

            Equal(SnapshotIndex + 1L, ReadCheckpoint(options.Location));
            Equal(SnapshotIndex + 1L, wal.LastAppliedIndex);
            Equal(
                Enumerable.Range(1, TailLength).Select(static i => (long)i).Append(SnapshotIndex + 1L),
                machine.AppliedEntries);
        }

        await using (var machine = new ByteArrayStateMachine(machineLocation))
        {
            await machine.RestoreAsync(TestToken);
            await using var wal = new WriteAheadLog(options, machine);
            await wal.InitializeAsync(TestToken);

            Equal(SnapshotIndex + 1L, wal.LastCommittedEntryIndex);
            Equal(SnapshotIndex + 1L, wal.LastEntryIndex);
            Equal(snapshotState, machine.State);
        }
    }

    /// <summary>
    /// Bounded interleaving coverage: repeated installations racing ordinary commits and applications must
    /// never move applied progress backwards nor strand a waiter.
    /// </summary>
    [Fact]
    public static async Task ConcurrentInstallationAndApplicationKeepProgressMonotonic()
    {
        const int rounds = 25;
        const int batchSize = 8;

        var options = CreateOptions(InfiniteTimeSpan);
        var machineLocation = new DirectoryInfo(GetTempPath());

        await using var machine = new ByteArrayStateMachine(machineLocation);
        await machine.RestoreAsync(TestToken);
        await using var wal = new WriteAheadLog(options, machine);

        using var samplerStopped = new CancellationTokenSource();
        var sampler = Task.Run(
            () =>
            {
                var highest = 0L;
                while (!samplerStopped.IsCancellationRequested)
                {
                    var observed = wal.LastAppliedIndex;
                    if (observed < highest)
                        return highest;

                    highest = observed;
                }

                return -1L;
            },
            TestToken);

        var snapshotIndex = 0L;
        for (var round = 1; round <= rounds; round++)
        {
            for (var i = 0; i < batchSize; i++)
            {
                await wal.AppendAsync(
                    new BinaryLogEntry { Content = BitConverter.GetBytes(i), Term = round }, TestToken);
            }

            await wal.CommitAsync(wal.LastEntryIndex, TestToken);

            snapshotIndex = wal.LastCommittedEntryIndex + batchSize;
            await wal.AppendAsync(
                new SnapshotEntry([(byte)round], round), snapshotIndex, TestToken);
            await wal.WaitForApplyAsync(snapshotIndex, TestToken).AsTask().WaitAsync(DefaultTimeout, TestToken);
        }

        await samplerStopped.CancelAsync();
        Equal(-1L, await sampler);

        Equal(snapshotIndex, wal.LastCommittedEntryIndex);
        Equal(snapshotIndex, wal.LastAppliedIndex);
        await wal.WaitForApplyAsync(snapshotIndex, TestToken).AsTask().WaitAsync(DefaultTimeout, TestToken);
    }

    private static async Task AppendTailAsync(WriteAheadLog wal)
    {
        for (var i = 1; i <= TailLength; i++)
        {
            Equal(
                i,
                await wal.AppendAsync(
                    new BinaryLogEntry { Content = BitConverter.GetBytes(i), Term = 1L },
                    TestToken));
        }
    }

    [UnsafeAccessor(UnsafeAccessorKind.Field, Name = "applyTrigger")]
    private static extern ref AsyncAutoResetEventSlim ApplyTrigger(WriteAheadLog wal);

    [UnsafeAccessor(UnsafeAccessorKind.Field, Name = "state")]
    private static extern ref int TriggerState(AsyncAutoResetEventSlim trigger);

    [UnsafeAccessor(UnsafeAccessorKind.Field, Name = "lockManager")]
    private static extern ref WriteAheadLog.LockManager LockManagerOf(WriteAheadLog wal);

    [UnsafeAccessor(UnsafeAccessorKind.Method, Name = "set_LastAppliedIndex")]
    private static extern void PublishAppliedIndex(WriteAheadLog wal, long value);

    private static void WaitForApplierParked(WriteAheadLog wal)
        => True(SpinWait.SpinUntil(
            () => Volatile.Read(ref TriggerState(ApplyTrigger(wal))) is ApplierParkedState,
            DefaultTimeout));

    private static void WaitForSuspendedCaller(WriteAheadLog wal, string callerInfo)
        => True(SpinWait.SpinUntil(
            () => LockManagerOf(wal).GetSuspendedCallers().Any(info => info is string caller && caller == callerInfo),
            DefaultTimeout));

    private static async Task SpinWaitAsync(Func<bool> condition)
    {
        for (var attempt = 0; attempt < 200; attempt++)
        {
            if (condition())
                return;

            await Task.Delay(50, TestToken);
        }

        True(condition());
    }

    private static WriteAheadLog.Options CreateOptions(TimeSpan interval)
        => new()
        {
            Location = GetTempPath(),
            MemoryManagement = WriteAheadLog.MemoryManagementStrategy.PrivateMemory,
            FlushInterval = interval,
        };

    private static long ReadCheckpoint(string location)
    {
        // The log keeps the checkpoint file open for writing.
        using var handle = File.OpenHandle(
            Path.Combine(location, "checkpoint"),
            access: FileAccess.Read,
            share: FileShare.ReadWrite);

        Span<byte> content = stackalloc byte[sizeof(uint) + sizeof(long)];
        return RandomAccess.Read(handle, content, fileOffset: 0L) switch
        {
            0 => 0L,
            sizeof(long) => BinaryPrimitives.ReadInt64LittleEndian(content),
            _ => BinaryPrimitives.ReadInt64LittleEndian(content.Slice(sizeof(uint))),
        };
    }

    private sealed class ByteArrayStateMachine(DirectoryInfo location) : SimpleStateMachine(location)
    {
        internal byte[] State { get; private set; } = [];

        protected override async ValueTask RestoreAsync(FileInfo snapshotFile, CancellationToken token)
            => State = await File.ReadAllBytesAsync(snapshotFile.FullName, token);

        protected override ValueTask PersistAsync(IAsyncBinaryWriter writer, CancellationToken token)
            => writer.Invoke(State, token);

        protected override ValueTask<bool> ApplyAsync(LogEntry entry, CancellationToken token)
            => ValueTask.FromResult(false);
    }

    private sealed class GatedStateMachine(ByteArrayStateMachine inner) : IStateMachine
    {
        private int regularEntries;

        internal readonly TaskCompletionSource RegularEntered = new(TaskCreationOptions.RunContinuationsAsynchronously);
        internal readonly TaskCompletionSource RegularRelease = new(TaskCreationOptions.RunContinuationsAsynchronously);
        internal readonly TaskCompletionSource SnapshotEntered = new(TaskCreationOptions.RunContinuationsAsynchronously);
        internal readonly TaskCompletionSource SnapshotRelease = new(TaskCreationOptions.RunContinuationsAsynchronously);

        internal ConcurrentQueue<long> AppliedEntries { get; } = new();

        public ISnapshot Snapshot => inner.As<IStateMachine>().Snapshot;

        public async ValueTask<long> ApplyAsync(LogEntry entry, CancellationToken token)
        {
            if (entry.IsSnapshot)
            {
                SnapshotEntered.TrySetResult();
                await SnapshotRelease.Task.WaitAsync(token);
            }
            else
            {
                AppliedEntries.Enqueue(entry.Index);

                // Park the applier inside the read lock while it applies the very first entry.
                if (Interlocked.Increment(ref regularEntries) is 1)
                {
                    RegularEntered.TrySetResult();
                    await RegularRelease.Task.WaitAsync(token);
                }
            }

            return await inner.As<IStateMachine>().ApplyAsync(entry, token);
        }

        public ValueTask ReclaimGarbageAsync(long watermark, CancellationToken token)
            => inner.As<IStateMachine>().ReclaimGarbageAsync(watermark, token);
    }

    private sealed class SnapshotEntry(byte[] content, long term) : IRaftLogEntry
    {
        public long Term => term;

        public bool IsSnapshot => true;

        public long? Length => content.LongLength;

        public bool IsReusable => true;

        public ValueTask WriteToAsync<TWriter>(TWriter writer, CancellationToken token)
            where TWriter : IAsyncBinaryWriter
            => writer.Invoke(content, token);
    }
}
