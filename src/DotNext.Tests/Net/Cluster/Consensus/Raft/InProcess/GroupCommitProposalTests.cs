namespace DotNext.Net.Cluster.Consensus.Raft.InProcess;

using StateMachine;
using static MembershipClusterFixture;

/// <summary>
/// #125: concurrent leader proposals share the leader's WAL persist cycle.
/// </summary>
public sealed class GroupCommitProposalTests : RaftTest
{
    [Fact(Timeout = TestTimeouts.Default)]
    public static async Task ConcurrentProposalsShareLeaderPersistCycles()
    {
        await using var cluster = new MembershipClusterFixture(voterCount: 3, joinerCount: 0);
        await cluster.StartAsync();
        var leader = cluster.Nodes[0];
        await cluster.ElectAsync(leader);
        await cluster.PumpAsync(leader, leader.ForceReplicationAsync(TestToken).AsTask());
        var lastIndex = leader.Log.LastEntryIndex;

        using var cycles = new WriteAheadLogGroupCommitTests.AppendCycles(MembershipNode.NodeMeasurementTag, leader.Location);
        var gate = cycles.HoldCycle(1);
        var proposals = new Task[WriteAheadLogGroupCommitTests.Concurrency];
        proposals[0] = leader.ReplicateAsync(new EmptyLogEntry { Term = leader.Term }, TestToken).AsTask();
        await gate.Entered.WaitAsync(DefaultTimeout, TestToken);
        for (var i = 1; i < proposals.Length; i++)
            proposals[i] = leader.ReplicateAsync(new EmptyLogEntry { Term = leader.Term }, TestToken).AsTask();

        gate.Release();
        await cluster.PumpAsync(leader, Task.WhenAll(proposals));

        Equal(lastIndex + proposals.Length, leader.Log.LastEntryIndex);
        True(leader.Log.LastCommittedEntryIndex >= leader.Log.LastEntryIndex);

        // One cycle for the first proposal, one shared cycle for the proposals that waited behind it.
        Equal(2, cycles.Count);
    }
}
