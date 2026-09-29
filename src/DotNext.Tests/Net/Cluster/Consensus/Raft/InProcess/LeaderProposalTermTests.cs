namespace DotNext.Net.Cluster.Consensus.Raft.InProcess;

using IO.Log;
using static MembershipClusterFixture;

/// <summary>
/// Regressions for #50: a leader proposal (<see cref="RaftCluster{TMember}.ReplicateAsync{TEntry}"/>) is accepted
/// only for the term in which the local node leads, and a failed or canceled proposal has an unknown outcome.
/// </summary>
/// <remarks>
/// Every test uses five WAL-backed voters, so the checks run against the production append lock.
/// </remarks>
public sealed class LeaderProposalTermTests : RaftTest
{
    [Fact(Timeout = TestTimeouts.Default)]
    public static async Task CurrentTermProposalCommits()
    {
        await using var cluster = new MembershipClusterFixture();
        await cluster.StartAsync();
        var leader = cluster.Nodes[0];
        await cluster.ElectAsync(leader);
        var term = leader.Term;

        var explicitEntry = Record.ExceptionAsync(() => leader.ReplicateAsync(new EmptyLogEntry { Term = term }, TestToken).AsTask()).AsTask();
        await cluster.PumpAsync(leader, explicitEntry);
        Null(await explicitEntry);
        Equal(2L, leader.Log.LastEntryIndex);
        Equal(term, await leader.Log.GetTermAsync(2L, TestToken));

        var helper = Record.ExceptionAsync(() => leader.ReplicateAsync(new byte[] { 1, 2, 3 }, token: TestToken).AsTask()).AsTask();
        await cluster.PumpAsync(leader, helper);
        Null(await helper);
        Equal(3L, leader.Log.LastAppliedIndex);
        Equal(term, await leader.Log.GetTermAsync(3L, TestToken));
    }

    [Fact(Timeout = TestTimeouts.Default)]
    public static async Task FutureTermProposalIsRejected()
    {
        await using var cluster = new MembershipClusterFixture();
        await cluster.StartAsync();
        var leader = cluster.Nodes[0];
        await cluster.ElectAsync(leader);
        var lastIndex = leader.Log.LastEntryIndex;

        var proposal = Record.ExceptionAsync(() => leader.ReplicateAsync(new EmptyLogEntry { Term = leader.Term + 1L }, TestToken).AsTask()).AsTask();
        await cluster.PumpAsync(leader, proposal);

        IsType<NotLeaderException>(await proposal);
        Equal(lastIndex, leader.Log.LastEntryIndex);
    }

    [Fact(Timeout = TestTimeouts.Default)]
    public static async Task StaleTermProposalIsRejected()
    {
        await using var cluster = new MembershipClusterFixture();
        await cluster.StartAsync();
        var oldLeader = cluster.Nodes[0];
        var newLeader = cluster.Nodes[1];
        await cluster.ElectAsync(oldLeader);
        var staleTerm = oldLeader.Term;

        await oldLeader.StopAsync(TestToken);
        await cluster.ElectAsync(newLeader);
        True(newLeader.Term > staleTerm);
        var lastIndex = newLeader.Log.LastEntryIndex;

        var proposal = Record.ExceptionAsync(() => newLeader.ReplicateAsync(new EmptyLogEntry { Term = staleTerm }, TestToken).AsTask()).AsTask();
        await cluster.PumpAsync(newLeader, proposal);

        IsType<NotLeaderException>(await proposal);
        Equal(lastIndex, newLeader.Log.LastEntryIndex);
    }

    [Fact(Timeout = TestTimeouts.Default)]
    public static async Task TermChangeBetweenCheckAndAppendIsRejected()
    {
        await using var cluster = new MembershipClusterFixture();
        await cluster.StartAsync();
        var leader = cluster.Nodes[0];
        await cluster.ElectAsync(leader);
        var term = leader.Term;

        // The proposal passes the leader-state check, then waits for the append lock behind another append.
        var gate = new GatedLogEntry { Term = term };
        var blocker = leader.Log.AppendAsync(gate, TestToken).AsTask();
        await gate.Started;
        var proposal = Record.ExceptionAsync(() => leader.ReplicateAsync(new EmptyLogEntry { Term = term }, TestToken).AsTask()).AsTask();
        False(proposal.IsCompleted);

        // The term advances after the check but before the append.
        await ((IPersistentState)leader.Log).UpdateTermAsync(term + 1L, resetLastVote: false, TestToken);
        gate.Release();
        var blockerIndex = await blocker;
        await cluster.PumpAsync(leader, proposal);

        IsType<NotLeaderException>(await proposal);
        Equal(blockerIndex, leader.Log.LastEntryIndex);
    }

