namespace DotNext.Net.Cluster.Consensus.Raft.InProcess;

/// <summary>
/// Checks leader leases against the supported clock and timer model.
/// </summary>
/// <remarks>
/// A lease is usable when <see cref="RaftCluster{TMember}.TryGetLeaseToken(out CancellationToken)"/>
/// returns a token that is not canceled. A stale read is possible when a node can observe
/// a usable lease while another leader has committed entries that the lease holder has not.
/// All scenarios establish the lease through a completed quorum round rather than relying on
/// the token issued at the leader-state transition.
/// </remarks>
public sealed class LeaderLeaseTimingTests : RaftTest
{
    private static readonly TimeSpan OneMillisecond = TimeSpan.FromMilliseconds(1);

    [Theory(Timeout = TestTimeouts.Default)]
    [InlineData(1D, 1D)]
    [InlineData(1.25D, 1D)]
    [InlineData(1.25D, 1.25D)]
    public static async Task LeaseExpiresBeforeVotersForgetLeaderWithinDriftBound(double clockDriftBound, double leaderSlowdown)
        => False(await CanVoteWhileLeaseIsUsableAsync(clockDriftBound, leaderSlowdown));

    // Characterizes an unsupported configuration: the leader clock runs slower than the configured bound allows.
    [Fact(Timeout = TestTimeouts.Default)]
    public static async Task DriftBeyondBoundLetsVotersForgetLeaderDuringLease()
        => True(await CanVoteWhileLeaseIsUsableAsync(clockDriftBound: 1.25D, leaderSlowdown: 1.5D));

    private static async Task<bool> CanVoteWhileLeaseIsUsableAsync(double clockDriftBound, double leaderSlowdown)
    {
        await using var cluster = new InProcessClusterFixture(
            3,
            lease: new(clockDriftBound),
            clockFactory: (member, clock) => member is 0 ? new DriftingTimeProvider(clock, leaderSlowdown) : clock);

        // The leader's election timer uses its local clock.
        await cluster.StartLeaderAsync(
            electionDelay: TimeSpan.FromTicks((long)Math.Ceiling(TimeSpan.FromMilliseconds(100).Ticks * leaderSlowdown)));
        await cluster.DeliverRoundAsync();
        True(IsLeaseUsable(cluster.Leader));

        // Automatic heartbeats stay held from now on, so neither the lease nor the voters' stickiness is refreshed.
        var candidate = cluster.Nodes[2];
        var voter = candidate.GetMember(cluster.Nodes[1].EndPoint).As<IRaftClusterMember>();
        var lastIndex = candidate.AuditTrail.LastEntryIndex;
        var lastTerm = await candidate.AuditTrail.GetTermAsync(lastIndex, TestToken);
        var overlap = false;
        for (var elapsed = 0; elapsed <= 150; elapsed++)
        {
            var leaseUsable = IsLeaseUsable(cluster.Leader);
            var response = await voter.PreVoteAsync(candidate.Term, lastIndex, lastTerm, TestToken);
            overlap |= leaseUsable && response.Value is PreVoteResult.Accepted;
            cluster.TimeProvider.Advance(OneMillisecond);
        }

        False(IsLeaseUsable(cluster.Leader));
        return overlap;
    }

    [Theory(Timeout = TestTimeouts.Default)]
    [InlineData(false)]
    [InlineData(true)]
    public static async Task LateLeaseTimerDoesNotExtendLease(bool lateTimers)
    {
        StarvableTimeProvider leaderClock = null;
        await using var cluster = new InProcessClusterFixture(
            5,
            lease: new(),
            clockFactory: (member, clock) => member is 0 ? leaderClock = new(clock) : clock);
        try
        {
            // t = 100: the lease deadline is t = 200.
            await cluster.StartLeaderAsync();
            await cluster.DeliverRoundAsync();
            var leader = cluster.Leader;
            True(IsLeaseUsable(leader));

            // The leader cannot reach anyone and, optionally, its timer callbacks run late while it keeps serving reads.
            if (lateTimers)
                leaderClock.Starve();
            for (var i = 1; i < cluster.Nodes.Length; i++)
                cluster.Network.Partition(leader.EndPoint, cluster.Nodes[i].EndPoint);

            // Node 1 times out at t = 210, after the deadline, so the voters legitimately elect it.
            cluster.TimeProvider.Advance(TimeSpan.FromMilliseconds(10));
            var successor = cluster.Nodes[1];
            successor.StartElectionTimer();
            cluster.TimeProvider.Advance(TimeSpan.FromMilliseconds(101));
            await CommitWriteBarrierAsync(successor, 2L);
            Equal(2L, successor.AuditTrail.LastCommittedEntryIndex);
            Equal(lateTimers, leaderClock.QueuedCallbacks > 0);

            // The old leader has not observed the new term or its commits.
            Equal(1L, cluster.States[0].LastCommittedEntryIndex);
            True(successor.Term > leader.Term);
            False(IsLeaseUsable(leader), "The stale leader still reports a usable lease after its deadline.");
        }
        finally
        {
            leaderClock?.Resume();
        }
    }

