using System.Net;
using System.Runtime.CompilerServices;

namespace DotNext.Net.Cluster.Consensus.Raft.InProcess;

using IO;
using IO.Log;
using Membership;
using NetworkTransport;
using static MembershipClusterFixture;

/// <summary>
/// #49: the active configuration of every node is the latest configuration in its log, committed or not
/// (Ongaro's thesis, §4.1).
/// </summary>
public sealed class LogDerivedConfigurationTests : RaftTest
{
    [Fact(Timeout = TestTimeouts.Default)]
    public static async Task TruncatedConfigurationIsReverted()
    {
        await using var cluster = new MembershipClusterFixture();
        await cluster.StartAsync();
        var (n0, n1, n2, n4) = (cluster.Nodes[0], cluster.Nodes[1], cluster.Nodes[2], cluster.Nodes[4]);
        await cluster.ElectAsync(n0);
        await cluster.PumpAsync(n0, n0.ForceReplicationAsync(TestToken).AsTask());

        // term 1: the removal of node 4 reaches node 1 only
        await n0.DetectAsync(n4.EndPoint);
        const long removalIndex = 2L;
        await cluster.ReplicateOnlyToAsync(n0, n1, removalIndex);
        True(n1.Log.LastCommittedEntryIndex < removalIndex);
        DoesNotContain(n4.EndPoint, EndPoints(n1));

        // term 2: node 2 is elected by {2,3,4}, its no-op overwrites the removal on nodes 0 and 1
        await cluster.ElectAsync(n2);
        await cluster.PumpAsync(n2, WaitUntilAsync(() => n0.Log.LastCommittedEntryIndex >= removalIndex
                                                          && n1.Log.LastCommittedEntryIndex >= removalIndex));

        var voters = cluster.Voters.ToHashSet();
        foreach (var node in new[] { n0, n1, n2 })
            Equal(voters, EndPoints(node));
    }

    [Fact(Timeout = TestTimeouts.Default)]
    public static async Task CanceledOverwriteRefreshesPublishedConfiguration()
    {
        await using var cluster = new MembershipClusterFixture();
        await cluster.StartAsync();
        var (n1, n2, n4) = await ReplicateUncommittedRemovalAsync(cluster);
        var stateVersion = ((ILocalMember)n2).Version;

        using var cancellation = new CancellationTokenSource();
        await using var entries = new LogEntryProducer<IRaftLogEntry>(
        [
            new DurableEntry { Term = 2L },
            new PartiallyFailingEntry(cancellation.Cancel, () => new OperationCanceledException(cancellation.Token)) { Term = 2L },
        ]);

        await ThrowsAnyAsync<OperationCanceledException>(() => ((ILocalMember)n1)
            .AppendEntriesAsync(n2.Id, 2L, entries, 1L, 1L, 1L, stateVersion, cancellation.Token)
            .AsTask());

        Contains(n4.EndPoint, EndPoints(n1));
    }

    [Fact(Timeout = TestTimeouts.Default)]
    public static async Task RestartRebuildsConfigurationFromLog()
    {
        await using var cluster = new MembershipClusterFixture();
        await cluster.StartAsync();
        var (n0, n1, n4, joiner) = (cluster.Nodes[0], cluster.Nodes[1], cluster.Nodes[4], cluster.Joiner);
        await cluster.ElectAsync(n0);

        var addition = n0.AddAsync(joiner.EndPoint, TestToken);
        await cluster.PumpAsync(n0, addition);
        True(await addition);

        // the removal of node 4 reaches node 1 only, so it is never applied there
        await n0.DetectAsync(n4.EndPoint);
        var removalIndex = n0.Log.LastEntryIndex;
        await cluster.ReplicateOnlyToAsync(n0, n1, removalIndex);
        True(n1.Log.LastCommittedEntryIndex < removalIndex);
        Contains(n4.EndPoint, (await n1.LoadConfigurationAsync()).Members);

        n1 = await cluster.RestartAsync(1);

        var expected = cluster.Voters.Append(joiner.EndPoint).Where(address => !address.Equals(n4.EndPoint)).ToHashSet();
        Equal(expected, EndPoints(n1));
        Contains(n4.EndPoint, (await n1.LoadConfigurationAsync()).Members);
    }

