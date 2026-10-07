using System.Runtime.CompilerServices;

namespace DotNext.Net.Cluster.Consensus.Raft.StateMachine;

using IO;

[Collection(TestCollections.WriteAheadLog)]
public sealed class CleanupBarrierFairnessTests : Test
{
    private const string CleanupCaller = "Remove Pages";

    // A replication read held by a slow follower must not block reads for the other followers
    // once a snapshot cleanup is queued (#128).
    [Fact(Timeout = TestTimeouts.Default)]
    public static async Task CleanupBarrierDoesNotParkReadsBehindHeldRead()
    {
        var location = GetTempPath();
        await using var machine = new SnapshotAtSix(new(Path.Combine(location, "snapshot")));
        await using var wal = new WriteAheadLog(new() { Location = location }, machine);
        LockManagerOf(wal).TrackSuspendedCallers();
        for (var i = 1; i <= 7; i++)
            await wal.AppendAsync(new TestLogEntry("x") { Term = 1L }, TestToken);
        await wal.CommitAsync(7L, TestToken);
        await wal.WaitForApplyAsync(7L, TestToken);
        Equal(6L, machine.As<ISnapshotManager>().Snapshot?.Index);

        // an in-flight AppendEntries to a slow follower
        var held = await wal.ReadAsync(7L, 7L, TestToken);

        // the flush publishes snapshot 6 and spawns the cleanup, which queues a read barrier behind the held read
        await wal.FlushAsync(TestToken);
        WaitForSuspendedCaller(wal, CleanupCaller);

        // a replication read for another follower
        var other = wal.ReadAsync(7L, 7L, TestToken);
        try
        {
            True(other.IsCompletedSuccessfully, "the read is parked behind the cleanup barrier, which waits for the held read");
        }
        finally
        {
            held.Dispose();
            (await other).Dispose();
        }
    }

    [UnsafeAccessor(UnsafeAccessorKind.Field, Name = "lockManager")]
    private static extern ref WriteAheadLog.LockManager LockManagerOf(WriteAheadLog wal);

    [UnsafeAccessor(UnsafeAccessorKind.Field, Name = "cleanupTask")]
    private static extern ref WeakReference<Task> CleanupTaskOf(WriteAheadLog wal);

    private const long SnapshotDepth = 10L;

    // The barrier waits for a reader registered before the cleanup was requested: such a reader may have observed
    // no snapshot, or an older one, and still reach the squashed pages.
    [Fact(Timeout = TestTimeouts.Default)]
    public static async Task CleanupDoesNotRemovePagesReachableByPreexistingReader()
    {
        var options = CreateOptions();
        var firstMetadataPage = Path.Combine(options.Location, "metadata", "0");
        await using var wal = new WriteAheadLog(options, IStateMachine.CreateNoOp(SnapshotDepth));
        LockManagerOf(wal).TrackSuspendedCallers();

        var lastIndex = GetMetadataEntriesPerPage() * 2L + SnapshotDepth / 2L;
        await AppendAsync(wal, 1L, lastIndex);

        // a replication read registered before any snapshot exists, so it reads entry 1 from the first page
        using (var held = await wal.ReadAsync(1L, 1L, TestToken))
        {
            await CommitAndFlushAsync(wal, lastIndex);
            WaitForSuspendedCaller(wal, CleanupCaller);

            True(File.Exists(firstMetadataPage));
            False(held[0].IsSnapshot);
            Equal(1L, ReadInt64(await held[0].ToByteArrayAsync(token: TestToken)));
        }

        await AwaitCleanupAsync(wal);
        False(File.Exists(firstMetadataPage));
    }

    // Overlapping replication reads, with at least one read held at any time, cannot starve the cleanup:
    // the barrier waits only for the read registered before it was requested.
    [Fact(Timeout = TestTimeouts.Default)]
    public static async Task CleanupRunsUnderContinuousOverlappingReads()
    {
        var options = CreateOptions();
        var firstMetadataPage = Path.Combine(options.Location, "metadata", "0");
        await using var wal = new WriteAheadLog(options, IStateMachine.CreateNoOp(SnapshotDepth));
        LockManagerOf(wal).TrackSuspendedCallers();

        var lastIndex = GetMetadataEntriesPerPage() * 2L + SnapshotDepth / 2L;
        await AppendAsync(wal, 1L, lastIndex);

        var held = await wal.ReadAsync(1L, 1L, TestToken);
        try
        {
            await CommitAndFlushAsync(wal, lastIndex);
            WaitForSuspendedCaller(wal, CleanupCaller);

            for (var i = 0; i < 100; i++)
            {
                var next = wal.ReadAsync(1L, 1L, TestToken);
                True(next.IsCompletedSuccessfully, "a read is parked behind the cleanup barrier");
                held.Dispose();
                held = await next;

                // a read registered after the cleanup was requested observes the snapshot instead of the squashed pages
                True(held[0].IsSnapshot);
            }

            // the cleanup completes while a read is still held
            await AwaitCleanupAsync(wal);
            False(File.Exists(firstMetadataPage));
        }
        finally
        {
            held.Dispose();
        }
    }

