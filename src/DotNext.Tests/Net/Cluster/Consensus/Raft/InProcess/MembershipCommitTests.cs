using System.Net;

namespace DotNext.Net.Cluster.Consensus.Raft.InProcess;

public sealed class MembershipCommitTests : RaftTest
{
    [Theory]
    [InlineData(3, false)]
    [InlineData(5, false)]
    [InlineData(7, false)]
    [InlineData(3, true)]
    [InlineData(5, true)]
    [InlineData(7, true)]
    public static async Task SnapshotRequiresFullMembershipMajority(int memberCount, bool failuresFirst)
    {
        await using var cluster = new Cluster(memberCount);
        var majority = memberCount / 2 + 1;
        var lagging = memberCount - 1;
        await cluster.StartAsync();
        cluster.HoldFollowers();
        await cluster.ElectAsync();
        var initial = await cluster.PendingRoundAsync();
        var retry = cluster.Leader.ForceReplicationAsync(TestToken).AsTask();
        foreach (var message in initial)
            await cluster.Network.DeliverAsync(message);
        var entries = await cluster.PendingRoundAsync();
        cluster.Network.Drop(entries[^1]);
        foreach (var message in entries[..^1])
            await cluster.Network.DeliverAsync(message);
        await retry;
        await cluster.Leader.WaitForLeadershipAsync(TestToken);
        await cluster.DeliverRoundAsync(drop: lagging, RaftMessageType.InstallSnapshot);
        for (var i = 1; i < lagging; i++)
            await cluster.Nodes[i].AuditTrail.WaitForApplyAsync(1L, TestToken);
        Equal(1L, cluster.States[0].LastCommittedEntryIndex);
        Equal(0L, cluster.States[lagging].LastEntryIndex);

        Equal(2L, await cluster.Leader.AuditTrail.AppendAsync(
            new EmptyLogEntry { Term = cluster.Leader.Term }, TestToken));
        var round = cluster.Leader.ForceReplicationAsync(TestToken).AsTask();
        var unavailable = new List<PendingMessage>();
        for (var i = majority - 1; i < lagging; i++)
            unavailable.Add(await cluster.PendingAsync(i, RaftMessageType.AppendEntries));
        if (failuresFirst)
            foreach (var message in unavailable)
                cluster.Network.Drop(message);

        // Leader plus majority-2 followers hold index 2: one replica short.
        for (var i = 1; i < majority - 1; i++)
            await cluster.Network.DeliverAsync(await cluster.PendingAsync(i, RaftMessageType.AppendEntries));
        var snapshot = await cluster.PendingAsync(lagging, RaftMessageType.InstallSnapshot);
        await cluster.Network.DeliverAsync(snapshot);
        var acknowledgment = await IsType<Task<Result<HeartbeatResult>>>(snapshot.Completion);
        Equal(HeartbeatResult.ReplicatedWithLeaderTerm, acknowledgment.Value);
        Equal(1L, cluster.States[lagging].LastEntryIndex);
        Equal(cluster.Leader.Term, await cluster.Nodes[lagging].AuditTrail.GetTermAsync(1L, TestToken));
        await round;
        Equal(1L, cluster.States[0].LastCommittedEntryIndex);
        False(cluster.Leader.LeadershipToken.IsCancellationRequested);

        if (!failuresFirst)
            foreach (var message in unavailable)
                cluster.Network.Drop(message);

        // Catch-up can commit once a full majority actually has the tail.
        await cluster.DeliverRoundAsync();
        Equal(2L, cluster.States[0].LastCommittedEntryIndex);
        foreach (var state in cluster.States)
            Equal(2L, state.LastEntryIndex);

        // Empty heartbeats contribute responsiveness, not current-term progress.
        // Selecting zero in that round must not move the commit index backwards.
        await cluster.DeliverRoundAsync();
        Equal(2L, cluster.States[0].LastCommittedEntryIndex);
    }

