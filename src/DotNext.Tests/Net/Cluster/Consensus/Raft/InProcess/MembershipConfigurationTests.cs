using System.Net;

namespace DotNext.Net.Cluster.Consensus.Raft.InProcess;

using static MembershipClusterFixture;

/// <summary>
/// Regressions for #18: a membership change must be built from the latest configuration in the log,
/// not from a stale applied configuration, so that it cannot resurrect a removed member.
/// </summary>
public sealed class MembershipConfigurationTests : RaftTest
{
    public enum Change
    {
        Add,
        Remove,
        Detect,
    }

    [Theory]
    [InlineData(Change.Add, false)]
    [InlineData(Change.Remove, false)]
    [InlineData(Change.Add, true)]
    [InlineData(Change.Remove, true)]
    public static async Task ManualChangePreservesDetectedRemoval(Change change, bool applyFirst)
    {
        await using var cluster = new MembershipClusterFixture();
        await cluster.StartAsync();
        var leader = cluster.Nodes[0];
        await cluster.ElectAsync(leader);

        // the detector's removal is appended, but replication is held, so it is not applied
        var removed = cluster.Voters[4];
        await leader.DetectAsync(removed);
        var removalIndex = leader.Log.LastEntryIndex;
        Equal(2L, removalIndex);
        if (applyFirst)
            await cluster.ReplicateUntilAppliedAsync(leader, removalIndex);
        else
            Contains(removed, (await leader.LoadConfigurationAsync()).Members);

        var (operation, expected) = change switch
        {
            Change.Add => (leader.AddAsync(cluster.Joiner.EndPoint, TestToken), Endpoints(cluster, 0, 1, 2, 3, 5)),
            _ => (leader.RemoveAsync(cluster.Voters[3], TestToken), Endpoints(cluster, 0, 1, 2)),
        };
        await cluster.PumpAsync(leader, operation);
        True(await operation);

        await AssertConfigurationAsync(leader, expected);
        await leader.PropagateConfigurationAsync();
        Equal(expected, leader.Members.Select(static member => member.EndPoint).ToHashSet());
        False(leader.IsMembershipLockHeld);
    }

    [Fact]
    public static async Task ConsecutiveDetectionsPreserveBothRemovals()
    {
        await using var cluster = new MembershipClusterFixture();
        await cluster.StartAsync();
        var leader = cluster.Nodes[0];
        await cluster.ElectAsync(leader);

        await leader.DetectAsync(cluster.Voters[4]);
        Equal(2L, leader.Log.LastEntryIndex);

        // the second detection runs while the first removal is still unapplied
        await cluster.PumpAsync(leader, leader.DetectAsync(cluster.Voters[3]));
        await cluster.ReplicateUntilAppliedAsync(leader, leader.Log.LastEntryIndex);

        await AssertConfigurationAsync(leader, Endpoints(cluster, 0, 1, 2));
    }

    [Theory]
    [InlineData(Change.Add)]
    [InlineData(Change.Remove)]
    [InlineData(Change.Detect)]
    public static async Task NewLeaderPreservesInheritedRemoval(Change change)
    {
        await using var cluster = new MembershipClusterFixture();
        await cluster.StartAsync();
        var oldLeader = cluster.Nodes[0];
        var newLeader = cluster.Nodes[1];
        await cluster.ElectAsync(oldLeader);

        // the removal reaches one follower only, then the old leader goes away
        var removalIndex = await oldLeader.AppendRemovalAsync(cluster.Voters[4]);
        await cluster.ReplicateOnlyToAsync(oldLeader, newLeader, removalIndex);
        await oldLeader.StopAsync(TestToken);

        // the new leader inherits the removal but has not applied it
        await cluster.ElectAsync(newLeader, passWriteBarrier: false);
        Equal(2L, newLeader.Term);
        True(newLeader.Log.LastAppliedIndex < removalIndex);
        Contains(cluster.Voters[4], (await newLeader.LoadConfigurationAsync()).Members);

        switch (change)
        {
            case Change.Add:
                var addition = newLeader.AddAsync(cluster.Joiner.EndPoint, TestToken);
                await cluster.PumpAsync(newLeader, addition);
                True(await addition);
                await AssertConfigurationAsync(newLeader, Endpoints(cluster, 0, 1, 2, 3, 5));
                break;
            case Change.Remove:
                var removal = newLeader.RemoveAsync(cluster.Voters[3], TestToken);
                await cluster.PumpAsync(newLeader, removal);
                True(await removal);
                await AssertConfigurationAsync(newLeader, Endpoints(cluster, 0, 1, 2));
                break;
            default:
                await cluster.PumpAsync(newLeader, newLeader.DetectAsync(cluster.Voters[0]));
                await cluster.ReplicateUntilAppliedAsync(newLeader, newLeader.Log.LastEntryIndex);
                await AssertConfigurationAsync(newLeader, Endpoints(cluster, 1, 2, 3));
                break;
        }
    }

