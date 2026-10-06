namespace DotNext.Net.Cluster.Consensus.Raft.InProcess;

using IO;
using static MembershipClusterFixture;

/// <summary>
/// #50: a leader's proposal API rejects an entry whose term is not the term of the leadership that appends it.
/// Low-level appends stay unguarded, because follower replication legitimately carries older terms.
/// </summary>
public sealed class ProposalTermGuardTests : RaftTest
{
    private static MembershipClusterFixture CreateCluster() => new(voterCount: 3, joinerCount: 0);

    private static async Task<MembershipNode> ElectAsync(MembershipClusterFixture cluster, params int[] leaders)
    {
        await cluster.StartAsync();
        MembershipNode leader = null;
        foreach (var index in leaders)
        {
            leader = cluster.Nodes[index];
            await cluster.ElectAsync(leader);
            await cluster.ReplicateToAllVotersAsync(leader);
        }

        return leader;
    }

    [Fact(Timeout = TestTimeouts.Default)]
    public static async Task StaleTermProposalIsRejected()
    {
        await using var cluster = CreateCluster();
        var leader = await ElectAsync(cluster, 0, 1);
        Equal(2L, leader.Term);
        var lastIndex = leader.Log.LastEntryIndex;

        var proposal = leader.ReplicateAsync(new EmptyLogEntry { Term = 1L }, TestToken).AsTask();
        await AppendedOrCompletedAsync(leader, lastIndex, proposal);

        Equal(lastIndex, leader.Log.LastEntryIndex);
        await ThrowsAsync<NotLeaderException>(() => proposal);
    }

    [Fact(Timeout = TestTimeouts.Default)]
    public static async Task ProposalWithFutureTermIsRejected()
    {
        await using var cluster = CreateCluster();
        var leader = await ElectAsync(cluster, 0);
        var lastIndex = leader.Log.LastEntryIndex;

        var proposal = leader.ReplicateAsync(new EmptyLogEntry { Term = leader.Term + 1L }, TestToken).AsTask();
        await AppendedOrCompletedAsync(leader, lastIndex, proposal);

        Equal(lastIndex, leader.Log.LastEntryIndex);
        await ThrowsAsync<NotLeaderException>(() => proposal);
    }

    // The term advances after the proposal has passed every leader check and is queued for the append lock.
    // The term update is the first half of the production step-down, which leaves the leader state
    // installed until the transition completes.
    [Fact(Timeout = TestTimeouts.Default)]
    public static async Task TermAdvanceBetweenCaptureAndAppendIsRejected()
    {
        await using var cluster = CreateCluster();
        var leader = await ElectAsync(cluster, 0);
        var term = leader.Term;
        var blocker = new BlockingEntry(term);
        var blockerAppend = leader.Log.AppendAsync(blocker, TestToken).AsTask();
        await blocker.Entered.Task.WaitAsync(DefaultTimeout, TestToken);
        var lastIndex = leader.Log.LastEntryIndex;

        var proposal = leader.ReplicateAsync(new EmptyLogEntry { Term = term }, TestToken).AsTask();
        False(proposal.IsCompleted);
        await ((IPersistentState)leader.Log).UpdateTermAsync(term + 1L, resetLastVote: false, TestToken);
        blocker.Release.SetResult();
        var blockerIndex = await blockerAppend;

        await AppendedOrCompletedAsync(leader, blockerIndex, proposal);
        Equal(blockerIndex, leader.Log.LastEntryIndex);
        await ThrowsAsync<NotLeaderException>(() => proposal);
        Equal(lastIndex + 1L, blockerIndex);
    }

    // The helpers read the term from the log, so during the step-down window they build an entry
    // of a term this node does not lead.
    [Fact(Timeout = TestTimeouts.Default)]
    public static async Task ProposalBuiltAfterTermAdvanceIsRejected()
    {
        await using var cluster = CreateCluster();
        var leader = await ElectAsync(cluster, 0);
        var term = leader.Term;
        await ((IPersistentState)leader.Log).UpdateTermAsync(term + 1L, resetLastVote: false, TestToken);
        var lastIndex = leader.Log.LastEntryIndex;

        ReadOnlyMemory<byte> payload = new byte[] { 1, 2, 3 };
        var proposal = ((IRaftCluster)leader).ReplicateAsync(payload, token: TestToken).AsTask();
        await AppendedOrCompletedAsync(leader, lastIndex, proposal);

        Equal(lastIndex, leader.Log.LastEntryIndex);
        await ThrowsAsync<NotLeaderException>(() => proposal);
    }

