using System.Net;

namespace DotNext.Net.Cluster.Consensus.Raft.InProcess;

public sealed class VoteStickinessTests : RaftTest
{
    [Theory]
    [InlineData(false, 0L)]
    [InlineData(true, 0L)]
    [InlineData(false, 1L)]
    [InlineData(true, 1L)]
    public static async Task NoLeaderActivityDoesNotSuppressVoting(bool preVote, long initialTicks)
    {
        var clock = CreateClock(initialTicks);
        var network = new InProcessNetwork();
        EndPoint[] membership = [new DnsEndPoint("candidate", 0), new DnsEndPoint("voter", 0)];
        using var candidateState = new ConsensusOnlyState();
        using var voterState = new ConsensusOnlyState();
        await using var candidate = CreateNode(network, clock, membership, 0, candidateState);
        await using var voter = CreateNode(network, clock, membership, 1, voterState);
        await candidate.StartAsync(TestToken);
        await voter.StartAsync(TestToken);

        Null(voter.Leader);
        Equal(initialTicks, clock.GetTimestamp());
        await AssertVoteAsync(candidate, voter, preVote, accepted: true);
    }

    [Theory]
    [InlineData(false, 0L)]
    [InlineData(true, 0L)]
    [InlineData(false, 1L)]
    [InlineData(true, 1L)]
    public static async Task ActualLeaderActivitySuppressesVotingUntilExpiry(bool preVote, long initialTicks)
    {
        var clock = CreateClock(initialTicks);
        var network = new InProcessNetwork();
        EndPoint[] membership =
        [
            new DnsEndPoint("candidate", 0),
            new DnsEndPoint("voter", 0),
            new DnsEndPoint("leader", 0),
        ];
        using var candidateState = new ConsensusOnlyState();
        using var voterState = new ConsensusOnlyState();
        using var leaderState = new ConsensusOnlyState();
        await using var candidate = CreateNode(network, clock, membership, 0, candidateState);
        await using var voter = CreateNode(network, clock, membership, 1, voterState);
        await using var leader = CreateNode(network, clock, membership, 2, leaderState);
        await candidate.StartAsync(TestToken);
        await voter.StartAsync(TestToken);
        await leader.StartAsync(TestToken);

        var heartbeat = await leader.GetMember(voter.EndPoint).As<IRaftClusterMember>()
            .AppendEntriesAsync<EmptyLogEntry, EmptyLogEntry[]>(0L, [], 0L, 0L, 0L, TestToken);
        True(heartbeat.Value.Result is HeartbeatResult.Replicated or HeartbeatResult.ReplicatedWithLeaderTerm);
        NotNull(voter.Leader);
        Equal(leader.EndPoint, voter.Leader.EndPoint);
        Equal(initialTicks, clock.GetTimestamp());
        await AssertVoteAsync(candidate, voter, preVote, accepted: false);

        clock.Advance(TimeSpan.FromMilliseconds(99));
        await AssertVoteAsync(candidate, voter, preVote, accepted: false);

        // Activity captured at timestamp zero is represented by timestamp one.
        clock.Advance(TimeSpan.FromMilliseconds(2));
        await AssertVoteAsync(candidate, voter, preVote, accepted: true);
    }

    private static ManualTimeProvider CreateClock(long initialTicks)
    {
        var clock = new ManualTimeProvider();
        clock.AdjustTime(DateTimeOffset.MinValue.AddTicks(initialTicks));
        Equal(initialTicks, clock.GetTimestamp());
        return clock;
    }

    private static InProcessCluster CreateNode(
        InProcessNetwork network,
        TimeProvider clock,
        IReadOnlyList<EndPoint> membership,
        int index,
        IPersistentState state)
        => new(
            network,
            ((DnsEndPoint)membership[index]).Host,
            membership,
            state,
            clock,
            TimeSpan.FromMilliseconds(100),
            startFollower: false);

    private static async Task AssertVoteAsync(
        InProcessCluster candidate, InProcessCluster voter, bool preVote, bool accepted)
    {
        var member = candidate.GetMember(voter.EndPoint).As<IRaftClusterMember>();
        if (preVote)
        {
            // The in-process transport converts current term zero to next term one.
            var response = await member.PreVoteAsync(0L, 0L, 0L, TestToken);
            Equal(accepted ? PreVoteResult.Accepted : PreVoteResult.RejectedByFollower, response.Value);
        }
        else
        {
            var response = await member.VoteAsync(1L, 0L, 0L, TestToken);
            Equal(accepted, response.Value);
        }
    }
}
