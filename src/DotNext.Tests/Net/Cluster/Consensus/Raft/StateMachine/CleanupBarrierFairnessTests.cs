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
