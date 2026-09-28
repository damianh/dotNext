namespace DotNext.Net.Cluster.Consensus.Raft.InProcess;

using StateMachine;
using Threading;

/// <summary>
/// Checks that a newly elected leader publishes no usable lease before a majority
/// has confirmed its term and the local state machine has applied its write barrier.
/// </summary>
/// <remarks>
/// A lease is usable when <see cref="RaftCluster{TMember}.TryGetLeaseToken(out CancellationToken)"/>
/// returns a token that is not canceled. In the write-barrier scenarios the leader uses a real
/// write-ahead log that holds an entry inherited from an earlier term. Its state machine can hold
/// the application of that entry, so the current-term write barrier (the leader's no-op) can be
/// committed while its application is still pending.
/// </remarks>
public sealed class LeaderLeaseActivationTests : RaftTest
{
    private const long WriteBarrier = 2L;

    [Fact(Timeout = TestTimeouts.Default)]
    public static async Task LeaseRequiresQuorumAndAppliedWriteBarrier()
    {
        var machine = new GatedStateMachine();
        await using var cluster = CreateCluster(3, machine);
        var leader = cluster.Leader;
        await PrepareInheritedEntryAsync(cluster);
        cluster.HoldFollowers();
        await cluster.ElectAsync();
        Equal(WriteBarrier, leader.AuditTrail.LastEntryIndex);

        // Every interval is observed before asserting, so a failure reports each premature lease.
        var premature = new List<string>();

        // The first automatic round is held: no member has confirmed the new term.
        var initial = await cluster.PendingRoundAsync();
        if (IsLeaseUsable(leader))
            premature.Add("before any quorum round");

        // The followers reject the first round because their logs are behind,
        // but they confirm the term. The barrier is not committed yet.
        var retry = leader.ForceReplicationAsync(TestToken).AsTask();
        foreach (var message in initial)
        {
            await cluster.Network.DeliverAsync(message);
            var response = await IsType<Task<Result<ReplicationStatus>>>(message.Completion);
            Equal(HeartbeatResult.Rejected, response.Value.Result);
        }

        var next = await cluster.PendingRoundAsync();
        Equal(0L, leader.AuditTrail.LastCommittedEntryIndex);
        if (IsLeaseUsable(leader))
            premature.Add("after quorum confirmation, before the write barrier is committed");

        // The barrier is committed, but the application of the inherited entry, and so of the barrier, is held.
        foreach (var message in next)
            await cluster.Network.DeliverAsync(message);
        await retry;
        await CommitAsync(cluster, WriteBarrier);
        await machine.Entered.Task.WaitAsync(DefaultTimeout, TestToken);
        True(((WriteAheadLog)cluster.States[0]).LastAppliedIndex < WriteBarrier);
        if (IsLeaseUsable(leader))
            premature.Add("after the write barrier is committed, before it is applied");

        False(premature.Count > 0, $"The leader exposes a usable lease {string.Join("; ", premature)}.");

        // Healthy activation: the manual clock is frozen, so the lease confirmed above cannot expire.
        machine.Release.TrySetResult();
        await leader.AuditTrail.WaitForApplyAsync(WriteBarrier, TestToken);
        await WaitForUsableLeaseAsync(leader);

        // Expiry and renewal through the production heartbeat path. Advancing the clock
        // also starts the next automatic round, which renews the lease once a majority answers.
        cluster.TimeProvider.Advance(leader.ElectionTimeout);
        False(IsLeaseUsable(leader));
        foreach (var message in await cluster.PendingRoundAsync())
            await cluster.Network.DeliverAsync(message);
        await WaitForUsableLeaseAsync(leader);
    }

    [Fact(Timeout = TestTimeouts.Default)]
    public static async Task StalledFirstHeartbeatDoesNotPublishLease()
    {
        await using var cluster = new InProcessClusterFixture(3, lease: new());
        var leader = cluster.Leader;
        await cluster.StartAsync();
        cluster.HoldFollowers();
        await cluster.ElectAsync();

        // The leader state's token is canceled after the node has left that state.
        var leaderState = leader.ConsensusToken;
        var initial = await cluster.PendingRoundAsync();
        False(IsLeaseUsable(leader), "The leader exposes a usable lease before its first heartbeat completes.");

        // No follower answers, so the leader loses quorum responsiveness and steps down.
        foreach (var message in initial)
        {
            cluster.Network.Drop(message);
            False(IsLeaseUsable(leader));
        }

        await leaderState.WaitAsync().AsTask().WaitAsync(DefaultTimeout, TestToken);
        False(leader.TryGetLeaseToken(out var token));
        True(token.IsCancellationRequested);
    }

