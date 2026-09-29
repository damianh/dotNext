namespace DotNext.Net.Cluster.Consensus.Raft.InProcess;

using static MembershipClusterFixture;

/// <summary>
/// Regressions for #54: membership warm-up must accept a node whose log already matches the leader's
/// committed prefix, and must still wait for a lagging node to actually catch up.
/// </summary>
public sealed class MembershipWarmUpTests : RaftTest
{
    [Fact(Timeout = TestTimeouts.Default)]
    public static async Task ReaddOfCaughtUpNodeCompletesWarmUp()
    {
        await using var cluster = new MembershipClusterFixture();
        await cluster.StartAsync();
        var leader = cluster.Nodes[0];
        await cluster.ElectAsync(leader);
        var node = cluster.Nodes[4];

        // The removed node stops receiving replication once the removal is appended, so it misses its own removal.
        // It appends the same entry itself, so its log keeps matching the leader's.
        var removal = leader.RemoveAsync(node.EndPoint, TestToken);
        await cluster.PumpAsync(leader, removal);
        True(await removal);
        var removalIndex = leader.Log.LastEntryIndex;
        if (node.Log.LastEntryIndex < removalIndex)
            Equal(removalIndex, await node.AppendRemovalAsync(node.EndPoint));
        DoesNotContain(node.EndPoint, leader.Members.Select(static member => member.EndPoint));

        Equal(removalIndex, leader.Log.LastCommittedEntryIndex);
        Equal(removalIndex, node.Log.LastEntryIndex);
        Equal(await leader.Log.GetTermAsync(removalIndex, TestToken), await node.Log.GetTermAsync(removalIndex, TestToken));

        // warm-up only sees empty heartbeats acknowledged as Replicated: no new application write arrives
        var nodeId = node.Id;
        var warmUpRounds = new HashSet<long>();
        var addition = leader.AddAsync(node.EndPoint, TestToken);
        await cluster.PumpAsync(leader, addition, message =>
        {
            // The pump may classify the same message more than once. Once the configuration is appended,
            // the node is a member and receives regular replication.
            if (message.TargetId == nodeId && message.MessageType is RaftMessageType.AppendEntries && leader.Log.LastEntryIndex == removalIndex)
                warmUpRounds.Add(message.Id);

            return MessageAction.Deliver;
        });
        True(await addition);

        Single(warmUpRounds);
        Equal(removalIndex + 1L, leader.Log.LastEntryIndex); // the configuration is the only new entry
        await leader.Log.WaitForApplyAsync(leader.Log.LastEntryIndex, TestToken);
        Contains(node.EndPoint, (await leader.LoadConfigurationAsync()).Members);
        False(leader.IsMembershipLockHeld);
    }

    [Fact(Timeout = TestTimeouts.Default)]
    public static async Task LaggingJoinerIsAddedOnlyAfterCatchingUp()
    {
        await using var cluster = new MembershipClusterFixture();
        await cluster.StartAsync();
        var leader = cluster.Nodes[0];
        await cluster.ElectAsync(leader);
        var joiner = cluster.Joiner;

        var watermark = leader.Log.LastCommittedEntryIndex;
        True(watermark > 0L);
        Equal(0L, joiner.Log.LastEntryIndex);

        // when the configuration entry is appended, the joiner must already hold the committed prefix
        var lastIndex = leader.Log.LastEntryIndex;
        long? joinerIndexAtConfiguration = null;
        var joinerId = joiner.Id;
        var warmUpRounds = new HashSet<long>();
        var addition = leader.AddAsync(joiner.EndPoint, TestToken);
        await cluster.PumpAsync(leader, addition, message =>
        {
            if (joinerIndexAtConfiguration is null && leader.Log.LastEntryIndex > lastIndex)
                joinerIndexAtConfiguration = joiner.Log.LastEntryIndex;

            if (message.TargetId == joinerId)
                warmUpRounds.Add(message.Id);

            return MessageAction.Deliver;
        });
        True(await addition);

        // the empty heartbeat is rejected, so warm-up waits for a round that delivers the missing entries
        True(warmUpRounds.Count >= 2, $"warm-up took {warmUpRounds.Count} round(s)");
        True(joinerIndexAtConfiguration >= watermark, $"joiner held {joinerIndexAtConfiguration} when the configuration was appended; expected at least {watermark}");
        Equal(await leader.Log.GetTermAsync(watermark, TestToken), await joiner.Log.GetTermAsync(watermark, TestToken));
        Contains(joiner.EndPoint, (await leader.LoadConfigurationAsync()).Members);
    }

    [Fact(Timeout = TestTimeouts.Default)]
    public static async Task RejectingJoinerIsNotAdded()
    {
        await using var cluster = new MembershipClusterFixture();
        await cluster.StartAsync();
        var leader = cluster.Nodes[0];
        await cluster.ElectAsync(leader);
        var joiner = cluster.Joiner;
        var lastIndex = leader.Log.LastEntryIndex;

        // the joiner answers every warm-up round with a rejection
        await ((IPersistentState)joiner.Log).UpdateTermAsync(leader.Term + 1L, false, TestToken);
        var addition = leader.AddAsync(joiner.EndPoint, TestToken);
        await cluster.PumpAsync(leader, addition);

        False(await addition);
        Equal(0L, joiner.Log.LastEntryIndex);
        Equal(lastIndex, leader.Log.LastEntryIndex);
        DoesNotContain(joiner.EndPoint, (await leader.LoadConfigurationAsync()).Members);
        False(leader.IsMembershipLockHeld);
    }
}
