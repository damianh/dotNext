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

        await cluster.PumpAsync(leader, leader.ForceReplicationAsync(TestToken).AsTask());
        foreach (var node in cluster.Nodes[1..VoterCount])
            Equal(1L, node.Log.LastEntryIndex);
        Equal(0L, cluster.Joiner.Log.LastEntryIndex);
    }

    [Fact(Timeout = TestTimeouts.Default)]
    public static async Task AppliedConfigurationReachesMembersOnlyWhenPropagated()
    {
        await using var cluster = new MembershipClusterFixture();
        await cluster.StartAsync();
        var leader = cluster.Nodes[0];
        await cluster.ElectAsync(leader);
        var removed = cluster.Voters[^1];

        var removal = leader.RemoveAsync(removed, TestToken);
        await cluster.PumpAsync(leader, removal);
        True(await removal);
        DoesNotContain(removed, (await leader.LoadConfigurationAsync()).Members);
        Equal(leader.Log.LastEntryIndex, await leader.LoadConfigurationVersionAsync());
        Contains(leader.Members, member => object.Equals(member.EndPoint, removed));

        await leader.PropagateConfigurationAsync();
        DoesNotContain(leader.Members, member => object.Equals(member.EndPoint, removed));
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