    [Fact(Timeout = TestTimeouts.Default)]
    public static async Task OverlappingSnapshotRequestsKeepTheirOwnConfigurations()
    {
        await using var cluster = new MembershipClusterFixture();
        await cluster.StartAsync();
        var (leader, follower, removedA, removedB) = (cluster.Nodes[0], cluster.Nodes[1], cluster.Nodes[3], cluster.Nodes[4]);
        var expectedA = cluster.Voters.Where(address => !address.Equals(removedB.EndPoint)).ToHashSet();
        var expectedB = cluster.Voters.Where(address => !address.Equals(removedA.EndPoint) && !address.Equals(removedB.EndPoint)).ToHashSet();
        const long firstConfigurationVersion = 2L, secondConfigurationVersion = 5L;

        var firstConfiguration = await follower.LoadConfigurationAsync();
        True(IClusterConfiguration<EndPoint>.TryRemove(ref firstConfiguration, removedB.EndPoint));
        var secondConfiguration = await follower.LoadConfigurationAsync();
        True(IClusterConfiguration<EndPoint>.TryRemove(ref secondConfiguration, removedB.EndPoint));
        True(IClusterConfiguration<EndPoint>.TryRemove(ref secondConfiguration, removedA.EndPoint));

        True(await ((ILocalMember)follower).InstallConfigurationAsync(1L, firstConfiguration, firstConfigurationVersion, TestToken));
        True(await ((ILocalMember)follower).InstallConfigurationAsync(1L, secondConfiguration, secondConfigurationVersion, TestToken));

        var firstResult = await ((ILocalMember)follower).InstallSnapshotAsync(
            leader.Id,
            1L,
            new DurableEntry { Term = 1L, IsSnapshot = true },
            firstConfigurationVersion,
            ((ILocalMember)leader).Version,
            TestToken);

        Equal(HeartbeatResult.ReplicatedWithLeaderTerm, firstResult.Value);
        Equal(firstConfigurationVersion, await follower.LoadConfigurationVersionAsync());
        Equal(expectedA, (await follower.LoadConfigurationAsync()).Members.ToHashSet());
        Equal(expectedA, EndPoints(follower));

        var secondResult = await ((ILocalMember)follower).InstallSnapshotAsync(
            leader.Id,
            1L,
            new DurableEntry { Term = 1L, IsSnapshot = true },
            secondConfigurationVersion,
            ((ILocalMember)leader).Version,
            TestToken);

        Equal(HeartbeatResult.ReplicatedWithLeaderTerm, secondResult.Value);
        Equal(secondConfigurationVersion, await follower.LoadConfigurationVersionAsync());
        Equal(expectedB, (await follower.LoadConfigurationAsync()).Members.ToHashSet());
        Equal(expectedB, EndPoints(follower));

        follower = await cluster.RestartAsync(1);
        Equal(expectedB, EndPoints(follower));
    }

    [Fact(Timeout = TestTimeouts.Default)]
    public static async Task SnapshotRetransmissionCompletesConfigurationPromotion()
    {
        await using var cluster = new MembershipClusterFixture();
        await cluster.StartAsync();
        var (leader, follower, removed) = (cluster.Nodes[0], cluster.Nodes[1], cluster.Nodes[4]);
        var original = cluster.Voters.ToHashSet();
        var expected = cluster.Voters.Where(address => !address.Equals(removed.EndPoint)).ToHashSet();
        const long configurationVersion = 2L, snapshotIndex = 5L;

        var configuration = await follower.LoadConfigurationAsync();
        True(IClusterConfiguration<EndPoint>.TryRemove(ref configuration, removed.EndPoint));

        var firstResult = await ((ILocalMember)follower).InstallSnapshotAsync(
            leader.Id,
            1L,
            new DurableEntry { Term = 1L, IsSnapshot = true },
            snapshotIndex,
            ((ILocalMember)leader).Version,
            TestToken);

        Equal(HeartbeatResult.ReplicatedWithLeaderTerm, firstResult.Value);
        Equal(0L, await follower.LoadConfigurationVersionAsync());
        Equal(original, (await follower.LoadConfigurationAsync()).Members.ToHashSet());
        Equal(original, EndPoints(follower));

        True(await ((ILocalMember)follower).InstallConfigurationAsync(1L, configuration, configurationVersion, TestToken));

        var retransmitted = await ((ILocalMember)follower).InstallSnapshotAsync(
            leader.Id,
            1L,
            new DurableEntry { Term = 1L, IsSnapshot = true },
            snapshotIndex,
            ((ILocalMember)leader).Version,
            TestToken);

        Equal(HeartbeatResult.ReplicatedWithLeaderTerm, retransmitted.Value);
        Equal(configurationVersion, await follower.LoadConfigurationVersionAsync());
        Equal(expected, (await follower.LoadConfigurationAsync()).Members.ToHashSet());
        Equal(expected, EndPoints(follower));

        follower = await cluster.RestartAsync(1);
        Equal(expected, EndPoints(follower));
    }

    [Fact(Timeout = TestTimeouts.Default)]
    public static async Task LeaderStepsDownOnceItsRemovalIsCommitted()
    {
        await using var cluster = new MembershipClusterFixture();
        await cluster.StartAsync();
        var n0 = cluster.Nodes[0];
        await cluster.ElectAsync(n0);

        var removal = n0.RemoveAsync(n0.EndPoint, TestToken);
        await cluster.PumpAsync(n0, removal);
        True(await removal);
        var removalIndex = n0.Log.LastCommittedEntryIndex;

        True(n0.Standby);
        True(n0.LeadershipToken.IsCancellationRequested);
        DoesNotContain(n0.EndPoint, EndPoints(n0));

        // the removal is committed by a majority of {1,2,3,4}
        var holders = cluster.Nodes[1..VoterCount].Where(node => node.Log.LastEntryIndex >= removalIndex).ToArray();
        True(holders.Length >= 3);
        foreach (var node in holders)
            DoesNotContain(n0.EndPoint, EndPoints(node));

        // the remaining voters elect a new leader and commit without node 0
        var candidate = holders[0];
        await cluster.ElectAsync(candidate);
        Equal(2L, candidate.Term);
        Equal(candidate.Log.LastEntryIndex, candidate.Log.LastCommittedEntryIndex);
    }