    [Fact(Timeout = TestTimeouts.Default)]
    public static async Task StepDownInSameTermBetweenCheckAndAppendIsRejected()
    {
        await using var cluster = new MembershipClusterFixture();
        await cluster.StartAsync();
        var leader = cluster.Nodes[0];
        await cluster.ElectAsync(leader);
        var term = leader.Term;

        var gate = new GatedLogEntry { Term = term };
        var blocker = leader.Log.AppendAsync(gate, TestToken).AsTask();
        await gate.Started;
        var proposal = Record.ExceptionAsync(() => leader.ReplicateAsync(new EmptyLogEntry { Term = term }, TestToken).AsTask()).AsTask();
        False(proposal.IsCompleted);

        // The node stops leading without a term change, as on quorum or lease loss.
        True(await ((NetworkTransport.ILocalMember)leader).ResignAsync(TestToken));
        Equal(term, leader.Term);
        gate.Release();
        var blockerIndex = await blocker;

        IsType<NotLeaderException>(await proposal.WaitAsync(DefaultTimeout, TestToken));
        Equal(blockerIndex, leader.Log.LastEntryIndex);
    }

    [Fact(Timeout = TestTimeouts.Default)]
    public static async Task CanceledProposalMayStillCommit()
    {
        await using var cluster = new MembershipClusterFixture();
        await cluster.StartAsync();
        var leader = cluster.Nodes[0];
        await cluster.ElectAsync(leader);
        var term = leader.Term;
        var index = leader.Log.LastEntryIndex + 1L;

        using var source = CancellationTokenSource.CreateLinkedTokenSource(TestToken);
        var proposal = leader.ReplicateAsync(new EmptyLogEntry { Term = term }, source.Token).AsTask();
        await WaitForLastIndexAsync(leader, index);
        await source.CancelAsync();
        await ThrowsAnyAsync<OperationCanceledException>(() => proposal);

        // The caller saw a failure, but the entry is in the log and is committed by later replication.
        Equal(index, leader.Log.LastEntryIndex);
        await cluster.ReplicateUntilAppliedAsync(leader, index);
        Equal(term, await leader.Log.GetTermAsync(index, TestToken));
    }

    [Fact(Timeout = TestTimeouts.Default)]
    public static async Task ProposalFailedByLeadershipLossMayStillCommitUnderNextLeader()
    {
        await using var cluster = new MembershipClusterFixture();
        await cluster.StartAsync();
        var oldLeader = cluster.Nodes[0];
        var newLeader = cluster.Nodes[1];
        await cluster.ElectAsync(oldLeader);
        var oldTerm = oldLeader.Term;
        var index = oldLeader.Log.LastEntryIndex + 1L;

        var proposal = Record.ExceptionAsync(() => oldLeader.ReplicateAsync(new EmptyLogEntry { Term = oldTerm }, TestToken).AsTask()).AsTask();
        await WaitForLastIndexAsync(oldLeader, index);

        // The entry reaches one follower only, and the old leader steps down without a quorum.
        await cluster.ReplicateOnlyToAsync(oldLeader, newLeader, index);
        IsType<NotLeaderException>(await proposal.WaitAsync(DefaultTimeout, TestToken));
        await oldLeader.StopAsync(TestToken);

        // The next leader inherits the older-term entry: follower replication of historical terms is not rejected.
        await cluster.ElectAsync(newLeader, passWriteBarrier: false);
        True(newLeader.Term > oldTerm);
        await cluster.ReplicateUntilAppliedAsync(newLeader, newLeader.Log.LastEntryIndex);
        Equal(oldTerm, await newLeader.Log.GetTermAsync(index, TestToken));
        True(newLeader.Log.LastAppliedIndex >= index);
    }

    private static async Task WaitForLastIndexAsync(MembershipNode node, long index)
    {
        while (node.Log.LastEntryIndex < index)
        {
            TestToken.ThrowIfCancellationRequested();
            await Task.Yield();
        }
    }
}
