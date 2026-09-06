using System.Net;

namespace DotNext.Net.Cluster.Consensus.Raft.InProcess;

using IO;
using StateMachine;

public sealed class CommitIndexTests : RaftTest
{
    [Fact]
    public static async Task SnapshotAcknowledgmentDoesNotCommitMinorityTail()
    {
        var timeProvider = new ManualTimeProvider();
        var network = new InProcessNetwork();
        EndPoint[] membership = Enumerable.Range(0, 5)
            .Select(i => new DnsEndPoint($"node-{i}", 0)).ToArray();
        var location = GetTempPath();
        await using var machine = new SnapshotAtSix(new(Path.Combine(location, "snapshot")));
        await using var stateA = new WriteAheadLog(new() { Location = location }, machine);
        using var stateB = new ConsensusOnlyState();
        using var stateC = new ConsensusOnlyState();
        using var stateD = new ConsensusOnlyState();
        using var stateE = new ConsensusOnlyState();
        await using var nodeA = CreateNode(0, stateA);
        await using var nodeB = CreateNode(1, stateB);
        await using var nodeC = CreateNode(2, stateC);
        await using var nodeD = CreateNode(3, stateD);
        await using var nodeE = CreateNode(4, stateE);
        InProcessCluster[] followers = [nodeB, nodeC, nodeD, nodeE];

        foreach (var node in new[] { nodeA, nodeB, nodeC, nodeD, nodeE })
            await node.StartAsync(TestToken);

        foreach (var node in followers)
            network.Hold(nodeA.EndPoint, node.EndPoint);
        nodeA.StartElectionTimer();
        timeProvider.Advance(TimeSpan.FromMilliseconds(100));
        foreach (var node in followers)
            await network.DeliverAsync(await PendingAsync(node, RaftMessageType.PreVote));
        foreach (var node in followers)
            await network.DeliverAsync(await PendingAsync(node, RaftMessageType.Vote));
        await nodeA.WaitForLeaderAsync(TimeSpan.FromSeconds(5), TestToken);

        // Observe the automatic first round before requesting its retry, so the
        // force waiter belongs to the retry rather than racing the first valve.
        var initial = new List<PendingMessage>();
        foreach (var node in followers)
            initial.Add(await PendingAsync(node, RaftMessageType.AppendEntries));
        var electionRound = nodeA.ForceReplicationAsync(TestToken).AsTask();
        foreach (var message in initial)
            await network.DeliverAsync(message);
        await CompleteHealthyRoundAsync(RaftMessageType.AppendEntries);
        await electionRound;
        await nodeA.WaitForLeadershipAsync(TestToken);
        Equal(1L, stateA.LastCommittedEntryIndex);

        // C misses every committed entry. Account for its actual RPC in every
        // round: a delayed worker must not contribute an old snapshot response
        // to the decisive round. A/B/D/E establish the history normally.
        for (var index = 2L; index <= 7L; index++)
        {
            Equal(index, await stateA.AppendAsync(new TestLogEntry("no-op") { Term = nodeA.Term }, TestToken));
            var replication = nodeA.ForceReplicationAsync(TestToken).AsTask();
            await CompleteHealthyRoundAsync(RaftMessageType.AppendEntries);
            await replication;
        }

        await stateA.WaitForApplyAsync(7L, TestToken);
        Equal(7L, stateA.LastCommittedEntryIndex);
        Equal(0L, stateC.LastEntryIndex);
        var snapshot = machine.As<ISnapshotManager>().Snapshot;
        NotNull(snapshot);
        Equal(6L, snapshot.Index);
        Equal(nodeA.Term, snapshot.Term);

        // Propagate commit 7, dropping C's snapshot before the next round starts.
        var preparation = nodeA.ForceReplicationAsync(TestToken).AsTask();
        await CompleteHealthyRoundAsync(RaftMessageType.InstallSnapshot);
        await preparation;
        foreach (var node in new[] { nodeB, nodeD, nodeE })
            await node.AuditTrail.WaitForApplyAsync(7L, TestToken);

        // Local proposals are uncommitted until the production leader selects
        // an index. Only B will receive this tail in the decisive round.
        for (var index = 8L; index <= 10L; index++)
            Equal(index, await stateA.AppendAsync(new EmptyLogEntry { Term = nodeA.Term }, TestToken));
        Equal(7L, stateA.LastCommittedEntryIndex);

        var round = nodeA.ForceReplicationAsync(TestToken).AsTask();
        var toB = await PendingAsync(nodeB, RaftMessageType.AppendEntries);
        var toC = await PendingAsync(nodeC, RaftMessageType.InstallSnapshot);
        var toD = await PendingAsync(nodeD, RaftMessageType.AppendEntries);
        var toE = await PendingAsync(nodeE, RaftMessageType.AppendEntries);
        await network.DeliverAsync(toB);
        await network.DeliverAsync(toC);
        var acknowledgment = await IsType<Task<Result<HeartbeatResult>>>(toC.Completion);
        Equal(nodeA.Term, nodeC.Term);
        Equal(HeartbeatResult.ReplicatedWithLeaderTerm, acknowledgment.Value);
        Equal(6L, stateC.LastEntryIndex);
        Equal(6L, stateC.LastCommittedEntryIndex);
        Equal(nodeA.Term, await nodeC.AuditTrail.GetTermAsync(6L, TestToken));
        Equal(10L, stateA.LastEntryIndex);
        Equal(10L, stateB.LastEntryIndex);
        Equal(7L, stateD.LastEntryIndex);
        Equal(7L, stateE.LastEntryIndex);

        // D/E are black-holed until the responsive quorum [10, 10, 6]
        // completes. Their unavailable results arrive only after selection.
        await round;
        network.Drop(toD);
        network.Drop(toE);
        TestContext.Current.TestOutputHelper.WriteLine(
            $"Snapshot acknowledged: index={stateC.LastEntryIndex}, term={snapshot.Term}, result={acknowledgment.Value}; " +
            $"leader/follower tails=10/10; expected commit=7, observed commit={stateA.LastCommittedEntryIndex}.");
        Equal(7L, stateA.LastCommittedEntryIndex);

        InProcessCluster CreateNode(int index, IPersistentState state)
            => new(network, ((DnsEndPoint)membership[index]).Host, membership, state,
                timeProvider, TimeSpan.FromMilliseconds(100), startFollower: false);

        Task<PendingMessage> PendingAsync(InProcessCluster node, RaftMessageType type)
            => network.WaitForMessageAsync(nodeA.EndPoint, node.EndPoint, type, TestToken);

        async Task CompleteHealthyRoundAsync(RaftMessageType catchUpType)
        {
            var toB = await PendingAsync(nodeB, RaftMessageType.AppendEntries);
            var toC = await PendingAsync(nodeC, catchUpType);
            var toD = await PendingAsync(nodeD, RaftMessageType.AppendEntries);
            var toE = await PendingAsync(nodeE, RaftMessageType.AppendEntries);
            network.Drop(toC);
            await network.DeliverAsync(toB);
            await network.DeliverAsync(toD);
            await network.DeliverAsync(toE);
        }
    }

    // SimpleStateMachine creates a real snapshot with the applied entry's
    // term/index; applying entry 7 publishes the completed snapshot at 6.
    private sealed class SnapshotAtSix(DirectoryInfo location) : SimpleStateMachine(location)
    {
        protected override ValueTask<bool> ApplyAsync(LogEntry entry, CancellationToken token)
            => ValueTask.FromResult(entry.Index is 6L);

        protected override ValueTask PersistAsync(IAsyncBinaryWriter writer, CancellationToken token)
            => ValueTask.CompletedTask;

        protected override ValueTask RestoreAsync(FileInfo snapshotFile, CancellationToken token)
            => ValueTask.CompletedTask;
    }
}
