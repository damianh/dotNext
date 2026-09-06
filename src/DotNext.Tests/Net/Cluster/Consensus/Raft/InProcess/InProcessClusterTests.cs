using System.Net;

namespace DotNext.Net.Cluster.Consensus.Raft.InProcess;

using AsyncAutoResetEvent = Threading.AsyncAutoResetEvent;
using NetworkTransport;

public sealed class InProcessClusterTests : RaftTest
{
    [Theory]
    [InlineData(0)]
    [InlineData(1)]
    [InlineData(2)]
    [InlineData(3)]
    [InlineData(4)]
    public static async Task HealthyElectionAndReplication(int iteration)
    {
        _ = iteration;
        var timeProvider = new ManualTimeProvider();
        var network = new InProcessNetwork();
        EndPoint[] membership = [Address("node-a"), Address("node-b"), Address("node-c")];
        using var stateA = new ConsensusOnlyState();
        using var stateB = new ConsensusOnlyState();
        using var stateC = new ConsensusOnlyState();
        await using var nodeA = CreateNode(network, timeProvider, membership, 0, stateA);
        await using var nodeB = CreateNode(network, timeProvider, membership, 1, stateB);
        await using var nodeC = CreateNode(network, timeProvider, membership, 2, stateC);

        await StartAsync(nodeA, nodeB, nodeC);
        nodeA.StartElectionTimer();
        timeProvider.Advance(TimeSpan.FromMilliseconds(100));

        await nodeA.WaitForLeaderAsync(TimeSpan.FromSeconds(5), TestToken);
        await nodeA.ForceReplicationAsync(TestToken);
        await nodeA.WaitForLeadershipAsync(TestToken);
        var index = nodeA.AuditTrail.LastEntryIndex + 1L;
        await nodeA.ReplicateAsync(new EmptyLogEntry { Term = nodeA.Term }, TestToken);
        await nodeA.ForceReplicationAsync(TestToken);
        await nodeB.AuditTrail.WaitForApplyAsync(index, TestToken);
        await nodeC.AuditTrail.WaitForApplyAsync(index, TestToken);

        Equal(index, nodeA.AuditTrail.LastCommittedEntryIndex);
        Equal(index, nodeB.AuditTrail.LastCommittedEntryIndex);
        Equal(index, nodeC.AuditTrail.LastCommittedEntryIndex);
    }

    [Fact]
    public static async Task ControlsDeliveryOrderAndLoss()
    {
        var timeProvider = new ManualTimeProvider();
        var network = new InProcessNetwork();
        EndPoint[] membership = [Address("node-a"), Address("node-b")];
        using var stateA = new ConsensusOnlyState();
        using var stateB = new ConsensusOnlyState();
        await using var nodeA = CreateNode(network, timeProvider, membership, 0, stateA);
        await using var nodeB = CreateNode(network, timeProvider, membership, 1, stateB);
        await StartAsync(nodeA, nodeB);
        timeProvider.Advance(TimeSpan.FromMilliseconds(100));

        network.Hold(nodeA.EndPoint, nodeB.EndPoint);
        var member = nodeA.GetMember(nodeB.EndPoint).As<IRaftClusterMember>();
        var first = member.PreVoteAsync(0L, 0L, 0L, TestToken);
        var second = member.PreVoteAsync(0L, 0L, 0L, TestToken);
        var pending = network.PendingMessages;

        Equal(2, pending.Count);
        await network.DeliverAsync(pending[1]);
        True(second.IsCompletedSuccessfully);
        False(first.IsCompleted);

        network.Drop(pending[0]);
        await ThrowsAsync<MemberUnavailableException>(first);
        Empty(network.PendingMessages);
    }

