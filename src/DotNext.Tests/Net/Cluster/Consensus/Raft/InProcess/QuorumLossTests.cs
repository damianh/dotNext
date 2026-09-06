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
        await cluster.StartAsync();
        cluster.HoldFollowers();
        await cluster.ElectAsync();

        // Observe the automatic round before forcing its retry, and account for
        // every worker's setup RPC before starting the round under test.
        var initial = await cluster.PendingRoundAsync();
        var retry = cluster.Leader.ForceReplicationAsync(TestToken).AsTask();
        foreach (var message in initial)
        {
            await cluster.Network.DeliverAsync(message);
            var response = await IsType<Task<Result<ReplicationStatus>>>(message.Completion);
            Equal(HeartbeatResult.Rejected, response.Value.Result);
        }

        foreach (var message in await cluster.PendingRoundAsync())
            await cluster.Network.DeliverAsync(message);
        await retry;
        await cluster.Leader.WaitForLeadershipAsync(TestToken);
        Equal(1L, cluster.States[0].LastCommittedEntryIndex);

        var leadership = cluster.Leader.LeadershipToken;
        var replication = cluster.Leader.ForceReplicationAsync(TestToken).AsTask();
        var messages = await cluster.PendingRoundAsync();
        for (var i = 0; i < messages.Length; i++)
        {
            if (i < memberCount / 2 - 1)
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

        Empty(cluster.Network.PendingMessages);
        TestContext.Current.TestOutputHelper.WriteLine(
            $"All {memberCount} contributions supplied, including the leader; " +
            $"{memberCount / 2} members unavailable. Waiting for leader step-down and shutdown.");
        await ThrowsAsync<NotLeaderException>(replication.WaitAsync(DefaultTimeout, TestToken));
        True(leadership.IsCancellationRequested);
        await cluster.Leader.StopAsync(TestToken).WaitAsync(DefaultTimeout, TestToken);
    }
}
