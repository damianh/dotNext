namespace DotNext.Net.Cluster.Consensus.Raft.InProcess;

using static MembershipClusterFixture;

public sealed class MembershipHarnessTests : RaftTest
{
    [Fact(Timeout = TestTimeouts.Default)]
    public static async Task ElectionRequiresExplicitDelivery()
    {
        await using var cluster = new MembershipClusterFixture();
        await cluster.StartAsync();
        var leader = cluster.Nodes[0];
        await cluster.ElectAsync(leader);
        Equal(1L, leader.Term);
        Equal(1L, leader.Log.LastAppliedIndex);
        False(leader.LeadershipToken.IsCancellationRequested);

        await cluster.ReplicateToAllVotersAsync(leader);
        foreach (var node in cluster.Nodes[1..VoterCount])
            Equal(1L, node.Log.LastEntryIndex);
        Equal(0L, cluster.Joiner.Log.LastEntryIndex);
    }

    // #127: a round completes once a majority acknowledges it, so a follower whose request is still held lags behind
    [Fact(Timeout = TestTimeouts.Default)]
    public static async Task ReplicationToAllVotersReachesLaggingFollower()
    {
        await using var cluster = new MembershipClusterFixture();
        await cluster.StartAsync();
        var (leader, lagging) = (cluster.Nodes[0], cluster.Nodes[2]);
        var target = lagging.Id;
        Func<PendingMessage, MessageAction> filter = message => message.TargetId == target && message.MessageType is RaftMessageType.AppendEntries
            ? MessageAction.Hold
            : MessageAction.Deliver;
        await cluster.ElectAsync(leader, filter: filter);
        await cluster.PumpAsync(leader, leader.ForceReplicationAsync(TestToken).AsTask(), filter);
        True(lagging.Log.LastEntryIndex < leader.Log.LastEntryIndex);

        await cluster.ReplicateToAllVotersAsync(leader);
        foreach (var node in cluster.Nodes[1..VoterCount])
            Equal(leader.Log.LastEntryIndex, node.Log.LastEntryIndex);
    }

    [Fact(Timeout = TestTimeouts.Default)]
    public static async Task ConfigurationIsActiveOnAppend()
    {
        await using var cluster = new MembershipClusterFixture();
        await cluster.StartAsync();
        var leader = cluster.Nodes[0];
        await cluster.ElectAsync(leader);
        var removed = cluster.Voters[^1];

        // the removal is appended, but not replicated
        await leader.DetectAsync(removed);
        var removalIndex = leader.Log.LastEntryIndex;
        True(removalIndex > leader.Log.LastCommittedEntryIndex);
        DoesNotContain(leader.Members, member => object.Equals(member.EndPoint, removed));
        Contains(removed, (await leader.LoadConfigurationAsync()).Members);

        // the storage keeps the applied configuration
        await cluster.ReplicateUntilAppliedAsync(leader, removalIndex);
        DoesNotContain(removed, (await leader.LoadConfigurationAsync()).Members);
        Equal(removalIndex, await leader.LoadConfigurationVersionAsync());
        False(leader.IsMembershipLockHeld);
    }

    [Fact(Timeout = TestTimeouts.Default)]
    public static async Task JoinerCatchesUpBeforeItsAddressIsCommitted()
    {
        await using var cluster = new MembershipClusterFixture();
        await cluster.StartAsync();
        var leader = cluster.Nodes[0];
        await cluster.ElectAsync(leader);

        var addition = leader.AddAsync(cluster.Joiner.EndPoint, TestToken);
        await cluster.PumpAsync(leader, addition);
        True(await addition);
        Contains(cluster.Joiner.EndPoint, (await leader.LoadConfigurationAsync()).Members);
        True(cluster.Joiner.Log.LastEntryIndex >= 1L);
        False(leader.IsMembershipLockHeld);
    }
}
