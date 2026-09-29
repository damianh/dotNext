using System.Net;

namespace DotNext.Net.Cluster.Consensus.Raft.InProcess;

using IO;
using IO.Log;
using Membership;
using NetworkTransport;
using static MembershipClusterFixture;

/// <summary>
/// #48: a configuration entry reaches a running leader's log only through the membership API, which serializes
/// changes, waits for the previous configuration to commit and activates the new one on the leader.
/// </summary>
public sealed class ConfigurationAppendBoundaryTests : RaftTest
{
    [Fact(Timeout = TestTimeouts.Default)]
    public static async Task ReplicateRejectsConfiguration()
    {
        await using var cluster = new MembershipClusterFixture();
        await cluster.StartAsync();
        var leader = cluster.Nodes[0];
        await cluster.ElectAsync(leader);
        var lastIndex = leader.Log.LastEntryIndex;

        var replication = leader.ReplicateAsync(new ConfigurationEntry(await BuildRemovalAsync(leader, cluster.Voters[4]), leader.Term), TestToken).AsTask();
        await cluster.PumpAsync(leader, SettleAsync(replication));

        AssertSameMembers(cluster, leader);
        await ThrowsAsync<ArgumentException>(() => replication);
        Equal(lastIndex, leader.Log.LastEntryIndex);
    }

    [Fact(Timeout = TestTimeouts.Default)]
    public static async Task NextChangeIsBuiltFromLatestConfiguration()
    {
        await using var cluster = new MembershipClusterFixture();
        await cluster.StartAsync();
        var leader = cluster.Nodes[0];
        await cluster.ElectAsync(leader);

        var replication = leader.ReplicateAsync(new ConfigurationEntry(await BuildRemovalAsync(leader, cluster.Voters[4]), leader.Term), TestToken).AsTask();
        await cluster.PumpAsync(leader, SettleAsync(replication));
        var expected = (await leader.LoadConfigurationAsync()).Members.ToHashSet();
        expected.Remove(cluster.Voters[3]);

        // the change must not be built over a configuration that the leader did not activate
        var removal = leader.RemoveAsync(cluster.Voters[3], TestToken);
        await cluster.PumpAsync(leader, removal);
        True(await removal);

        Equal(expected, (await leader.LoadConfigurationAsync()).Members.ToHashSet());
    }

    [Fact(Timeout = TestTimeouts.Default)]
    public static async Task AppendToRunningClusterLogIsRejected()
    {
        await using var cluster = new MembershipClusterFixture();
        await cluster.StartAsync();
        var leader = cluster.Nodes[0];
        await cluster.ElectAsync(leader);
        var lastIndex = leader.Log.LastEntryIndex;

        var append = leader.Log.AppendAsync(await BuildRemovalAsync(leader, cluster.Voters[4]), TestToken).AsTask();
        await SettleAsync(append);
        await cluster.ReplicateUntilAppliedAsync(leader, leader.Log.LastEntryIndex);

        AssertSameMembers(cluster, leader);
        await ThrowsAsync<InvalidOperationException>(() => append);
        Equal(lastIndex, leader.Log.LastEntryIndex);
    }

    [Fact(Timeout = TestTimeouts.Default)]
    public static async Task AppendToStoppedClusterLogIsAllowed()
    {
        await using var cluster = new MembershipClusterFixture();
        await cluster.StartAsync();
        var node = cluster.Nodes[0];
        var configuration = await BuildRemovalAsync(node, cluster.Voters[4]);
        var lastIndex = node.Log.LastEntryIndex;

        await node.StopAsync(TestToken);
        Equal(lastIndex + 1L, await node.Log.AppendAsync(configuration, TestToken));
    }