    [Theory(Timeout = TestTimeouts.Default)]
    [InlineData(false)]
    [InlineData(true)]
    public static async Task RestartedVoterRespectsAcknowledgedLease(bool restart)
    {
        await using var cluster = new InProcessClusterFixture(5, lease: new());
        await cluster.StartLeaderAsync();
        var leader = cluster.Leader;
        var candidate = cluster.Nodes[3];
        cluster.Network.Partition(leader.EndPoint, candidate.EndPoint);
        cluster.Network.Partition(leader.EndPoint, cluster.Nodes[4].EndPoint);

        // The candidate times out at t = 210.
        cluster.TimeProvider.Advance(TimeSpan.FromMilliseconds(10));
        candidate.StartElectionTimer();

        // t = 149: nodes 1 and 2 renew the lease until t = 249.
        cluster.TimeProvider.Advance(TimeSpan.FromMilliseconds(39));
        var round = leader.ForceReplicationAsync(TestToken).AsTask();
        await cluster.Network.DeliverAsync(await cluster.PendingAsync(1, RaftMessageType.AppendEntries));
        await cluster.Network.DeliverAsync(await cluster.PendingAsync(2, RaftMessageType.AppendEntries));
        await round;
        True(IsLeaseUsable(leader));

        // t = 150: node 2 optionally crashes and restarts with its persistent state.
        cluster.TimeProvider.Advance(OneMillisecond);
        if (restart)
            await cluster.RestartAsync(2);

        int[] voters = [1, 2, 4];
        foreach (var i in voters)
            cluster.Network.Hold(candidate.EndPoint, cluster.Nodes[i].EndPoint);

        cluster.TimeProvider.Advance(TimeSpan.FromMilliseconds(60));
        var preVotes = new PendingMessage[voters.Length];
        for (var i = 0; i < voters.Length; i++)
            preVotes[i] = await cluster.Network.WaitForMessageAsync(
                candidate.EndPoint, cluster.Nodes[voters[i]].EndPoint, RaftMessageType.PreVote, TestToken);

        foreach (var i in voters)
            cluster.Network.Release(candidate.EndPoint, cluster.Nodes[i].EndPoint);

        // Node 1 rejects and node 4 accepts; the candidate's own vote cancels out the unreachable leader.
        var tally = 0;
        foreach (var message in preVotes)
        {
            await cluster.Network.DeliverAsync(message);
            var response = await IsType<Task<Result<PreVoteResult>>>(message.Completion);
            tally += response.Value is PreVoteResult.Accepted ? 1 : -1;
        }

        if (tally > 0)
        {
            await CommitWriteBarrierAsync(candidate, 2L);
            Equal(2L, candidate.AuditTrail.LastCommittedEntryIndex);
        }

        False(
            IsLeaseUsable(leader) && cluster.States[3].LastCommittedEntryIndex > cluster.States[0].LastCommittedEntryIndex,
            "A restarted voter helped elect a new leader while the old leader's lease was still usable.");
    }

    [Fact(Timeout = TestTimeouts.Default)]
    public static async Task RetransmittedSnapshotAcknowledgmentKeepsVoterSticky()
    {
        await using var cluster = new InProcessClusterFixture(5, lease: new());
        const int lagging = 4;
        await cluster.StartLeaderAsync(laggingFollower: lagging);
        var leader = cluster.Leader;

        // t = 100: the lagging node installs the snapshot, but its acknowledgment is lost.
        var round = leader.ForceReplicationAsync(TestToken).AsTask();
        var messages = await cluster.PendingRoundAsync(RaftMessageType.InstallSnapshot);
        foreach (var message in messages[..^1])
            await cluster.Network.DeliverAsync(message);
        await cluster.Network.DeliverAndLoseResponseAsync(messages[^1]);
        await round;
        Equal(1L, cluster.States[lagging].LastCommittedEntryIndex);

        // t = 149: the leader retransmits the snapshot and counts its rejection towards the lease quorum.
        cluster.TimeProvider.Advance(TimeSpan.FromMilliseconds(49));
        round = leader.ForceReplicationAsync(TestToken).AsTask();
        messages = await cluster.PendingRoundAsync(RaftMessageType.InstallSnapshot);
        await cluster.Network.DeliverAsync(messages[0]);
        cluster.Network.Drop(messages[1]);
        cluster.Network.Drop(messages[2]);
        await cluster.Network.DeliverAsync(messages[^1]);
        var retransmission = await IsType<Task<Result<HeartbeatResult>>>(messages[^1].Completion);
        Equal(HeartbeatResult.Rejected, retransmission.Value);
        await round;
        True(IsLeaseUsable(leader));

        // t = 201: nodes 2, 3 and 4 last heard from the leader at t = 100.
        cluster.TimeProvider.Advance(TimeSpan.FromMilliseconds(52));
        var candidate = cluster.Nodes[2];
        var lastIndex = candidate.AuditTrail.LastEntryIndex;
        var lastTerm = await candidate.AuditTrail.GetTermAsync(lastIndex, TestToken);
        var granted = 0;
        foreach (var i in (int[])[2, 3, lagging])
        {
            var response = await candidate.GetMember(cluster.Nodes[i].EndPoint).As<IRaftClusterMember>()
                .PreVoteAsync(candidate.Term, lastIndex, lastTerm, TestToken);
            if (response.Value is PreVoteResult.Accepted)
                granted++;
        }

        False(
            IsLeaseUsable(leader) && granted > cluster.Nodes.Length / 2,
            "A majority can elect a new leader while the lease is usable.");
    }

    // The candidate's election timer fires during Advance, but the transition to leader completes asynchronously.
    // The new leader's first round only discovers the followers' log positions, and the manual clock
    // does not drive its heartbeats, so the rounds that commit the write barrier are forced.
    private static async Task CommitWriteBarrierAsync(InProcessCluster node, long writeBarrier)
    {
        while (!object.Equals(node.Leader?.EndPoint, node.EndPoint) || node.AuditTrail.LastEntryIndex < writeBarrier)
            await Task.Delay(1, TestToken);

        while (node.AuditTrail.LastCommittedEntryIndex < writeBarrier)
            await node.ForceReplicationAsync(TestToken);

        await node.WaitForLeadershipAsync(TestToken);
    }
    private static bool IsLeaseUsable(InProcessCluster node)
        => node.TryGetLeaseToken(out var token) && !token.IsCancellationRequested;
}
