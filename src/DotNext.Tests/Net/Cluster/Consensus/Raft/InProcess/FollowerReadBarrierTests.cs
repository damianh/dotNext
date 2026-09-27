namespace DotNext.Net.Cluster.Consensus.Raft.InProcess;

using static MembershipClusterFixture;

/// <summary>
/// Regressions for #19: a follower's read barrier must obtain a read index that its presumed leader
/// has confirmed with a quorum, and wait until that index is applied locally.
/// </summary>
public sealed class FollowerReadBarrierTests : RaftTest
{
    [Theory(Timeout = TestTimeouts.Default)]
    [InlineData(false)]
    [InlineData(true)]
    public static async Task IsolatedFormerLeaderCannotAuthorizeFollowerRead(bool formerLeaderObservesNewTerm)
    {
        await using var cluster = new MembershipClusterFixture();
        await cluster.StartAsync();
        var (oldLeader, follower, newLeader) = (cluster.Nodes[0], cluster.Nodes[1], cluster.Nodes[2]);
        await cluster.ElectAsync(oldLeader);
        var olderWrite = await ReplicateAsync(cluster, oldLeader, filter: null);
        await cluster.PumpAsync(oldLeader, follower.Log.WaitForApplyAsync(olderWrite, TestToken).AsTask());
        var oldLeadership = oldLeader.LeadershipToken;

        // {0, 1} | {2, 3, 4}: the majority elects a new leader and acknowledges a newer write.
        // The former leader's heartbeats fired by the election deadline stay queued.
        var minority = new HashSet<ClusterMemberId> { oldLeader.Id, follower.Id };
        MessageAction ToMajority(PendingMessage message)
            => minority.Contains(message.TargetId) ? MessageAction.Drop : MessageAction.Deliver;
        await cluster.ElectAsync(newLeader, filter: ToMajority);
        Equal(2L, newLeader.Term);
        var newerWrite = await ReplicateAsync(cluster, newLeader, ToMajority);
        True(newerWrite > olderWrite);
        Equal(olderWrite, follower.Log.LastAppliedIndex);
        False(oldLeadership.IsCancellationRequested);

        // The read starts after the acknowledgment; its request reaches the former leader first.
        var read = ReadAsync(follower);
        var synchronize = await cluster.Network.WaitForMessageAsync(
            follower.EndPoint, oldLeader.EndPoint, RaftMessageType.Synchronize, TestToken);
        var answered = cluster.Network.DeliverAsync(synchronize);
        await cluster.PumpAllAsync(answered, message => message switch
        {
            { MessageType: RaftMessageType.Synchronize } => MessageAction.Hold,
            _ when minority.Contains(message.SourceId) == minority.Contains(message.TargetId) => MessageAction.Deliver,
            _ when formerLeaderObservesNewTerm && message.SourceId == oldLeader.Id => MessageAction.Deliver,
            _ => MessageAction.Drop,
        });

        var readIndex = await IsType<Task<long?>>(synchronize.Completion);
        True(readIndex is null || readIndex >= newerWrite,
            $"Isolated former leader authorized read index {readIndex}; index {newerWrite} was acknowledged by the new leader.");
        True(oldLeadership.IsCancellationRequested);
        False(read.IsCompleted);

        // After the partition heals, the follower's retry reaches the new leader.
        await cluster.PumpAllAsync(read, leader: newLeader);
        var observed = await read;
        True(observed >= newerWrite, $"Read observed applied index {observed}; index {newerWrite} was acknowledged.");
    }