    [Fact]
    public static async Task SupportsPartitionsCancellationAndInjectedFailures()
    {
        var timeProvider = new ManualTimeProvider();
        var network = new InProcessNetwork();
        EndPoint[] membership = [Address("node-a"), Address("node-b")];
        using var stateA = new ConsensusOnlyState();
        using var stateB = new ConsensusOnlyState();
        await using var nodeA = CreateNode(network, timeProvider, membership, 0, stateA);
        await using var nodeB = CreateNode(network, timeProvider, membership, 1, stateB);
        await StartAsync(nodeA, nodeB);
        timeProvider.Advance(TimeSpan.FromMilliseconds(100));

        var member = nodeA.GetMember(nodeB.EndPoint);
        network.Partition(nodeA.EndPoint, nodeB.EndPoint, bidirectional: false);
        await ThrowsAsync<MemberUnavailableException>(
            member.As<IRaftClusterMember>().PreVoteAsync(0L, 0L, 0L, TestToken));

        var reverseMember = nodeB.GetMember(nodeA.EndPoint);
        True((await reverseMember.As<IRaftClusterMember>().PreVoteAsync(0L, 0L, 0L, TestToken)).Value is PreVoteResult.Accepted);

        network.Heal(nodeA.EndPoint, nodeB.EndPoint, bidirectional: false);
        True((await member.As<IRaftClusterMember>().PreVoteAsync(0L, 0L, 0L, TestToken)).Value is PreVoteResult.Accepted);

        network.Partition(nodeA.EndPoint, nodeB.EndPoint);
        await ThrowsAsync<MemberUnavailableException>(
            member.As<IRaftClusterMember>().PreVoteAsync(0L, 0L, 0L, TestToken));
        await ThrowsAsync<MemberUnavailableException>(
            reverseMember.As<IRaftClusterMember>().PreVoteAsync(0L, 0L, 0L, TestToken));
        network.Heal(nodeA.EndPoint, nodeB.EndPoint);

        network.Hold(nodeA.EndPoint, nodeB.EndPoint);
        using (var cancellation = new CancellationTokenSource())
        {
            var canceledRequest = member.As<IClusterMember>().GetMetadataAsync(refresh: true, cancellation.Token).AsTask();
            cancellation.Cancel();
            await ThrowsAnyAsync<OperationCanceledException>(canceledRequest);
        }

        network.Release(nodeA.EndPoint, nodeB.EndPoint);
        var injected = network.FailNext(
            nodeA.EndPoint,
            nodeB.EndPoint,
            RaftMessageType.PreVote,
            new ArithmeticException("Injected RPC failure."));
        await ThrowsAsync<ArithmeticException>(
            member.As<IRaftClusterMember>().PreVoteAsync(0L, 0L, 0L, TestToken));
        await injected.WaitAsync(TestToken);
        True((await member.As<IRaftClusterMember>().PreVoteAsync(0L, 0L, 0L, TestToken)).Value is PreVoteResult.Accepted);
        Empty(network.PendingMessages);
    }