    [Fact(Timeout = TestTimeouts.Default)]
    public static async Task ReplicationWorkerFailureDoesNotActivateLease()
    {
        const int firstResponsiveMember = 2;
        var machine = new GatedStateMachine();
        await using var cluster = CreateCluster(5, machine);
        var leader = cluster.Leader;
        await PrepareInheritedEntryAsync(cluster);

        // Node 1's first AppendEntries fails in its replication worker; its later RPCs stay held.
        var failure = cluster.Network.FailNext(
            leader.EndPoint,
            cluster.Nodes[1].EndPoint,
            RaftMessageType.AppendEntries,
            new IOException("Injected replication failure."));
        cluster.HoldFollowers();
        await cluster.ElectAsync();
        await failure.WaitAsync(DefaultTimeout, TestToken);

        var initial = await PendingFromAsync(cluster, firstResponsiveMember);
        False(IsLeaseUsable(leader), "The leader exposes a usable lease before any quorum round.");

        // The remaining followers confirm the term and commit the barrier; its application is held.
        var retry = leader.ForceReplicationAsync(TestToken).AsTask();
        foreach (var message in initial)
            await cluster.Network.DeliverAsync(message);
        var next = await PendingFromAsync(cluster, firstResponsiveMember);
        False(IsLeaseUsable(leader), "The leader exposes a usable lease before its write barrier is committed.");
        foreach (var message in next)
            await cluster.Network.DeliverAsync(message);
        await retry;
        await CommitAsync(cluster, WriteBarrier, firstResponsiveMember);

        await machine.Entered.Task.WaitAsync(DefaultTimeout, TestToken);
        False(IsLeaseUsable(leader), "The leader exposes a usable lease before its write barrier is applied.");

        machine.Release.TrySetResult();
        await leader.AuditTrail.WaitForApplyAsync(WriteBarrier, TestToken);
        await WaitForUsableLeaseAsync(leader);
    }

    [Fact(Timeout = TestTimeouts.Default)]
    public static async Task LeadershipChangeBeforeActivationDoesNotPublishLease()
    {
        await using var cluster = new InProcessClusterFixture(3, lease: new());
        var leader = cluster.Leader;
        await cluster.StartAsync();
        cluster.HoldFollowers();
        await cluster.ElectAsync();

        // The leader state's token is canceled after the node has left that state.
        var leaderState = leader.ConsensusToken;
        var initial = await cluster.PendingRoundAsync();
        False(IsLeaseUsable(leader), "The leader exposes a usable lease before any quorum round.");

        // Node 1 has observed a newer term, e.g. from another candidate, and answers the held first round with it.
        await cluster.States[1].UpdateTermAsync(leader.Term + 1L, resetLastVote: true, TestToken);
        await cluster.Network.DeliverAsync(initial[0]);
        await leaderState.WaitAsync().AsTask().WaitAsync(DefaultTimeout, TestToken);

        False(leader.TryGetLeaseToken(out var token));
        True(token.IsCancellationRequested);
        foreach (var message in initial[1..])
            cluster.Network.TryDrop(message);
    }