    [Fact(Timeout = TestTimeouts.Default)]
    public static async Task NewLeaderReadIndexCoversInheritedCommittedWrite()
    {
        await using var cluster = new MembershipClusterFixture();
        await cluster.StartAsync();
        var (oldLeader, newLeader, follower) = (cluster.Nodes[0], cluster.Nodes[1], cluster.Nodes[2]);
        await cluster.ElectAsync(oldLeader);
        await cluster.PumpAsync(oldLeader, Task.WhenAll(cluster.Nodes[1..MembershipClusterFixture.VoterCount]
            .Select(static node => node.Log.WaitForApplyAsync(1L, TestToken).AsTask())));

        // The write is committed with the acknowledgments of the two followers only,
        // so neither of them learns the new commit index.
        var write = oldLeader.ReplicateAsync(new EmptyLogEntry { Term = oldLeader.Term }, TestToken).AsTask();
        foreach (var target in new[] { newLeader, follower })
        {
            while (target.Log.LastEntryIndex < 2L)
            {
                await cluster.Network.DeliverAsync(await cluster.Network.WaitForMessageAsync(
                    oldLeader.EndPoint, target.EndPoint, RaftMessageType.AppendEntries, TestToken));
            }
        }

        await write;
        var acknowledged = oldLeader.Log.LastCommittedEntryIndex;
        Equal(2L, acknowledged);
        Equal(1L, newLeader.Log.LastCommittedEntryIndex);
        Equal(1L, follower.Log.LastCommittedEntryIndex);
        await oldLeader.StopAsync(TestToken);

        // The new leader has not committed its no-op yet, so its own commit index predates the write.
        await cluster.ElectAsync(newLeader, passWriteBarrier: false);
        Equal(1L, newLeader.Log.LastCommittedEntryIndex);

        // the follower learns the new leader from its first heartbeat
        await cluster.Network.DeliverAsync(await cluster.Network.WaitForMessageAsync(
            newLeader.EndPoint, follower.EndPoint, RaftMessageType.AppendEntries, TestToken));
        Equal(newLeader.EndPoint, follower.Leader?.EndPoint);
        Equal(1L, follower.Log.LastCommittedEntryIndex);

        var read = ReadAsync(follower);
        var synchronize = await cluster.Network.WaitForMessageAsync(
            follower.EndPoint, newLeader.EndPoint, RaftMessageType.Synchronize, TestToken);
        await cluster.PumpAllAsync(cluster.Network.DeliverAsync(synchronize));
        var readIndex = await IsType<Task<long?>>(synchronize.Completion);
        True(readIndex >= acknowledged, $"New leader authorized read index {readIndex}; index {acknowledged} was acknowledged.");

        await cluster.PumpAllAsync(read, leader: newLeader);
        True(await read >= acknowledged);
    }

    [Fact(Timeout = TestTimeouts.Default)]
    public static async Task HealthyFollowerReadWaitsForQuorumConfirmation()
    {
        await using var cluster = new InProcessClusterFixture(5);
        await cluster.StartLeaderAsync();
        await cluster.DeliverRoundAsync();
        var follower = cluster.Nodes[1];
        Equal(cluster.Leader.AuditTrail.LastCommittedEntryIndex, follower.AuditTrail.LastCommittedEntryIndex);

        // The follower is already in sync; the leader must still confirm its term before answering.
        var read = follower.ApplyReadBarrierAsync(ReadBarrierType.Strong, TestToken).AsTask();
        var round = cluster.PendingRoundAsync();
        await Task.WhenAny(read, round);
        False(read.IsCompleted, "The read completed without a quorum round.");

        foreach (var message in await round)
            await cluster.Network.DeliverAsync(message);
        await read;
        False(cluster.Leader.LeadershipToken.IsCancellationRequested);
    }

    [Fact(Timeout = TestTimeouts.Default)]
    public static async Task LeaderWithoutQuorumDoesNotReturnReadIndex()
    {
        await using var cluster = new InProcessClusterFixture(5);
        await cluster.StartLeaderAsync();
        await cluster.DeliverRoundAsync();
        var leadership = cluster.Leader.LeadershipToken;

        // the production client used by the follower's read barrier
        var response = cluster.Nodes[1].GetMember(cluster.Leader.EndPoint).As<IRaftClusterMember>()
            .SynchronizeAsync(cluster.Nodes[1].AuditTrail.LastCommittedEntryIndex, TestToken);
        var round = cluster.PendingRoundAsync();
        await Task.WhenAny(response, round);
        False(response.IsCompleted, "The leader answered without a quorum round.");

        // only the leader and one follower remain reachable
        var messages = await round;
        await cluster.Network.DeliverAsync(messages[0]);
        foreach (var message in messages.Skip(1))
            cluster.Network.Drop(message);

        Null(await response.WaitAsync(DefaultTimeout, TestToken));
        True(leadership.IsCancellationRequested);
    }

