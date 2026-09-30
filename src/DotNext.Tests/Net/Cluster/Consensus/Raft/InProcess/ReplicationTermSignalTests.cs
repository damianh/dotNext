using System.Net;

namespace DotNext.Net.Cluster.Consensus.Raft.InProcess;

using IO.Log;
using NetworkTransport;

public sealed class ReplicationTermSignalTests : RaftTest
{
    [Theory(Timeout = TestTimeouts.Default)]
    [InlineData(3L, 1L)] // same sender term, only an older-term entry
    [InlineData(4L, 3L)] // later leader and term, only older-term entries
    public static async Task FlagFromPreviousRequestIsNotCarriedOver(long secondSenderTerm, long secondEntryTerm)
    {
        using var followerState = new ConsensusOnlyState();
        using var senderState = new ConsensusOnlyState();
        var (follower, sender) = await CreateNodesAsync(followerState, senderState);
        await using (follower)
        await using (sender)
        {
            var first = await AppendAsync(follower, sender, 3L, 1L, 3L);
            Equal(HeartbeatResult.ReplicatedWithLeaderTerm, first.Result);

            var second = await AppendAsync(follower, sender, secondSenderTerm, 1L, secondEntryTerm);
            Equal(HeartbeatResult.Replicated, second.Result);
            Equal(2L, second.LastIndex);

            var third = await AppendAsync(follower, sender, secondSenderTerm, 1L, secondSenderTerm);
            Equal(HeartbeatResult.ReplicatedWithLeaderTerm, third.Result);
        }
    }

    private static async Task<ReplicationStatus> AppendAsync(InProcessCluster follower, InProcessCluster sender, long senderTerm, params long[] entryTerms)
    {
        await using var producer = new LogEntryProducer<EmptyLogEntry>(
            Array.ConvertAll(entryTerms, static term => new EmptyLogEntry { Term = term }));
        var result = await ((ILocalMember)follower).AppendEntriesAsync(
            sender.Id, senderTerm, producer, 0L, 0L, 0L, ((ILocalMember)follower).Version, TestToken);
        return result.Value;
    }

    private static async Task<(InProcessCluster, InProcessCluster)> CreateNodesAsync(IPersistentState followerState, IPersistentState senderState)
    {
        var timeProvider = new ManualTimeProvider();
        var network = new InProcessNetwork();
        EndPoint[] membership = [new DnsEndPoint("node-a", 0), new DnsEndPoint("node-b", 0)];
        var follower = CreateNode(network, timeProvider, membership, 0, followerState);
        var sender = CreateNode(network, timeProvider, membership, 1, senderState);
        await follower.StartAsync(TestToken);
        await sender.StartAsync(TestToken);
        return (follower, sender);
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
}
