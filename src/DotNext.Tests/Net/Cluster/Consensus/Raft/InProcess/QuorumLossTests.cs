namespace DotNext.Net.Cluster.Consensus.Raft.InProcess;

public sealed class QuorumLossTests : RaftTest
{
    [Theory]
    [InlineData(2)]
    [InlineData(4)]
    public static async Task HalfUnavailableReleasesLeaderAndShutdown(int memberCount)
    {
        var cluster = new InProcessClusterFixture(memberCount);
        var operationFailure = await Record.ExceptionAsync(() => LoseQuorumAsync(cluster));
        var disposalFailure = await Record.ExceptionAsync(async () => await cluster.DisposeAsync());

        // Report both failures: a broken shutdown must not hide the stuck caller.
        Multiple(() => Null(operationFailure), () => Null(disposalFailure));
    }

    private static async Task LoseQuorumAsync(InProcessClusterFixture cluster)
    {
        var memberCount = cluster.Nodes.Length;
        await cluster.StartLeaderAsync();

        var leadership = cluster.Leader.LeadershipToken;
        var replication = cluster.Leader.ForceReplicationAsync(TestToken).AsTask();
        await CompleteHalfUnavailableRoundAsync(cluster, await cluster.PendingRoundAsync());

        Empty(cluster.Network.PendingMessages);
        TestContext.Current.TestOutputHelper.WriteLine(
            $"All {memberCount} contributions supplied, including the leader; " +
            $"{memberCount / 2} members unavailable. Waiting for leader step-down and shutdown.");
        await ThrowsAsync<NotLeaderException>(replication.WaitAsync(DefaultTimeout, TestToken));
        True(leadership.IsCancellationRequested);
        await cluster.Leader.StopAsync(TestToken).WaitAsync(DefaultTimeout, TestToken);
    }

    [Theory]
    [InlineData(2, false)]
    [InlineData(2, true)]
    [InlineData(4, false)]
    [InlineData(4, true)]
    public static async Task CancelingCallerDoesNotStrandReplication(int memberCount, bool loseQuorum)
    {
        await using var cluster = new InProcessClusterFixture(memberCount);
        await cluster.StartLeaderAsync();
        var leadership = cluster.Leader.LeadershipToken;
        var replication = cluster.Leader.ForceReplicationAsync(TestToken).AsTask();
        var messages = await cluster.PendingRoundAsync();
        using var cancellation = new CancellationTokenSource();
        var canceled = cluster.Leader.ForceReplicationAsync(cancellation.Token).AsTask();
        cancellation.Cancel();
        var exception = await ThrowsAnyAsync<OperationCanceledException>(canceled.WaitAsync(DefaultTimeout, TestToken));
        Equal(cancellation.Token, exception.CancellationToken);
        False(replication.IsCompleted);
        False(leadership.IsCancellationRequested);

        if (loseQuorum)
        {
            await CompleteHalfUnavailableRoundAsync(cluster, messages);
            await ThrowsAsync<NotLeaderException>(replication.WaitAsync(DefaultTimeout, TestToken));
            True(leadership.IsCancellationRequested);
        }
        else
        {
            foreach (var message in messages)
                await cluster.Network.DeliverAsync(message);
            await replication.WaitAsync(DefaultTimeout, TestToken);
            False(leadership.IsCancellationRequested);
        }
    }

    [Theory]
    [InlineData(2, false)]
    [InlineData(2, true)]
    [InlineData(4, false)]
    [InlineData(4, true)]
    public static async Task ShutdownDrainsHeldReplication(int memberCount, bool dispose)
    {
        await using var cluster = new InProcessClusterFixture(memberCount);
        await cluster.StartLeaderAsync();
        var leadership = cluster.Leader.LeadershipToken;
        var replication = cluster.Leader.ForceReplicationAsync(TestToken).AsTask();
        var messages = await cluster.PendingRoundAsync();

        var shutdown = dispose ? cluster.Leader.DisposeAsync().AsTask() : cluster.Leader.StopAsync(TestToken);
        await shutdown.WaitAsync(DefaultTimeout, TestToken);
        await ThrowsAsync<NotLeaderException>(replication.WaitAsync(DefaultTimeout, TestToken));
        True(leadership.IsCancellationRequested);
        foreach (var message in messages)
            await ThrowsAsync<MemberUnavailableException>(message.Completion);
        Empty(cluster.Network.PendingMessages);
    }

    private static async Task CompleteHalfUnavailableRoundAsync(InProcessClusterFixture cluster, PendingMessage[] messages)
    {
        for (var i = 0; i < messages.Length; i++)
        {
            if (i < cluster.Nodes.Length / 2 - 1)
            {
                await cluster.Network.DeliverAsync(messages[i]);
                var response = await IsType<Task<Result<ReplicationStatus>>>(messages[i].Completion);
                Equal(HeartbeatResult.Replicated, response.Value.Result);
            }
            else
            {
                cluster.Network.Drop(messages[i]);
                await ThrowsAsync<MemberUnavailableException>(messages[i].Completion);
            }
        }
    }
}