    [Fact(Timeout = TestTimeouts.Default)]
    public static async Task CanceledFollowerReadDoesNotStrandLeader()
    {
        await using var cluster = new InProcessClusterFixture(5);
        await cluster.StartLeaderAsync();
        await cluster.DeliverRoundAsync();
        var follower = cluster.Nodes[1];
        var leadership = cluster.Leader.LeadershipToken;

        using var cancellation = CancellationTokenSource.CreateLinkedTokenSource(TestToken);
        var read = follower.ApplyReadBarrierAsync(ReadBarrierType.Strong, cancellation.Token).AsTask();
        var round = cluster.PendingRoundAsync();
        await Task.WhenAny(read, round);
        False(read.IsCompleted, "The read completed without a quorum round.");

        await cancellation.CancelAsync();
        await ThrowsAnyAsync<OperationCanceledException>(() => read.WaitAsync(DefaultTimeout, TestToken));

        foreach (var message in await round)
            await cluster.Network.DeliverAsync(message);
        await cluster.DeliverRoundAsync();
        False(leadership.IsCancellationRequested);
    }

    [Fact(Timeout = TestTimeouts.Default)]
    public static async Task LaggingFollowerReadWaitsForSnapshotApplication()
    {
        const int laggingFollower = 4;
        await using var cluster = new InProcessClusterFixture(5);
        await cluster.StartLeaderAsync(laggingFollower);
        var lagging = cluster.Nodes[laggingFollower];
        Equal(0L, lagging.AuditTrail.LastEntryIndex);
        cluster.Network.Hold(lagging.EndPoint, cluster.Leader.EndPoint);

        var read = lagging.ApplyReadBarrierAsync(ReadBarrierType.Strong, TestToken).AsTask();
        var synchronize = await cluster.Network.WaitForMessageAsync(
            lagging.EndPoint, cluster.Leader.EndPoint, RaftMessageType.Synchronize, TestToken);
        var answered = cluster.Network.DeliverAsync(synchronize);

        // a quorum confirms the read index while the lagging follower's snapshot is held
        for (var i = 1; i < laggingFollower; i++)
            await cluster.Network.DeliverAsync(await cluster.PendingAsync(i, RaftMessageType.AppendEntries));
        var snapshot = await cluster.Network.WaitForReplicationAsync(cluster.Leader.EndPoint, lagging.EndPoint, TestToken);
        Equal(RaftMessageType.InstallSnapshot, snapshot.MessageType);
        await answered;
        var readIndex = await IsType<Task<long?>>(synchronize.Completion);
        NotNull(readIndex);
        True(lagging.AuditTrail.LastCommittedEntryIndex < readIndex);
        False(read.IsCompleted);

        await cluster.Network.DeliverAsync(snapshot);
        await read.WaitAsync(DefaultTimeout, TestToken);
        True(lagging.AuditTrail.LastCommittedEntryIndex >= readIndex);
    }

    private static async Task<long> ReplicateAsync(MembershipClusterFixture cluster, MembershipNode leader,
        Func<PendingMessage, MessageAction> filter)
    {
        var write = leader.ReplicateAsync(new EmptyLogEntry { Term = leader.Term }, TestToken).AsTask();
        await cluster.PumpAsync(leader, write, filter);
        return leader.Log.LastCommittedEntryIndex;
    }

    private static async Task<long> ReadAsync(MembershipNode node)
    {
        await node.ApplyReadBarrierAsync(ReadBarrierType.Strong, TestToken);
        return node.Log.LastAppliedIndex;
    }
}
