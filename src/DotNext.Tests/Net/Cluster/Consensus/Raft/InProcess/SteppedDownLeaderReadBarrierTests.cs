using System.Runtime.CompilerServices;

namespace DotNext.Net.Cluster.Consensus.Raft.InProcess;

using static MembershipClusterFixture;

/// <summary>
/// Regression for #65: a read barrier on a leader that stepped down on a higher term must not
/// spin synchronously while the node has not heard from the new leader yet.
/// </summary>
public sealed class SteppedDownLeaderReadBarrierTests : RaftTest
{
    [Fact(Timeout = TestTimeouts.Default)]
    public static async Task ReadBarrierDoesNotSpinAfterStepDown()
    {
        await using var cluster = new MembershipClusterFixture();
        await cluster.StartAsync();
        var (oldLeader, newLeader) = (cluster.Nodes[0], cluster.Nodes[2]);
        await cluster.ElectAsync(oldLeader);
        await cluster.PumpAsync(oldLeader, newLeader.Log.WaitForApplyAsync(oldLeader.Log.LastCommittedEntryIndex, TestToken).AsTask());

        // {0} | {1, 2, 3, 4}: the majority elects a new leader that the former leader never hears from
        await cluster.ElectAsync(newLeader, filter: message => message.TargetId == oldLeader.Id ? MessageAction.Drop : MessageAction.Deliver);
        Equal(2L, newLeader.Term);
        var newerWrite = newLeader.Log.LastCommittedEntryIndex;
        True(oldLeader.Log.LastAppliedIndex < newerWrite);

        // the former leader learns the higher term from replication responses and steps down
        await cluster.PumpAsync(oldLeader, WaitForStepDownAsync(oldLeader));
        Equal(2L, oldLeader.Term);

        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(TestToken);
        deadline.CancelAfter(TimeSpan.FromSeconds(5));
        var invocation = Task.Factory.StartNew(
            () => oldLeader.ApplyReadBarrierAsync(ReadBarrierType.Strong, deadline.Token).AsTask(),
            TestToken,
            TaskCreationOptions.DenyChildAttach,
            TaskScheduler.Default);

        // the call ends on its own only if it returns without spinning until the deadline
        var read = await invocation;
        False(deadline.IsCancellationRequested, "The read barrier spun synchronously until it was canceled.");

        // the node has no known leader, so the read cannot be authorized
        IsType<QuorumUnreachableException>(await Record.ExceptionAsync(() => read.WaitAsync(DefaultTimeout, TestToken)));

        // once the new leader is known, a read observes its writes
        await cluster.PumpAsync(newLeader, WaitForLeaderAsync(oldLeader, newLeader));
        var retry = oldLeader.ApplyReadBarrierAsync(ReadBarrierType.Strong, TestToken).AsTask();
        await cluster.PumpAllAsync(retry, leader: newLeader);
        True(oldLeader.Log.LastAppliedIndex >= newerWrite);
    }

    private static async Task WaitForStepDownAsync(MembershipNode node)
    {
        while (Accessors<InProcessClusterMember>.State(node) is not FollowerState<InProcessClusterMember>)
            await Task.Delay(1, TestToken);
    }

    private static async Task WaitForLeaderAsync(MembershipNode node, MembershipNode leader)
    {
        while (node.Leader?.Id != leader.Id)
            await Task.Delay(1, TestToken);
    }

    private static class Accessors<TMember>
        where TMember : class, IRaftClusterMember, IDisposable
    {
        [UnsafeAccessor(UnsafeAccessorKind.Field, Name = "state")]
        internal static extern ref RaftState<TMember> State(RaftCluster<TMember> cluster);
    }
}
