namespace DotNext.Net.Cluster.Consensus.Raft.InProcess;

public sealed class MembershipLockTests : RaftTest
{
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public static async Task CompletedDetectionAllowsManualMembershipChange(bool add)
    {
        await using var cluster = new MembershipClusterFixture();
        await cluster.StartAsync();
        var leader = cluster.Leader;
        var caller = leader.CaptureCaller();
        var before = leader.Log.LastEntryIndex;

        await leader.DetectAsync(caller, cluster.Nodes[4].EndPoint, leader.LeadershipToken);
        True(caller.Cleared);
        Equal(1, caller.Validations);
        using (var entries = await leader.Log.ReadAsync(before + 1L, leader.Log.LastEntryIndex, TestToken))
            True(entries[^1].IsConfiguration);
        TestContext.Current.TestOutputHelper.WriteLine(
            $"Dispatcher completed; caller cleared={caller.Cleared}; membership lock held={leader.IsMembershipLockHeld}.");

        if (add)
        {
            True(await leader.AddAsync(cluster.Nodes[^1].EndPoint, TestToken));
            await leader.WaitForMembersAsync(members => members.Contains(cluster.Nodes[^1].EndPoint));
        }
        else
        {
            await cluster.RemoveAsync(cluster.Nodes[3]);
        }
        False(leader.IsMembershipLockHeld);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public static async Task CallbackFailureReleasesOwnership(bool cancel)
    {
        await using var cluster = new MembershipClusterFixture();
        await cluster.StartAsync();
        var leader = cluster.Leader;
        using var source = new CancellationTokenSource();
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var failure = new IOException("Unavailable-member callback failed.");
        leader.OnUnavailable = async (_, _, token) =>
        {
            entered.SetResult();
            await release.Task.WaitAsync(token);
            throw failure;
        };
        var caller = leader.CaptureCaller();
        var detection = leader.DetectAsync(caller, cluster.Nodes[4].EndPoint, source.Token);
        await entered.Task;
        True(leader.IsMembershipLockHeld);
        if (cancel)
            source.Cancel();
        else
            release.SetResult();
        await detection;
        True(caller.Cleared);
        var logged = Single(leader.Errors.Exceptions);
        if (cancel)
            Equal(source.Token, IsAssignableFrom<OperationCanceledException>(logged).CancellationToken);
        else
            Same(failure, logged);
        False(leader.IsMembershipLockHeld);
        leader.OnUnavailable = null;
        await cluster.RemoveAsync(cluster.Nodes[3]);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public static async Task CanceledWaiterDoesNotReleaseAnotherOwner(bool alreadyCanceled)
    {
        await using var cluster = new MembershipClusterFixture();
        await cluster.StartAsync();
        var leader = cluster.Leader;
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        leader.OnUnavailable = async (_, _, token) =>
        {
            entered.TrySetResult();
            await release.Task.WaitAsync(token);
        };
        var owner = leader.DetectAsync(leader.CaptureCaller(), cluster.Nodes[4].EndPoint, leader.LeadershipToken);
        await entered.Task;
        using var source = new CancellationTokenSource();
        if (alreadyCanceled)
            source.Cancel();
        var caller = leader.CaptureCaller();
        var waiter = leader.DetectAsync(caller, cluster.Nodes[3].EndPoint, source.Token);
        if (!alreadyCanceled)
        {
            False(waiter.IsCompleted);
            source.Cancel();
        }
        await waiter;
        True(caller.Cleared);
        Equal(0, caller.Validations);
        True(leader.IsMembershipLockHeld);
        Empty(leader.Errors.Exceptions);
        await ThrowsAsync<RaftCluster<InProcessClusterMember>.ConcurrentMembershipModificationException>(
            leader.RemoveAsync(cluster.Nodes[2].EndPoint, TestToken));
        release.SetResult();
        await owner;
        False(leader.IsMembershipLockHeld);
        leader.OnUnavailable = null;
        await cluster.RemoveAsync(cluster.Nodes[2]);
    }

    [Fact]
    public static async Task StaleCallerAfterAcquisitionReleasesOwnership()
    {
        await using var cluster = new MembershipClusterFixture();
        await cluster.StartAsync();
        var leader = cluster.Leader;
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        leader.OnUnavailable = async (_, _, token) =>
        {
            entered.SetResult();
            await Task.Delay(Timeout.Infinite, token);
        };
        var caller = leader.CaptureCaller();
        var queuedCaller = leader.CaptureCaller();
        var detection = leader.DetectAsync(caller, cluster.Nodes[4].EndPoint, leader.LeadershipToken);
        await entered.Task;
        True(leader.IsMembershipLockHeld);
        // Keep this dispatch alive to exercise stale identity validation independently of cancellation.
        var queued = leader.DetectAsync(queuedCaller, cluster.Nodes[3].EndPoint, TestToken);
        False(queued.IsCompleted);
        True(await leader.ResignAsync());
        await detection;
        True(caller.Cleared);
        await queued.WaitAsync(DefaultTimeout, TestToken);
        True(queuedCaller.Cleared);
        Equal(1, queuedCaller.Validations);
        False(leader.IsMembershipLockHeld);
        leader.OnUnavailable = null;
        foreach (var node in cluster.Nodes)
            cluster.Hold(node);
        await cluster.PumpAsync(leader, cluster.ElectAsync(leader));
        var change = leader.RemoveAsync(cluster.Nodes[2].EndPoint, TestToken);
        await cluster.PumpAsync(leader, change);
        True(await change);
    }

    [Fact]
    public static async Task AutomaticDetectionAndQueuedCallbackRemainSerialized()
    {
        await using var cluster = new MembershipClusterFixture();
        await cluster.StartAsync();
        var leader = cluster.Leader;
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var callbacks = 0;
        // Keep this callback notification-only: removal barriers can legitimately
        // schedule more notifications. Configuration removal is covered separately.
        leader.OnUnavailable = async (_, _, token) =>
        {
            if (Interlocked.Increment(ref callbacks) is 1)
            {
                entered.SetResult();
                await release.Task.WaitAsync(token);
            }
        };
        // Require the detector's worker for this round's quorum. Its health check
        // finishes before it contributes, unlike an arbitrary late follower.
        foreach (var node in cluster.Nodes[2..5])
            cluster.Network.Hold(leader.EndPoint, node.EndPoint);
        await leader.AuditTrail.AppendAsync(new EmptyLogEntry { Term = leader.Term }, TestToken);
        var round = leader.ForceReplicationAsync(TestToken).AsTask();
        var failedMemberReply = await cluster.Network.WaitForReplicationAsync(
            leader.EndPoint, cluster.Nodes[4].EndPoint, TestToken);
        leader.Detectors[cluster.Nodes[4].EndPoint].Fail();
        await cluster.Network.DeliverAsync(failedMemberReply);
        cluster.Network.Release(leader.EndPoint, cluster.Nodes[4].EndPoint);
        foreach (var node in cluster.Nodes[2..4])
        {
            var message = await cluster.Network.WaitForReplicationAsync(leader.EndPoint, node.EndPoint, TestToken);
            cluster.Network.Release(leader.EndPoint, node.EndPoint);
            cluster.Network.Drop(message);
        }
        await round.WaitAsync(DefaultTimeout, TestToken);
        await leader.ForceReplicationAsync(TestToken);
        await entered.Task.WaitAsync(DefaultTimeout, TestToken);
        True(leader.IsMembershipLockHeld);
        var caller = leader.CaptureCaller();
        var next = leader.DetectAsync(caller, cluster.Nodes[3].EndPoint, TestToken);
        False(next.IsCompleted);
        Equal(1, Volatile.Read(ref callbacks));
        await ThrowsAsync<RaftCluster<InProcessClusterMember>.ConcurrentMembershipModificationException>(
            leader.RemoveAsync(cluster.Nodes[2].EndPoint, TestToken));
        release.SetResult();
        await next.WaitAsync(DefaultTimeout, TestToken);
        True(caller.Cleared);
        Equal(2, Volatile.Read(ref callbacks));
        False(leader.IsMembershipLockHeld);
    }
}