    [Theory(Timeout = TestTimeouts.Default)]
    [InlineData(false)]
    [InlineData(true)]
    public static async Task ShutdownBeforeActivationCancelsPendingLease(bool dispose)
    {
        var machine = new GatedStateMachine();
        var cluster = CreateCluster(3, machine);
        var disposalStarted = false;
        try
        {
            var leader = cluster.Leader;
            await PrepareInheritedEntryAsync(cluster);
            cluster.HoldFollowers();
            await cluster.ElectAsync();

            // Commit the barrier and hold its application.
            var initial = await cluster.PendingRoundAsync();
            var retry = leader.ForceReplicationAsync(TestToken).AsTask();
            foreach (var message in initial)
                await cluster.Network.DeliverAsync(message);
            foreach (var message in await cluster.PendingRoundAsync())
                await cluster.Network.DeliverAsync(message);
            await retry;
            await CommitAsync(cluster, WriteBarrier);
            await machine.Entered.Task.WaitAsync(DefaultTimeout, TestToken);

            True(leader.TryGetLeaseToken(out var pending));
            False(IsLeaseUsable(leader), "The leader exposes a usable lease before its write barrier is applied.");

            Task shutdown;
            if (dispose)
            {
                disposalStarted = true;
                shutdown = cluster.DisposeAsync().AsTask();
            }
            else
            {
                shutdown = leader.StopAsync(TestToken);
            }

            await shutdown.WaitAsync(DefaultTimeout, TestToken);
            True(pending.IsCancellationRequested);
            False(leader.TryGetLeaseToken(out var token));
            True(token.IsCancellationRequested);
        }
        finally
        {
            machine.Release.TrySetResult();
            if (!disposalStarted)
                await cluster.DisposeAsync();
        }
    }

    [Fact(Timeout = TestTimeouts.Default)]
    public static async Task DisabledLeaseIsNeverPublished()
    {
        await using var cluster = new InProcessClusterFixture(3);
        var leader = cluster.Leader;
        await cluster.StartLeaderAsync();
        await cluster.DeliverRoundAsync();

        False(leader.TryGetLeaseToken(out var token));
        True(token.IsCancellationRequested);
    }

    private static InProcessClusterFixture CreateCluster(int memberCount, GatedStateMachine machine)
    {
        var location = GetTempPath();
        return new(
            memberCount,
            index => index is 0 ? new WriteAheadLog(new() { Location = location }, machine) : new ConsensusOnlyState(),
            lease: new());
    }

    // The leader holds an uncommitted entry with a payload from term 1. After the election in term 2,
    // its no-op at index 2 is the write barrier, and applying it requires applying the inherited entry first.
    private static async Task PrepareInheritedEntryAsync(InProcessClusterFixture cluster)
    {
        await cluster.StartAsync();
        var state = cluster.States[0];
        await state.UpdateTermAsync(1L, resetLastVote: true, TestToken);
        Equal(1L, await state.AppendAsync(new TestLogEntry("inherited") { Term = 1L }, TestToken));
    }

    private static async Task CommitAsync(InProcessClusterFixture cluster, long index, int firstMember = 1)
    {
        while (cluster.Leader.AuditTrail.LastCommittedEntryIndex < index)
        {
            var round = cluster.Leader.ForceReplicationAsync(TestToken).AsTask();
            foreach (var message in await PendingFromAsync(cluster, firstMember))
                await cluster.Network.DeliverAsync(message);
            await round;
        }

        Equal(index, cluster.Leader.AuditTrail.LastCommittedEntryIndex);
    }

    private static async Task<PendingMessage[]> PendingFromAsync(InProcessClusterFixture cluster, int firstMember)
    {
        var messages = new PendingMessage[cluster.Nodes.Length - firstMember];
        for (var i = firstMember; i < cluster.Nodes.Length; i++)
            messages[i - firstMember] = await cluster.PendingAsync(i, RaftMessageType.AppendEntries);
        return messages;
    }

    // The lease activates in a continuation of the leader's application wait, which may run after
    // the test's own continuation. The manual clock does not advance while polling, so the lease cannot expire.
    private static async Task WaitForUsableLeaseAsync(InProcessCluster node)
    {
        while (!IsLeaseUsable(node))
            await Task.Delay(1, TestToken);
    }

    private static bool IsLeaseUsable(InProcessCluster node)
        => node.TryGetLeaseToken(out var token) && !token.IsCancellationRequested;

    private sealed class GatedStateMachine : NoOpSnapshotManager, IStateMachine
    {
        internal readonly TaskCompletionSource Entered = new(TaskCreationOptions.RunContinuationsAsynchronously);
        internal readonly TaskCompletionSource Release = new(TaskCreationOptions.RunContinuationsAsynchronously);

        async ValueTask<long> IStateMachine.ApplyAsync(LogEntry entry, CancellationToken token)
        {
            Entered.TrySetResult();
            await Release.Task.WaitAsync(token);
            return entry.Index;
        }
    }
}