    [Fact]
    public static async Task StaleTermDetectionDoesNotAppend()
    {
        await using var cluster = new MembershipClusterFixture();
        await cluster.StartAsync();
        var leader = cluster.Nodes[0];
        await cluster.ElectAsync(leader);
        var lastIndex = leader.Log.LastEntryIndex;

        await ThrowsAsync<NotLeaderException>(
            () => leader.RemoveUnavailableAsync(cluster.Voters[4], leader.Term - 1L, TestToken).AsTask());

        Equal(lastIndex, leader.Log.LastEntryIndex);
    }

    [Fact]
    public static async Task ChangeAppendsSingleEntry()
    {
        await using var cluster = new MembershipClusterFixture();
        await cluster.StartAsync();
        var leader = cluster.Nodes[0];
        await cluster.ElectAsync(leader);
        var lastIndex = leader.Log.LastEntryIndex;

        var removal = leader.RemoveAsync(cluster.Voters[4], TestToken);
        await cluster.PumpAsync(leader, removal);
        True(await removal);

        Equal(lastIndex + 1L, leader.Log.LastEntryIndex);
        await AssertConfigurationAsync(leader, Endpoints(cluster, 0, 1, 2, 3));
    }

    [Fact]
    public static async Task NoOpChangesAppendNothing()
    {
        await using var cluster = new MembershipClusterFixture();
        await cluster.StartAsync();
        var leader = cluster.Nodes[0];
        await cluster.ElectAsync(leader);
        var lastIndex = leader.Log.LastEntryIndex;

        var addition = leader.AddAsync(cluster.Voters[1], TestToken);
        await cluster.PumpAsync(leader, addition);
        False(await addition);

        var removal = leader.RemoveAsync(cluster.Joiner.EndPoint, TestToken);
        await cluster.PumpAsync(leader, removal);
        False(await removal);

        Equal(lastIndex, leader.Log.LastEntryIndex);
        False(leader.IsMembershipLockHeld);
    }

    [Fact]
    public static async Task ExplicitReaddAfterAppliedRemoval()
    {
        await using var cluster = new MembershipClusterFixture();
        await cluster.StartAsync();
        var leader = cluster.Nodes[0];
        await cluster.ElectAsync(leader);
        var member = cluster.Voters[4];

        // the removed node misses its own removal, as it would when it is actually gone
        var removal = leader.RemoveAsync(member, TestToken);
        var removedId = cluster.Nodes[4].Id;
        await cluster.PumpAsync(leader, removal, message => message.TargetId == removedId ? MessageAction.Drop : MessageAction.Deliver);
        True(await removal);
        await leader.PropagateConfigurationAsync();

        var addition = leader.AddAsync(member, TestToken);
        await cluster.PumpAsync(leader, addition);
        True(await addition);

        await AssertConfigurationAsync(leader, Endpoints(cluster, 0, 1, 2, 3, 4));
    }

    [Fact]
    public static async Task CanceledChangeReleasesLock()
    {
        await using var cluster = new MembershipClusterFixture();
        await cluster.StartAsync();
        var leader = cluster.Nodes[0];
        await cluster.ElectAsync(leader);
        await leader.DetectAsync(cluster.Voters[4]);

        // replication is held, so the change cannot complete
        using var source = CancellationTokenSource.CreateLinkedTokenSource(TestToken);
        var removal = leader.RemoveAsync(cluster.Voters[3], source.Token);
        await source.CancelAsync();
        await ThrowsAnyAsync<OperationCanceledException>(() => removal);
        False(leader.IsMembershipLockHeld);

        await cluster.ReplicateUntilAppliedAsync(leader, leader.Log.LastEntryIndex);
        await AssertConfigurationAsync(leader, Endpoints(cluster, 0, 1, 2, 3));
    }

    private static HashSet<EndPoint> Endpoints(MembershipClusterFixture cluster, params int[] nodes)
        => nodes.Select(i => cluster.Nodes[i].EndPoint).ToHashSet();

    private static async Task AssertConfigurationAsync(MembershipNode node, HashSet<EndPoint> expected)
    {
        Equal(expected, (await node.LoadConfigurationAsync()).Members.ToHashSet());
        Equal(node.Log.LastEntryIndex, await node.LoadConfigurationVersionAsync());
    }
}