    // A flush pass does not wait for the cleanup scheduled by an earlier pass, and the cleanup catches up
    // with the newest snapshot once the old reader is gone.
    [Fact(Timeout = TestTimeouts.Default)]
    public static async Task FlusherIsNotBlockedByPendingCleanup()
    {
        var options = CreateOptions();
        var metadataPage = static (WriteAheadLog.Options options, int page) => Path.Combine(options.Location, "metadata", page.ToString());
        await using var wal = new WriteAheadLog(options, IStateMachine.CreateNoOp(SnapshotDepth));
        LockManagerOf(wal).TrackSuspendedCallers();

        var entriesPerPage = GetMetadataEntriesPerPage();
        var firstIndex = entriesPerPage * 2L + SnapshotDepth / 2L;
        await AppendAsync(wal, 1L, firstIndex);

        using (var held = await wal.ReadAsync(1L, 1L, TestToken))
        {
            await CommitAndFlushAsync(wal, firstIndex);
            WaitForSuspendedCaller(wal, CleanupCaller);

            // the snapshot advances twice more while the cleanup waits for the held read
            var secondIndex = entriesPerPage * 3L + SnapshotDepth / 2L;
            await AppendAsync(wal, firstIndex + 1L, secondIndex);
            await CommitAndFlushAsync(wal, secondIndex);

            var lastIndex = entriesPerPage * 4L + SnapshotDepth / 2L;
            await AppendAsync(wal, secondIndex + 1L, lastIndex);
            await CommitAndFlushAsync(wal, lastIndex);

            True(File.Exists(metadataPage(options, 0)));
            Equal(1L, ReadInt64(await held[0].ToByteArrayAsync(token: TestToken)));
        }

        await AwaitCleanupAsync(wal);
        False(File.Exists(metadataPage(options, 0)));
        False(File.Exists(metadataPage(options, 1)));
        False(File.Exists(metadataPage(options, 2)));
        True(File.Exists(metadataPage(options, 4)));
    }

    private static WriteAheadLog.Options CreateOptions() => new()
    {
        Location = GetTempPath(),
        // Memory-mapped pages exist on disk before the cleanup can remove them.
        MemoryManagement = WriteAheadLog.MemoryManagementStrategy.SharedMemory,
        FlushInterval = Timeout.InfiniteTimeSpan,
    };

    private static async Task AppendAsync(WriteAheadLog wal, long fromIndex, long toIndex)
    {
        for (var i = fromIndex; i <= toIndex; i++)
            Equal(i, await wal.AppendAsync(new BinaryLogEntry { Content = BitConverter.GetBytes(i), Term = 1L }, TestToken));
    }

    private static async Task CommitAndFlushAsync(WriteAheadLog wal, long index)
    {
        await wal.CommitAsync(index, TestToken);
        await wal.WaitForApplyAsync(index, TestToken);
        await wal.FlushAsync(TestToken);
    }

    private static async Task AwaitCleanupAsync(WriteAheadLog wal)
    {
        True(CleanupTaskOf(wal).TryGetTarget(out var cleanup));
        await cleanup.WaitAsync(TestToken);
        True(cleanup.IsCompletedSuccessfully);
    }

    private static long ReadInt64(byte[] bytes) => BitConverter.ToInt64(bytes);

    private static long GetMetadataEntriesPerPage()
    {
        var pageSize = int.Max(4096, Environment.SystemPageSize);
        return pageSize / GetAlignedSize(LogEntryMetadata.Size, pageSize);
    }

    // Mirrors MetadataPageManager.GetAlignedSize for a log without integrity hashes.
    private static int GetAlignedSize(int headerSize, int containerSize)
    {
        var best = int.MaxValue;

        for (var i = 1; i * i <= containerSize; i++)
        {
            if (containerSize % i is not 0)
                continue;

            var d2 = containerSize / i;

            if (i >= headerSize && i < best)
                best = i;

            if (d2 >= headerSize && d2 < best)
                best = d2;
        }

        return best is int.MaxValue
            ? throw new OverflowException()
            : best;
    }

    private static void WaitForSuspendedCaller(WriteAheadLog wal, string callerInfo)
        => True(SpinWait.SpinUntil(
            () => LockManagerOf(wal).GetSuspendedCallers().Any(info => info is string caller && caller == callerInfo),
            DefaultTimeout));

    private sealed class SnapshotAtSix(DirectoryInfo location) : SimpleStateMachine(location)
    {
        protected override ValueTask<bool> ApplyAsync(LogEntry entry, CancellationToken token)
            => ValueTask.FromResult(entry.Index is 6L);

        protected override ValueTask RestoreAsync(FileInfo snapshotFile, CancellationToken token)
            => ValueTask.CompletedTask;

        protected override ValueTask PersistAsync(IAsyncBinaryWriter writer, CancellationToken token)
            => writer.WriteAsync(new byte[] { 6 }, token: token);
    }
}
