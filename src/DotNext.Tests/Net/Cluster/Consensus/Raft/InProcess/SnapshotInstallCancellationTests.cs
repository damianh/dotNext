using System.Net;

namespace DotNext.Net.Cluster.Consensus.Raft.InProcess;

using IO.Log;
using StateMachine;

/// <summary>
/// #73: a snapshot transfer that is canceled must not fault the follower's WAL, so that the leader's
/// retransmission of the snapshot can be installed.
/// </summary>
[Collection(TestCollections.WriteAheadLog)]
public sealed class SnapshotInstallCancellationTests : RaftTest
{
    private const long SnapshotIndex = 6L;
    private static readonly byte[] SnapshotState = [1, 2, 3, 4, 5, 6, 7, 8];

    // The request token of a message that the harness has already dispatched belongs to the leader's replication
    // worker, and the test cannot cancel it. Instead, the test sends the snapshot through the same production path
    // (network, RaftCluster.InstallSnapshotAsync, WAL, state machine) with a token it owns.
    [Fact(Timeout = TestTimeouts.Default)]
    public static async Task CanceledSnapshotTransferDoesNotBlockRetransmission()
    {
        var timeProvider = new ManualTimeProvider();
        var network = new InProcessNetwork();
        EndPoint[] membership = [new DnsEndPoint("node-a", 0), new DnsEndPoint("node-b", 0)];
        var location = GetTempPath();
        using var stateA = new ConsensusOnlyState();
        await using var machine = new ByteStateMachine(new(Path.Combine(location, "snapshot")));
        await using var stateB = new WriteAheadLog(new() { Location = location }, machine);
        await using var nodeA = CreateNode(0, stateA);
        await using var nodeB = CreateNode(1, stateB);
        await nodeA.StartAsync(TestToken);
        await nodeB.StartAsync(TestToken);

        var leader = nodeA.GetMember(nodeB.EndPoint).As<IRaftClusterMember>();
        var configuration = new EmptyLogEntry { Term = 1L };
        using var cancellation = new CancellationTokenSource();
        var transferring = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var stuck = new ByteSnapshotEntry(SnapshotState, 1L, async token =>
        {
            transferring.SetResult();
            await Task.Delay(Timeout.InfiniteTimeSpan, token);
        });

        var interrupted = leader.InstallSnapshotAsync(1L, stuck, SnapshotIndex, configuration, 0L, cancellation.Token);
        await transferring.Task.WaitAsync(TestToken);
        await cancellation.CancelAsync();
        await ThrowsAnyAsync<OperationCanceledException>(interrupted);

        Equal(0L, stateB.LastEntryIndex);
        Equal(0L, stateB.LastCommittedEntryIndex);
        Empty(machine.State);

        var retransmitted = await leader.InstallSnapshotAsync(
            1L,
            new ByteSnapshotEntry(SnapshotState, 1L),
            SnapshotIndex,
            configuration,
            0L,
            TestToken);

        Equal(HeartbeatResult.ReplicatedWithLeaderTerm, retransmitted.Value);
        Equal(SnapshotIndex, stateB.LastEntryIndex);
        Equal(SnapshotIndex, stateB.LastCommittedEntryIndex);
        Equal(SnapshotState, machine.State);

        Equal(SnapshotIndex + 1L, await stateB.AppendAsync(new TestLogEntry("after snapshot") { Term = 1L }, TestToken));
        await stateB.CommitAsync(SnapshotIndex + 1L, TestToken);
        await stateB.WaitForApplyAsync(SnapshotIndex + 1L, TestToken);

        InProcessCluster CreateNode(int index, IPersistentState state)
            => new(network, ((DnsEndPoint)membership[index]).Host, membership, state,
                timeProvider, TimeSpan.FromMilliseconds(100), startFollower: false);
    }
}