    // CE-1: a leader elected with the latest configuration in its log must count its quorum over it,
    // even though the configuration is not applied yet
    [Fact(Timeout = TestTimeouts.Default)]
    public static async Task LeaderDoesNotCommitUnderStaleConfiguration()
    {
        // voters {0,1,2}, joiners 3 and 4
        await using var cluster = new MembershipClusterFixture(voterCount: 3, joinerCount: 2);
        await cluster.StartAsync();
        var (n0, n1, n2, n3, n4) = (cluster.Nodes[0], cluster.Nodes[1], cluster.Nodes[2], cluster.Nodes[3], cluster.Nodes[4]);

        // term 1: node 0 adds nodes 3 and 4, node 1 and node 2 hold both changes
        await cluster.ElectAsync(n0);
        foreach (var joiner in new[] { n3, n4 })
        {
            var addition = n0.AddAsync(joiner.EndPoint, TestToken);
            await cluster.PumpAsync(n0, addition);
            True(await addition);
        }

        var lastIndex = n0.Log.LastEntryIndex;
        Equal(lastIndex, n1.Log.LastEntryIndex);
        await cluster.PumpAsync(n0, WaitForFollowerAsync(n0), static _ => MessageAction.Drop);

        // term 2: node 1 is elected by {1,2,3,4}, then its no-op reaches node 2 only
        await cluster.ElectAsync(n1, passWriteBarrier: false, message => message.TargetId == n0.Id
            ? MessageAction.Drop
            : MessageAction.Deliver);
        Equal(2L, n1.Term);

        var noOpIndex = lastIndex + 1L;
        await cluster.ReplicateOnlyToAsync(n1, n2, noOpIndex);

        // {1,2} is a quorum of {0,1,2} but not of {0..4}
        True(n1.Log.LastCommittedEntryIndex < noOpIndex, $"node 1 committed {n1.Log.LastCommittedEntryIndex} with {{1,2}}");
        True(n2.Log.LastCommittedEntryIndex < noOpIndex);
        Equal(5, n1.Members.Count);
    }

    private static HashSet<EndPoint> EndPoints(MembershipNode node)
        => node.Members.Select(static member => member.EndPoint).ToHashSet();

    private static async Task<(MembershipNode Follower, MembershipNode Sender, MembershipNode Removed)> ReplicateUncommittedRemovalAsync(
        MembershipClusterFixture cluster)
    {
        var (n0, n1, n2, n4) = (cluster.Nodes[0], cluster.Nodes[1], cluster.Nodes[2], cluster.Nodes[4]);
        await cluster.ElectAsync(n0);
        await cluster.PumpAsync(n0, n0.ForceReplicationAsync(TestToken).AsTask());
        await n0.DetectAsync(n4.EndPoint);
        await cluster.ReplicateOnlyToAsync(n0, n1, 2L);

        DoesNotContain(n4.EndPoint, EndPoints(n1));
        return (n1, n2, n4);
    }

    private static async Task WaitUntilAsync(Func<bool> condition)
    {
        while (!condition())
            await Task.Delay(1, TestToken);
    }

    private static Task WaitForFollowerAsync(MembershipNode node)
        => WaitUntilAsync(() => Accessors<InProcessClusterMember>.State(node) is FollowerState<InProcessClusterMember>);

    private static class Accessors<TMember>
        where TMember : class, IRaftClusterMember, IDisposable
    {
        [UnsafeAccessor(UnsafeAccessorKind.Field, Name = "state")]
        internal static extern ref RaftState<TMember> State(RaftCluster<TMember> cluster);
    }

    private class DurableEntry : IRaftLogEntry
    {
        public long Term { get; init; }

        public virtual bool IsSnapshot { get; init; }

        public bool IsReusable => false;

        public virtual long? Length => 3L;

        async ValueTask IDataTransferObject.WriteToAsync<TWriter>(TWriter writer, CancellationToken token)
        {
            await writer.WriteAsync(new byte[] { 1, 2, 3 }, token: token);
        }
    }

    private sealed class PartiallyFailingEntry(Action beforeFailure, Func<Exception> failureFactory) : DurableEntry, IRaftLogEntry
    {
        async ValueTask IDataTransferObject.WriteToAsync<TWriter>(TWriter writer, CancellationToken token)
        {
            await writer.WriteAsync(new byte[] { 1, 2 }, token: token);
            beforeFailure();
            throw failureFactory();
        }
    }
}
