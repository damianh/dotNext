namespace DotNext.Net.Cluster.Consensus.Raft.InProcess;

using Membership;
using static MembershipClusterFixture;

public sealed class MembershipConfigurationTests : RaftTest
{
    [Fact]
    public static async Task ExistingNoOpChecksDoNotAppendBarriers()
    {
        await using var cluster = new MembershipClusterFixture();
        await cluster.StartAsync();
        var leader = cluster.Leader;
        var index = leader.Log.LastEntryIndex;
        False(await leader.AddAsync(cluster.Nodes[1].EndPoint, TestToken));
        False(await leader.RemoveAsync(cluster.Nodes[^1].EndPoint, TestToken));
        Equal(index, leader.Log.LastEntryIndex);
        False(leader.IsMembershipLockHeld);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public static async Task AlreadyRemovedMemberIsRevalidatedAfterBarrier(bool automatic)
    {
        await using var cluster = new MembershipClusterFixture();
        await cluster.StartAsync();
        var leader = cluster.Leader;
        var removed = cluster.Nodes[4];
        cluster.Hold(leader);
        await cluster.PumpAsync(leader, leader.DetectAsync(leader.CaptureCaller(), removed.EndPoint, leader.ConsensusToken));
        var removalIndex = leader.Log.LastEntryIndex;
        Task change = automatic
            ? leader.DetectAsync(leader.CaptureCaller(), removed.EndPoint, leader.ConsensusToken)
            : leader.RemoveAsync(removed.EndPoint, TestToken);
        await cluster.PumpAsync(leader, change);
        if (change is Task<bool> manual)
            False(await manual);
        var (_, version) = await ((IClusterConfigurationStorage)leader.Storage).LoadConfigurationAsync(TestToken);
        Equal(removalIndex, version);
        DoesNotContain(removed.EndPoint, (await ConfigurationAsync(leader)).Members);
        False(leader.IsMembershipLockHeld);
    }

    [Theory]
    [InlineData("add")]
    [InlineData("remove")]
    [InlineData("detect")]
    public static async Task CancellationDuringBarrierPreservesOwnershipAndMembership(string operation)
    {
        await using var cluster = new MembershipClusterFixture();
        await cluster.StartAsync();
        var leader = cluster.Leader;
        cluster.Hold(leader);
        cluster.Network.Release(leader.EndPoint, cluster.Nodes[^1].EndPoint);
        using var source = CancellationTokenSource.CreateLinkedTokenSource(TestToken, leader.ConsensusToken);
        Task change = operation switch
        {
            "add" => leader.AddAsync(cluster.Nodes[^1].EndPoint, source.Token),
            "remove" => leader.RemoveAsync(cluster.Nodes[4].EndPoint, source.Token),
            _ => leader.DetectAsync(leader.CaptureCaller(), cluster.Nodes[4].EndPoint, source.Token),
        };
        await cluster.Network.WaitForReplicationAsync(leader.EndPoint, cluster.Nodes[1].EndPoint, TestToken);
        True(leader.IsMembershipLockHeld);
        source.Cancel();
        if (operation is "detect")
        {
            await change;
            Equal(source.Token, IsAssignableFrom<OperationCanceledException>(Single(leader.Errors.Exceptions)).CancellationToken);
        }
        else
        {
            Equal(source.Token, (await ThrowsAsync<OperationCanceledException>(change)).CancellationToken);
        }
        False(leader.IsMembershipLockHeld);
        False(leader.ConsensusToken.IsCancellationRequested);
        Contains(cluster.Nodes[4].EndPoint, (await ConfigurationAsync(leader)).Members);
        await cluster.PumpAsync(leader, leader.ForceReplicationAsync(TestToken).AsTask());
        var removal = leader.RemoveAsync(cluster.Nodes[3].EndPoint, TestToken);
        await cluster.PumpAsync(leader, removal);
        True(await removal);
    }

    [Theory]
    [InlineData(false, false)]
    [InlineData(true, false)]
    [InlineData(false, true)]
    [InlineData(true, true)]
    public static async Task ManualChangePreservesDetectedRemoval(bool add, bool applyFirst)
    {
        await using var cluster = new MembershipClusterFixture();
        await cluster.StartAsync();
        var leader = cluster.Leader;
        cluster.Hold(leader);
        var detection = leader.DetectAsync(leader.CaptureCaller(), cluster.Nodes[4].EndPoint, leader.LeadershipToken);
        await cluster.PumpAsync(leader, detection);
        False(leader.IsMembershipLockHeld);
        var removalIndex = leader.Log.LastEntryIndex;
        True(removalIndex > leader.Log.LastAppliedIndex);

        if (applyFirst)
        {
            await cluster.PumpAsync(leader, leader.ForceReplicationAsync(TestToken).AsTask());
            await leader.Log.WaitForApplyAsync(removalIndex, TestToken);
            await leader.WaitForMembersAsync(members => !members.Contains(cluster.Nodes[4].EndPoint));
        }
        else
        {
            Contains(cluster.Nodes[4].EndPoint, (await ConfigurationAsync(leader)).Members);
        }

        var change = add
            ? leader.AddAsync(cluster.Nodes[^1].EndPoint, TestToken)
            : leader.RemoveAsync(cluster.Nodes[3].EndPoint, TestToken);
        await cluster.PumpAsync(leader, change);
        True(await change);
        var config = await ConfigurationAsync(leader);
        TestContext.Current.TestOutputHelper.WriteLine(
            $"Detected removal at {removalIndex}; final configuration at {leader.Log.LastAppliedIndex}: " +
            string.Join(", ", config.Members));
        DoesNotContain(cluster.Nodes[4].EndPoint, config.Members);
        var expected = cluster.Nodes[..5].Select(node => node.EndPoint).ToHashSet();
        expected.Remove(cluster.Nodes[4].EndPoint);
        if (add)
            expected.Add(cluster.Nodes[^1].EndPoint);
        else
            expected.Remove(cluster.Nodes[3].EndPoint);
        True(expected.SetEquals(config.Members));
        await leader.WaitForMembersAsync(members => members.SetEquals(expected));
        await AssertVersionAsync(leader);
    }

    [Fact]
    public static async Task ConsecutiveDetectionsPreserveBothRemovals()
    {
        await using var cluster = new MembershipClusterFixture();
        await cluster.StartAsync();
        var leader = cluster.Leader;
        cluster.Hold(leader);
        foreach (var node in cluster.Nodes[3..5])
        {
            var detection = leader.DetectAsync(leader.CaptureCaller(), node.EndPoint, leader.LeadershipToken);
            await cluster.PumpAsync(leader, detection);
            False(leader.IsMembershipLockHeld);
        }
        var removalIndex = leader.Log.LastEntryIndex;
        await cluster.PumpAsync(leader, leader.ForceReplicationAsync(TestToken).AsTask());
        await leader.Log.WaitForApplyAsync(removalIndex, TestToken);
        var expected = cluster.Nodes[..3].Select(node => node.EndPoint).ToHashSet();
        True(expected.SetEquals((await ConfigurationAsync(leader)).Members));
        await leader.WaitForMembersAsync(members => members.SetEquals(expected));
        await AssertVersionAsync(leader);
    }

    [Fact]
    public static async Task ExplicitReadditionAfterAppliedRemovalStillWorks()
    {
        await using var cluster = new MembershipClusterFixture();
        await cluster.StartAsync();
        var leader = cluster.Leader;
        var removed = cluster.Nodes[4];
        await leader.DetectAsync(leader.CaptureCaller(), removed.EndPoint, leader.LeadershipToken);
        var index = leader.Log.LastEntryIndex;
        await leader.ForceReplicationAsync(TestToken);
        await leader.Log.WaitForApplyAsync(index, TestToken);
        await leader.WaitForMembersAsync(members => !members.Contains(removed.EndPoint));
        // Give the removed node a real catch-up suffix before explicitly rejoining.
        await leader.ReplicateAsync(new EmptyLogEntry { Term = leader.Term }, TestToken);
        True(await leader.AddAsync(removed.EndPoint, TestToken));
        Contains(removed.EndPoint, (await ConfigurationAsync(leader)).Members);
        await leader.WaitForMembersAsync(members => members.Contains(removed.EndPoint));
    }

    [Theory]
    [InlineData("add")]
    [InlineData("remove")]
    [InlineData("detect")]
    public static async Task NewLeaderPreservesInheritedRemoval(string operation)
    {
        await using var cluster = new MembershipClusterFixture();
        await cluster.StartAsync();
        var oldLeader = cluster.Leader;
        await oldLeader.ForceReplicationAsync(TestToken);
        foreach (var node in cluster.Nodes[..5])
            await node.Log.WaitForApplyAsync(1L, TestToken);
        cluster.Hold(oldLeader);
        var detection = oldLeader.DetectAsync(oldLeader.CaptureCaller(), cluster.Nodes[4].EndPoint, oldLeader.LeadershipToken);
        await cluster.PumpAsync(oldLeader, detection);
        var removalIndex = oldLeader.Log.LastEntryIndex;
        var replication = oldLeader.ForceReplicationAsync(TestToken).AsTask();
        var successor = cluster.Nodes[1];
        await cluster.Network.DeliverAsync(await cluster.Network.WaitForReplicationAsync(
            oldLeader.EndPoint, successor.EndPoint, TestToken));
        Equal(removalIndex, successor.Log.LastEntryIndex);
        True(successor.Log.LastCommittedEntryIndex < removalIndex);
        True(oldLeader.Log.LastCommittedEntryIndex < removalIndex);
        await oldLeader.StopAsync(TestToken);
        await ThrowsAsync<NotLeaderException>(replication);

        // Prevent competing elections from delivering requests. Only the chosen
        // successor's genuine pre-votes, votes and later replication are pumped.
        foreach (var node in cluster.Nodes[1..])
            cluster.Hold(node);
        var elected = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        successor.LeaderChanged += (_, leader) =>
        {
            if (leader is not null && leader.Id == successor.Id)
                elected.TrySetResult();
        };
        successor.StartElectionTimer();
        cluster.TimeProvider.Advance(TimeSpan.FromMilliseconds(100));
        await cluster.PumpAsync(successor, elected.Task);
        True(successor.Log.LastAppliedIndex < removalIndex);
        Contains(cluster.Nodes[4].EndPoint, (await ConfigurationAsync(successor)).Members);

        var expected = cluster.Nodes[..4].Select(node => node.EndPoint).ToHashSet();
        Task change;
        switch (operation)
        {
            case "add":
                change = successor.AddAsync(cluster.Nodes[^1].EndPoint, TestToken);
                expected.Add(cluster.Nodes[^1].EndPoint);
                break;
            case "remove":
                change = successor.RemoveAsync(cluster.Nodes[3].EndPoint, TestToken);
                expected.Remove(cluster.Nodes[3].EndPoint);
                break;
            default:
                change = successor.DetectAsync(successor.CaptureCaller(), cluster.Nodes[3].EndPoint, successor.ConsensusToken);
                expected.Remove(cluster.Nodes[3].EndPoint);
                break;
        }
        await cluster.PumpAsync(successor, change);
        var finalIndex = successor.Log.LastEntryIndex;
        await cluster.PumpAsync(successor, successor.ForceReplicationAsync(TestToken).AsTask());
        await successor.Log.WaitForApplyAsync(finalIndex, TestToken);
        var config = await ConfigurationAsync(successor);
        TestContext.Current.TestOutputHelper.WriteLine(
            $"Inherited uncommitted removal {removalIndex}; elected term {successor.Term}; final members: " +
            string.Join(", ", config.Members));
        DoesNotContain(cluster.Nodes[4].EndPoint, config.Members);
        True(expected.SetEquals(config.Members));
        await successor.WaitForMembersAsync(members => members.SetEquals(expected));
        await AssertVersionAsync(successor);
    }

    private static ValueTask<IClusterConfiguration<System.Net.EndPoint>> ConfigurationAsync(MembershipNode node)
        => ((IClusterConfigurationStorage<System.Net.EndPoint>)node.Storage).LoadConfigurationAsync(TestToken);

    private static async Task AssertVersionAsync(MembershipNode node)
    {
        var (_, version) = await ((IClusterConfigurationStorage)node.Storage).LoadConfigurationAsync(TestToken);
        Equal(node.Log.LastEntryIndex, version);
        Equal(node.Log.LastEntryIndex, node.Log.LastAppliedIndex);
    }
}
