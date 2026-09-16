using System.Text;

namespace DotNext.Net.Cluster.Consensus.Raft.InProcess;

using IO;
using StateMachine;

public sealed class AcknowledgedLogDurabilityTests : RaftTest
{
    private const string Payload = "client acknowledged N";

    [Fact]
    public static async Task SurvivingMajorityRetainsClientAcknowledgedEntry()
        => await RunAsync(Enumerable.Range(0, 3).Select(_ => GetTempPath()).ToArray());

    [Fact]
    public static async Task SurvivingMajorityRetainsEntryAfterProcessTermination()
    {
        var location = GetTempPath();
        await WalCrashWorker.KillAfterAcknowledgmentAsync(new(location, WriteAheadLog.MemoryManagementStrategy.PrivateMemory,
            false, 0, WriteAheadLog.IntegrityHashAlgorithm.None, "raft"));
        await using var cluster = new InProcessClusterFixture(3, i => new WriteAheadLog(new()
        {
            Location = Path.Combine(location, i.ToString()),
            MemoryManagement = WriteAheadLog.MemoryManagementStrategy.PrivateMemory,
            FlushInterval = Timeout.InfiniteTimeSpan,
        }, IStateMachine.CreateNoOp()));
        await cluster.StartAsync();
        await cluster.Leader.StopAsync(TestToken);
        await VerifySurvivorsAsync(cluster);
    }

    internal static async Task RunAsync(string[] locations, Func<Task> afterAcknowledgment = null)
    {
        await using var cluster = new InProcessClusterFixture(3, i => Open(i));
        await cluster.StartLeaderAsync();
        await cluster.DeliverRoundAsync();
        foreach (var state in cluster.States.Cast<WriteAheadLog>())
        {
            await state.WaitForApplyAsync(1L, TestToken);
            await state.FlushAsync(TestToken);
            Equal(1L, state.LastEntryIndex);
            Equal(1L, state.LastCommittedEntryIndex);
        }

        var leader = cluster.Leader;
        var follower = cluster.Nodes[1];
        var stale = cluster.Nodes[2];
        var network = cluster.Network;
        Equal(2L, await leader.AuditTrail.AppendAsync(new TestLogEntry(Payload) { Term = leader.Term }, TestToken));
        var replication = leader.ForceReplicationAsync(TestToken).AsTask();
        var toFollower = await cluster.PendingAsync(1, RaftMessageType.AppendEntries);
        var toStale = await cluster.PendingAsync(2, RaftMessageType.AppendEntries);
        await network.DeliverAsync(toFollower);
        var acknowledgment = await IsType<Task<Result<ReplicationStatus>>>(toFollower.Completion);
        Equal(HeartbeatResult.ReplicatedWithLeaderTerm, acknowledgment.Value.Result);
        Equal(2L, follower.AuditTrail.LastEntryIndex);
        Equal(1L, follower.AuditTrail.LastCommittedEntryIndex);
        await replication;
        await leader.AuditTrail.WaitForApplyAsync(2L, TestToken);
        await ((WriteAheadLog)leader.AuditTrail).FlushAsync(TestToken);
        Equal(2L, leader.AuditTrail.LastCommittedEntryIndex);
        network.Drop(toStale);

        // The client has completed, but no RPC carrying commit N reaches F1.
        network.Partition(leader.EndPoint, follower.EndPoint);
        network.Partition(leader.EndPoint, stale.EndPoint);
        await leader.StopAsync(TestToken);
        if (afterAcknowledgment is not null)
            await afterAcknowledgment();
        follower = await follower.RestartAsync(old =>
        {
            ((WriteAheadLog)old).Dispose();
            return Open(1);
        }, TestToken);
        await cluster.Nodes[1].DisposeAsync();
        cluster.Nodes[1] = follower;
        cluster.States[1] = follower.AuditTrail;
        await VerifySurvivorsAsync(cluster);

        WriteAheadLog Open(int index) => new(new()
        {
            Location = locations[index],
            MemoryManagement = WriteAheadLog.MemoryManagementStrategy.PrivateMemory,
            FlushInterval = Timeout.InfiniteTimeSpan,
        }, IStateMachine.CreateNoOp());
    }

    private static async Task VerifySurvivorsAsync(InProcessClusterFixture cluster)
    {
        var follower = cluster.Nodes[1];
        var stale = cluster.Nodes[2];
        var network = cluster.Network;
        Equal(1L, follower.AuditTrail.LastCommittedEntryIndex);
        Equal(1L, stale.AuditTrail.LastEntryIndex);
        TestContext.Current.TestOutputHelper.WriteLine(
            $"L persisted/client acknowledged N=2; F1 acknowledged with commit=1; " +
            $"recovered F1 tail/commit={follower.AuditTrail.LastEntryIndex}/{follower.AuditTrail.LastCommittedEntryIndex}");

        network.Hold(stale.EndPoint, follower.EndPoint);
        stale.StartElectionTimer();
        cluster.TimeProvider.Advance(TimeSpan.FromMilliseconds(100));
        var preVote = await network.WaitForMessageAsync(stale.EndPoint, follower.EndPoint, RaftMessageType.PreVote, TestToken);
        await network.DeliverAsync(preVote);
        var response = await IsType<Task<Result<PreVoteResult>>>(preVote.Completion);
        if (response.Value is PreVoteResult.Accepted)
        {
            var vote = await network.WaitForMessageAsync(stale.EndPoint, follower.EndPoint, RaftMessageType.Vote, TestToken);
            await network.DeliverAsync(vote);
            await stale.WaitForLeaderAsync(DefaultTimeout, TestToken);
            TestContext.Current.TestOutputHelper.WriteLine(
                $"Stale F2 obtained the surviving majority and became leader in term {stale.Term}, without client payload N.");
        }

        Equal(PreVoteResult.RejectedByFollower, response.Value);
        Equal(2L, follower.AuditTrail.LastEntryIndex);
        using (var entries = await ((WriteAheadLog)follower.AuditTrail).ReadAsync(2L, 2L, TestToken))
            Equal(Payload, await entries[0].ToStringAsync(Encoding.UTF8, token: TestToken));

        // Positive control: preserving the tail must still allow useful leadership.
        network.Release(stale.EndPoint, follower.EndPoint);
        follower.StartElectionTimer();
        cluster.TimeProvider.Advance(TimeSpan.FromMilliseconds(100));
        await follower.WaitForLeaderAsync(DefaultTimeout, TestToken);
        await follower.ForceReplicationAsync(TestToken);
        await follower.WaitForLeadershipAsync(TestToken);
        await follower.ForceReplicationAsync(TestToken);
        await stale.AuditTrail.WaitForApplyAsync(2L, TestToken);
        using var replicated = await ((WriteAheadLog)stale.AuditTrail).ReadAsync(2L, 2L, TestToken);
        Equal(Payload, await replicated[0].ToStringAsync(Encoding.UTF8, token: TestToken));
    }
}