    [Fact(Timeout = TestTimeouts.Default)]
    public static async Task CurrentTermProposalIsCommitted()
    {
        await using var cluster = CreateCluster();
        var leader = await ElectAsync(cluster, 0);
        var lastIndex = leader.Log.LastEntryIndex;

        var explicitTerm = leader.ReplicateAsync(new EmptyLogEntry { Term = leader.Term }, TestToken).AsTask();
        await cluster.PumpAsync(leader, explicitTerm);
        ReadOnlyMemory<byte> payload = new byte[] { 1, 2, 3 };
        var helper = ((IRaftCluster)leader).ReplicateAsync(payload, token: TestToken).AsTask();
        await cluster.PumpAsync(leader, helper);

        Equal(lastIndex + 2L, leader.Log.LastEntryIndex);
        True(leader.Log.LastCommittedEntryIndex >= lastIndex + 2L);
        Equal(leader.Term, await leader.Log.GetTermAsync(lastIndex + 1L, TestToken));
        Equal(leader.Term, await leader.Log.GetTermAsync(lastIndex + 2L, TestToken));
    }

    // Guarded proposals must not affect the follower path: a new leader replicates the previous term's entries.
    [Fact(Timeout = TestTimeouts.Default)]
    public static async Task FollowerReplicationOfOlderTermEntriesIsNotGuarded()
    {
        await using var cluster = CreateCluster();
        var n0 = await ElectAsync(cluster, 0);
        var (n1, n2) = (cluster.Nodes[1], cluster.Nodes[2]);
        const long inheritedIndex = 2L;

        // term 1: the entry reaches node 1 only, then node 0 steps down
        Equal(inheritedIndex, await n0.Log.AppendAsync(new EmptyLogEntry { Term = 1L }, TestToken));
        await cluster.ReplicateOnlyToAsync(n0, n1, inheritedIndex);
        Equal(1L, n1.Term);

        // term 2: node 1's no-op reaches node 2 together with the inherited term-1 entry
        await cluster.ElectAsync(n1);
        await cluster.PumpAsync(n1, n2.Log.WaitForApplyAsync(inheritedIndex + 1L, TestToken).AsTask());

        Equal(2L, n1.Term);
        Equal(1L, await n2.Log.GetTermAsync(inheritedIndex, TestToken));
        Equal(2L, await n2.Log.GetTermAsync(inheritedIndex + 1L, TestToken));
    }

    // The deposed leader waits for the commit of its own index. When the new leader overwrites that index,
    // the wait must not report success for an entry that is gone.
    [Fact(Timeout = TestTimeouts.Default)]
    public static async Task OverwrittenProposalNeverSucceeds()
    {
        await using var cluster = CreateCluster();
        var oldLeader = await ElectAsync(cluster, 0);
        var newLeader = cluster.Nodes[1];
        var index = oldLeader.Log.LastEntryIndex + 1L;

        var proposal = oldLeader.ReplicateAsync(new EmptyLogEntry { Term = oldLeader.Term }, TestToken).AsTask();
        await AppendedOrCompletedAsync(oldLeader, index - 1L, proposal);
        Equal(index, oldLeader.Log.LastEntryIndex);
        Equal(1L, await oldLeader.Log.GetTermAsync(index, TestToken));

        // the proposal never leaves the old leader, so the new leader's no-op takes its index
        await cluster.ElectAsync(newLeader);
        await cluster.PumpAsync(newLeader, oldLeader.Log.WaitForApplyAsync(index, TestToken).AsTask());
        Equal(2L, await oldLeader.Log.GetTermAsync(index, TestToken));

        Exception error = null;
        try
        {
            await proposal.WaitAsync(DefaultTimeout, TestToken);
        }
        catch (Exception e)
        {
            error = e;
        }

        True(error is NotLeaderException or OperationCanceledException, $"The overwritten proposal completed with {error?.ToString() ?? "success"}.");
    }


    private static async Task AppendedOrCompletedAsync(MembershipNode node, long lastIndex, Task proposal)
    {
        while (!proposal.IsCompleted && node.Log.LastEntryIndex == lastIndex)
            await Task.Delay(1, TestToken);
    }

    private sealed class BlockingEntry(long term) : IRaftLogEntry
    {
        public TaskCompletionSource Entered { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);

        public TaskCompletionSource Release { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);

        public long Term => term;

        bool IDataTransferObject.IsReusable => true;

        long? IDataTransferObject.Length => null;

        async ValueTask IDataTransferObject.WriteToAsync<TWriter>(TWriter writer, CancellationToken token)
        {
            Entered.SetResult();
            await Release.Task.WaitAsync(token);
            await writer.WriteAsync(new byte[] { 1, 2, 3 }, token: token);
        }
    }
}