    [Theory]
    [InlineData(3)]
    [InlineData(5)]
    [InlineData(7)]
    public static async Task PreviousTermSnapshotIsResponsiveButCannotCommit(int memberCount)
    {
        await using var cluster = new Cluster(memberCount);
        var majority = memberCount / 2 + 1;
        var lagging = memberCount - 1;

        // Legal durable history: term 1's entry was committed on a majority;
        // the last member missed it. Electing a new leader appends term 2's
        // no-op at index 2, which must not commit based on the old snapshot.
        for (var i = 0; i < lagging; i++)
        {
            var state = cluster.Nodes[i].AuditTrail;
            await state.UpdateTermAsync(1L, resetLastVote: true, TestToken);
            await state.AppendAsync(new EmptyLogEntry { Term = 1L }, TestToken);
            await state.CommitAsync(1L, TestToken);
        }
        await cluster.StartAsync();
        cluster.HoldFollowers();
        await cluster.ElectAsync();
        Equal(2L, cluster.Leader.Term);
        Equal(2L, cluster.States[0].LastEntryIndex);

        // The first real heartbeat fails its preceding-index check everywhere.
        // Queue the next round before releasing it, then let FIFO workers retry.
        var initial = await cluster.PendingRoundAsync();
        var round = cluster.Leader.ForceReplicationAsync(TestToken).AsTask();
        foreach (var message in initial)
        {
            await cluster.Network.DeliverAsync(message);
            var response = await IsType<Task<Result<ReplicationStatus>>>(message.Completion);
            Equal(HeartbeatResult.Rejected, response.Value.Result);
        }

        for (var i = majority - 1; i < lagging; i++)
            cluster.Network.Drop(await cluster.PendingAsync(i, RaftMessageType.AppendEntries));
        for (var i = 1; i < majority - 1; i++)
            await cluster.Network.DeliverAsync(await cluster.PendingAsync(i, RaftMessageType.AppendEntries));
        var snapshot = await cluster.PendingAsync(lagging, RaftMessageType.InstallSnapshot);
        await cluster.Network.DeliverAsync(snapshot);
        var acknowledgment = await IsType<Task<Result<HeartbeatResult>>>(snapshot.Completion);
        Equal(HeartbeatResult.Replicated, acknowledgment.Value);
        Equal(1L, await cluster.Nodes[lagging].AuditTrail.GetTermAsync(1L, TestToken));
        Equal(2L, cluster.Nodes[lagging].Term);
        await round;
        Equal(1L, cluster.States[0].LastCommittedEntryIndex);

        await cluster.DeliverRoundAsync();
        await cluster.Leader.WaitForLeadershipAsync(TestToken);
        Equal(2L, cluster.States[0].LastCommittedEntryIndex);
    }

    private sealed class Cluster : IAsyncDisposable
    {
        internal readonly ManualTimeProvider TimeProvider = new();
        internal readonly InProcessNetwork Network = new();
        internal readonly ConsensusOnlyState[] States;
        internal readonly InProcessCluster[] Nodes;

        internal Cluster(int memberCount)
        {
            EndPoint[] membership = Enumerable.Range(0, memberCount)
                .Select(i => new DnsEndPoint($"node-{i}", 0)).ToArray();
            States = Enumerable.Range(0, memberCount).Select(_ => new ConsensusOnlyState()).ToArray();
            Nodes = States.Select((state, i) => new InProcessCluster(
                Network, ((DnsEndPoint)membership[i]).Host, membership, state,
                TimeProvider, TimeSpan.FromMilliseconds(100), startFollower: false)).ToArray();
        }

        internal InProcessCluster Leader => Nodes[0];

        internal async Task StartAsync()
        {
            foreach (var node in Nodes)
                await node.StartAsync(TestToken);
        }

        internal async Task ElectAsync()
        {
            Leader.StartElectionTimer();
            TimeProvider.Advance(TimeSpan.FromMilliseconds(100));
            for (var i = 1; i < Nodes.Length; i++)
                await Network.DeliverAsync(await PendingAsync(i, RaftMessageType.PreVote));
            for (var i = 1; i < Nodes.Length; i++)
                await Network.DeliverAsync(await PendingAsync(i, RaftMessageType.Vote));
            await Leader.WaitForLeaderAsync(TimeSpan.FromSeconds(5), TestToken);
        }

        internal void HoldFollowers()
        {
            foreach (var node in Nodes.Skip(1))
                Network.Hold(Leader.EndPoint, node.EndPoint);
        }

        internal Task<PendingMessage> PendingAsync(int member, RaftMessageType type)
            => Network.WaitForMessageAsync(Leader.EndPoint, Nodes[member].EndPoint, type, TestToken);

        internal async Task<PendingMessage[]> PendingRoundAsync(RaftMessageType lastType = RaftMessageType.AppendEntries)
        {
            var messages = new PendingMessage[Nodes.Length - 1];
            for (var i = 1; i < Nodes.Length; i++)
                messages[i - 1] = await PendingAsync(i, i == Nodes.Length - 1 ? lastType : RaftMessageType.AppendEntries);
            return messages;
        }

        internal async Task DeliverRoundAsync(int drop = -1, RaftMessageType lastType = RaftMessageType.AppendEntries)
        {
            var round = Leader.ForceReplicationAsync(TestToken).AsTask();
            var messages = await PendingRoundAsync(lastType);
            for (var i = 0; i < messages.Length; i++)
            {
                if (i + 1 == drop)
                    Network.Drop(messages[i]);
                else
                    await Network.DeliverAsync(messages[i]);
            }
            await round;
        }

        public async ValueTask DisposeAsync()
        {
            foreach (var node in Nodes)
                await node.DisposeAsync();
            foreach (var state in States)
                state.Dispose();
        }
    }
}