    // Regression guard: a node out of its own configuration still follows the leader, and rejoins
    // when its uncommitted removal is overwritten by a new leader.
    [Fact(Timeout = TestTimeouts.Default)]
    public static async Task RemovedNodeRejoinsWhenRemovalIsTruncated()
    {
        await using var cluster = new MembershipClusterFixture();
        await cluster.StartAsync();
        var (n0, n2, n4) = (cluster.Nodes[0], cluster.Nodes[2], cluster.Nodes[4]);
        await cluster.ElectAsync(n0);
        await cluster.PumpAsync(n0, n0.ForceReplicationAsync(TestToken).AsTask());
        var noOpIndex = n0.Log.LastEntryIndex;
        Equal(noOpIndex, n4.Log.LastEntryIndex);

        // term 1: the removal of node 4 reaches node 4 only, the leader no longer replicates to it
        var configuration = await BuildRemovalAsync(n0, n4.EndPoint);
        await n0.DetectAsync(n4.EndPoint);
        var removalIndex = n0.Log.LastEntryIndex;
        Equal(noOpIndex + 1L, removalIndex);
        True(await AppendEntriesAsync(n0, n4, [new ConfigurationEntry(configuration, n0.Term)], noOpIndex));
        Equal(removalIndex, n4.Log.LastEntryIndex);
        DoesNotContain(n4.EndPoint, EndPoints(n4));
        True(n4.Standby);

        // the removed node still accepts AppendEntries from the leader
        True(await AppendEntriesAsync(n0, n4, [], removalIndex));

        // term 2: node 2 is elected by {1,2,3}, its no-op overwrites the removal on nodes 0 and 4
        await cluster.ElectAsync(n2);
        await cluster.PumpAsync(n2, WaitUntilAsync(() => n0.Log.LastCommittedEntryIndex >= removalIndex
                                                          && n4.Log.LastCommittedEntryIndex >= removalIndex));

        Equal(2L, await n4.Log.GetTermAsync(removalIndex, TestToken));
        False(n4.Standby);
        var voters = cluster.Voters.ToHashSet();
        foreach (var node in cluster.Nodes[..VoterCount])
            Equal(voters, EndPoints(node));
    }

    private static async Task<IClusterConfiguration<EndPoint>> BuildRemovalAsync(MembershipNode node, EndPoint address)
    {
        var configuration = await node.LoadConfigurationAsync();
        True(IClusterConfiguration<EndPoint>.TryRemove(ref configuration, address));
        return configuration;
    }

    private static async Task<bool> AppendEntriesAsync(MembershipNode sender, MembershipNode receiver, IRaftLogEntry[] entries, long prevLogIndex)
    {
        await using var producer = new LogEntryProducer<IRaftLogEntry>(entries);
        var result = await ((ILocalMember)receiver).AppendEntriesAsync(
            sender.Id,
            sender.Term,
            producer,
            prevLogIndex,
            await sender.Log.GetTermAsync(prevLogIndex, TestToken),
            sender.Log.LastCommittedEntryIndex,
            ((ILocalMember)sender).Version,
            TestToken);

        return result.Value.Result is not HeartbeatResult.Rejected and not HeartbeatResult.UnsupportedVersion;
    }

    // every node that holds the leader's log must count over the same members as the leader
    private static void AssertSameMembers(MembershipClusterFixture cluster, MembershipNode leader)
    {
        var expected = EndPoints(leader);
        foreach (var node in cluster.Nodes[1..VoterCount])
        {
            if (node.Log.LastEntryIndex >= leader.Log.LastEntryIndex)
                Equal(expected, EndPoints(node));
        }
    }

    private static HashSet<EndPoint> EndPoints(MembershipNode node)
        => node.Members.Select(static member => member.EndPoint).ToHashSet();

    private static async Task SettleAsync(Task task)
    {
        try
        {
            await task;
        }
        catch
        {
            // the outcome is asserted by the caller
        }
    }

    private static async Task WaitUntilAsync(Func<bool> condition)
    {
        while (!condition())
            await Task.Delay(1, TestToken);
    }

    private sealed class ConfigurationEntry(IClusterConfiguration<EndPoint> configuration, long term) : IRaftLogEntry
    {
        public long Term => term;

        bool IRaftLogEntry.IsConfiguration => true;

        public bool IsReusable => configuration.IsReusable;

        public long? Length => configuration.Length;

        public ValueTask WriteToAsync<TWriter>(TWriter writer, CancellationToken token)
            where TWriter : IAsyncBinaryWriter
            => configuration.WriteToAsync(writer, token);

        public bool TryGetMemory(out ReadOnlyMemory<byte> memory)
            => configuration.TryGetMemory(out memory);
    }
}