    [Fact]
    public static async Task RestartRetainsExplicitDurableStateAndResetsVolatileState()
    {
        var timeProvider = new ManualTimeProvider();
        var network = new InProcessNetwork();
        EndPoint[] membership = [Address("node-a"), Address("node-b")];
        using var stateA = new ConsensusOnlyState();
        using var stateB = new ConsensusOnlyState();
        IPersistentState durableState = stateA;
        await using var nodeA = new InProcessCluster(
            network,
            "node-a",
            membership,
            durableState,
            timeProvider,
            TimeSpan.FromMilliseconds(100),
            startFollower: false);
        await using var nodeB = CreateNode(network, timeProvider, membership, 1, stateB);
        await nodeA.StartAsync(TestToken);
        await nodeB.StartAsync(TestToken);
        await durableState.UpdateTermAsync(3L, resetLastVote: true, TestToken);
        var index = await durableState.AppendAsync<EmptyLogEntry>(new EmptyLogEntry { Term = 3L }, TestToken);
        await durableState.CommitAsync(index, TestToken);

        var acknowledged = await nodeB.GetMember(nodeA.EndPoint).As<IRaftClusterMember>()
            .AppendEntriesAsync<EmptyLogEntry, EmptyLogEntry[]>(
                3L, [new EmptyLogEntry { Term = 3L }], index, 3L, index, TestToken);
        Equal(HeartbeatResult.ReplicatedWithLeaderTerm, acknowledged.Value.Result);
        Equal(index + 1L, durableState.LastEntryIndex);
        Equal(index, durableState.LastCommittedEntryIndex);
        NotNull(nodeA.Leader);

        network.Hold(nodeA.EndPoint, nodeB.EndPoint);
        network.Hold(nodeB.EndPoint, nodeA.EndPoint);
        var staleMember = nodeA.GetMember(nodeB.EndPoint);
        var pendingRequest = staleMember.As<IClusterMember>().GetMetadataAsync(refresh: true, TestToken).AsTask();
        var inbound = nodeB.GetMember(nodeA.EndPoint).As<IClusterMember>()
            .GetMetadataAsync(refresh: true, TestToken).AsTask();
        var staleMessages = network.PendingMessages;
        Same(staleMember, nodeA.GetMember(nodeB.EndPoint));

        var replacement = await nodeA.RestartAsync(
            state =>
            {
                Same(durableState, state);
                return state;
            },
            TestToken);
        await using (replacement)
        {
            await ThrowsAsync<MemberUnavailableException>(pendingRequest);
            await ThrowsAsync<MemberUnavailableException>(inbound);
            await ThrowsAsync<MemberUnavailableException>(
                staleMember.As<IClusterMember>().GetMetadataAsync(refresh: true, TestToken).AsTask());
            foreach (var message in staleMessages)
                await ThrowsAsync<InvalidOperationException>(() => network.DeliverAsync(message));
            Same(durableState, replacement.AuditTrail);
            Equal(3L, replacement.Term);
            Equal(index, replacement.AuditTrail.LastCommittedEntryIndex);
            Equal(index + 1L, replacement.AuditTrail.LastEntryIndex);
            Equal(3L, await replacement.AuditTrail.GetTermAsync(index + 1L, TestToken));
            NotSame(staleMember, replacement.GetMember(nodeB.EndPoint));
            Null(replacement.Leader);
            Empty(network.PendingMessages);

            network.Release(nodeB.EndPoint, nodeA.EndPoint);
            await nodeB.GetMember(nodeA.EndPoint).As<IClusterMember>().GetMetadataAsync(refresh: true, TestToken);
        }
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public static async Task StoppingNodeCancelsAndDrainsActiveDispatches(bool stopSource)
    {
        var timeProvider = new ManualTimeProvider();
        var network = new InProcessNetwork();
        EndPoint[] membership = [Address("node-a"), Address("node-b")];
        using var stateA = new ConsensusOnlyState();
        using var stateB = new ConsensusOnlyState();
        await using var nodeA = CreateNode(network, timeProvider, membership, 0, stateA);
        await using var nodeB = CreateNode(network, timeProvider, membership, 1, stateB);
        await StartAsync(nodeA, nodeB);

        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var canceled = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var member = nodeB.GetMember(nodeA.EndPoint);
        var dispatch = network.SendAsync(
            member,
            RaftMessageType.Metadata,
            async (target, token) =>
            {
                entered.TrySetResult();
                try
                {
                    await Task.Delay(Timeout.InfiniteTimeSpan, token);
                }
                finally
                {
                    canceled.TrySetResult();
                    await release.Task;
                }
                return target.Metadata;
            },
            TestToken);

        await entered.Task.WaitAsync(TestToken);
        var stoppingNode = stopSource ? nodeB : nodeA;
        var draining = stoppingNode.StopAsync(TestToken);

        try
        {
            await canceled.Task.WaitAsync(TestToken);
            False(draining.IsCompleted);
            False(dispatch.IsCompleted);
        }
        finally
        {
            release.TrySetResult();
        }

        await ThrowsAsync<MemberUnavailableException>(dispatch);
        await draining.WaitAsync(TestToken);
    }

    [Fact]
    public static async Task ReplicationWorkerSurvivesInjectedFailure()
    {
        var timeProvider = new ManualTimeProvider();
        var network = new InProcessNetwork();
        EndPoint[] membership = [Address("node-a"), Address("node-b"), Address("node-c")];
        using var stateA = new ConsensusOnlyState();
        using var stateB = new ConsensusOnlyState();
        using var stateC = new ConsensusOnlyState();
        await using var nodeA = CreateNode(network, timeProvider, membership, 0, stateA);
        await using var nodeB = CreateNode(network, timeProvider, membership, 1, stateB);
        await using var nodeC = CreateNode(network, timeProvider, membership, 2, stateC);
        await StartAsync(nodeA, nodeB, nodeC);
        nodeA.StartElectionTimer();
        timeProvider.Advance(TimeSpan.FromMilliseconds(100));
        await nodeA.WaitForLeaderAsync(TimeSpan.FromSeconds(5), TestToken);
        await nodeA.ForceReplicationAsync(TestToken);
        await nodeA.WaitForLeadershipAsync(TestToken);

        // Freeze both workers at an actual RPC before injecting the next round's failure.
        network.Hold(nodeA.EndPoint, nodeB.EndPoint);
        network.Hold(nodeA.EndPoint, nodeC.EndPoint);
        var round = nodeA.ForceReplicationAsync(TestToken).AsTask();
        var toB = await network.WaitForMessageAsync(nodeA.EndPoint, nodeB.EndPoint, RaftMessageType.AppendEntries, TestToken);
        var toC = await network.WaitForMessageAsync(nodeA.EndPoint, nodeC.EndPoint, RaftMessageType.AppendEntries, TestToken);
        var injected = network.FailNext(nodeA.EndPoint, nodeB.EndPoint, RaftMessageType.AppendEntries,
            new ArithmeticException("Injected replication worker failure."));
        network.Release(nodeA.EndPoint, nodeB.EndPoint);
        network.Release(nodeA.EndPoint, nodeC.EndPoint);
        await network.DeliverAsync(toB);
        await network.DeliverAsync(toC);
        await round;

        await nodeA.ReplicateAsync(new EmptyLogEntry { Term = nodeA.Term }, TestToken);
        await injected.WaitAsync(TestToken);
        await nodeA.ForceReplicationAsync(TestToken);
        var committed = nodeA.AuditTrail.LastCommittedEntryIndex;
        await nodeB.AuditTrail.WaitForApplyAsync(committed, TestToken);
        Equal(committed, nodeB.AuditTrail.LastEntryIndex);
        False(nodeA.LeadershipToken.IsCancellationRequested);
    }

    [Fact]
    public static async Task CancelingDeliveredMessageWaitsForHandlerCleanup()
    {
        var timeProvider = new ManualTimeProvider();
        var network = new InProcessNetwork();
        EndPoint[] membership = [Address("node-a"), Address("node-b")];
        using var stateA = new ConsensusOnlyState();
        using var stateB = new ConsensusOnlyState();
        await using var nodeA = CreateNode(network, timeProvider, membership, 0, stateA);
        await using var nodeB = CreateNode(network, timeProvider, membership, 1, stateB);
        await StartAsync(nodeA, nodeB);
        network.Hold(nodeA.EndPoint, nodeB.EndPoint);
        using var cancellation = new CancellationTokenSource();
        var canceled = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var request = network.SendAsync(nodeA.GetMember(nodeB.EndPoint), RaftMessageType.Metadata,
            async (target, token) =>
            {
                try
                {
                    await Task.Delay(Timeout.InfiniteTimeSpan, token);
                }
                finally
                {
                    canceled.TrySetResult();
                    await release.Task;
                }
                return target.Metadata;
            }, cancellation.Token);
        var delivery = network.DeliverAsync(Single(network.PendingMessages));
        cancellation.Cancel();
        try
        {
            await canceled.Task.WaitAsync(TestToken);
            False(request.IsCompleted);
            False(delivery.IsCompleted);
        }
        finally
        {
            release.TrySetResult();
        }

        await delivery;
        await ThrowsAnyAsync<OperationCanceledException>(request);
        Empty(network.PendingMessages);
    }

    [Fact]
    public static async Task SnapshotUsesProductionConfigurationAndSnapshotHandlers()
    {
        var timeProvider = new ManualTimeProvider();
        var network = new InProcessNetwork();
        EndPoint[] membership = [Address("node-a"), Address("node-b")];
        using var stateA = new ConsensusOnlyState();
        using var stateB = new ConsensusOnlyState();
        await using var nodeA = CreateNode(network, timeProvider, membership, 0, stateA);
        await using var nodeB = CreateNode(network, timeProvider, membership, 1, stateB);
        await StartAsync(nodeA, nodeB);
        var snapshot = new EmptyLogEntry { Term = 1L, IsSnapshot = true };
        False(await nodeB.As<ILocalMember>().InstallConfigurationAsync(1L, snapshot, 0L, TestToken));
        var response = await nodeA.GetMember(nodeB.EndPoint).As<IRaftClusterMember>()
            .InstallSnapshotAsync(1L, snapshot, 6L, snapshot, 0L, TestToken);

        Equal(HeartbeatResult.ReplicatedWithLeaderTerm, response.Value);
        Equal(6L, stateB.LastCommittedEntryIndex);
        Equal(6L, stateB.LastEntryIndex);
        Equal(1L, stateB.Term);
    }

    [Fact]
    public static async Task RaftTimeoutUsesManualTimeAndPreservesCancellation()
    {
        var timeProvider = new ManualTimeProvider();
        using var signal = new AsyncAutoResetEvent(false);
        var timedOut = RaftTimer.WaitAsync(signal, TimeSpan.FromMilliseconds(100), timeProvider, TestToken).AsTask();
        timeProvider.Advance(TimeSpan.FromMilliseconds(99));
        False(timedOut.IsCompleted);
        timeProvider.Advance(TimeSpan.FromMilliseconds(1));
        False(await timedOut);

        var signaled = RaftTimer.WaitAsync(signal, TimeSpan.FromMilliseconds(100), timeProvider, TestToken).AsTask();
        signal.Set();
        True(await signaled);

        using var cancellation = new CancellationTokenSource();
        var canceled = RaftTimer.WaitAsync(signal, TimeSpan.FromMilliseconds(100), timeProvider, cancellation.Token).AsTask();
        cancellation.Cancel();
        var exception = await ThrowsAnyAsync<OperationCanceledException>(canceled);
        Equal(cancellation.Token, exception.CancellationToken);
        timeProvider.Advance(TimeSpan.FromMilliseconds(100));
    }

    [Fact]
    public static void ManualTimeRunsTimersInDueOrder()
    {
        var timeProvider = new ManualTimeProvider();
        var callbacks = new List<int>();
        using var later = timeProvider.CreateTimer(
            _ => callbacks.Add(2),
            null,
            TimeSpan.FromSeconds(2),
            Timeout.InfiniteTimeSpan);
        using var sooner = timeProvider.CreateTimer(
            _ => callbacks.Add(1),
            null,
            TimeSpan.FromSeconds(1),
            Timeout.InfiniteTimeSpan);
        using var zeroPeriod = timeProvider.CreateTimer(
            _ => callbacks.Add(3),
            null,
            TimeSpan.FromSeconds(1),
            TimeSpan.Zero);

        timeProvider.Advance(TimeSpan.FromSeconds(2));

        Equal([1, 3, 2], callbacks);
    }

    private static InProcessCluster CreateNode(
        InProcessNetwork network,
        TimeProvider timeProvider,
        IReadOnlyList<EndPoint> membership,
        int index,
        IPersistentState state)
        => new(
            network,
            ((DnsEndPoint)membership[index]).Host,
            membership,
            state,
            timeProvider,
            TimeSpan.FromMilliseconds(100),
            startFollower: false);

    private static async Task StartAsync(params InProcessCluster[] nodes)
    {
        foreach (var node in nodes)
            await node.StartAsync(TestToken);
    }

    private static DnsEndPoint Address(string host) => new(host, 0);
}
