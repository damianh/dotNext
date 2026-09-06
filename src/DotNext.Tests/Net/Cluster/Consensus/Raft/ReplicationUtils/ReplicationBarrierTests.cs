namespace DotNext.Net.Cluster.Consensus.Raft.ReplicationUtils;

public class ReplicationBarrierTests : Test
{
    [Theory]
    [InlineData(2)]
    [InlineData(4)]
    public static async Task HalfUnavailableCompletesWithoutConsensus(int memberCount)
    {
        var barrier = new ReplicationBarrier();
        var result = barrier.WaitAsync(memberCount, checkpointIndex: 10L);

        // The successful half includes the leader's local contribution.
        for (var i = 0; i < memberCount / 2; i++)
            True(barrier.SetResult(MemberResult.Replicated(10L)));
        for (var i = 0; i < memberCount / 2; i++)
            True(barrier.SetResult(MemberResult.Unavailable));

        True(barrier.IsCompleted, $"All {memberCount} responses arrived, but the half-unavailable barrier is still pending.");
        True(result.IsCompleted);
        Equal(new(memberCount, false), await result);
        barrier.Reuse();
    }

    [Theory]
    [InlineData("R", 1, true)]
    [InlineData("U", 1, false)]
    [InlineData("RR", 2, true)]
    [InlineData("TT", 2, true)]
    [InlineData("TU", 2, false)]
    [InlineData("UT", 1, false)]
    [InlineData("RRU", 2, true)]
    [InlineData("TRU", 3, true)]
    [InlineData("TTT", 2, true)]
    [InlineData("RUU", 3, false)]
    [InlineData("UUR", 2, false)]
    [InlineData("RRRU", 3, true)]
    [InlineData("RTTU", 3, true)]
    [InlineData("RTUU", 4, false)]
    [InlineData("UURT", 2, false)]
    [InlineData("URUT", 3, false)]
    [InlineData("RRRUU", 3, true)]
    [InlineData("RTUUR", 5, true)]
    [InlineData("RRUUU", 5, false)]
    [InlineData("UUURR", 3, false)]
    [InlineData("RRRRUUU", 4, true)]
    [InlineData("RRRUUUU", 7, false)]
    [InlineData("UUUURRR", 4, false)]
    [InlineData("RRRRRRUUUU", 6, true)]
    [InlineData("RRRRRUUUUU", 10, false)]
    [InlineData("UUUUURRRRR", 5, false)]
    public static async Task ResponseOrderPreservesQuorumBoundary(
        string responses, int completionCount, bool hasConsensus)
    {
        var barrier = new ReplicationBarrier();
        var result = barrier.WaitAsync(responses.Length, checkpointIndex: 10L);

        for (var i = 0; i < responses.Length; i++)
        {
            MemberResult? response = responses[i] switch
            {
                'R' => MemberResult.Replicated(10L),
                'T' => MemberResult.Touched,
                'U' => MemberResult.Unavailable,
                _ => throw new ArgumentOutOfRangeException(nameof(responses)),
            };
            Equal(i < completionCount, barrier.SetResult(response));
            Equal(i + 1 >= completionCount, barrier.IsCompleted);
            Equal(i + 1 >= completionCount, result.IsCompleted);
        }

        Equal(new(completionCount, hasConsensus), await result);
        barrier.Reuse();
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public static async Task CancellationAndHigherTermCompleteImmediately(bool canceled)
    {
        var barrier = new ReplicationBarrier();
        var result = barrier.WaitAsync(4, checkpointIndex: 10L);
        True(barrier.SetResult(MemberResult.Replicated(10L)));
        var terminal = canceled ? MemberResult.Canceled : MemberResult.HigherTermDetected(20L);
        True(barrier.SetResult(terminal));
        True(barrier.IsCompleted);
        True(result.IsCompleted);
        Equal(new(2, false), await result);
        Equal(terminal, barrier[1]);

        False(barrier.SetResult(MemberResult.Touched));
        False(barrier.SetResult(MemberResult.Unavailable));
        barrier.Reuse();
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public static async Task EarlyQuorumLossWaitsForLateRepliesBeforeReuse(bool consumeBeforeLateReplies)
    {
        var barrier = new TrackingReplicationBarrier();
        var result = barrier.WaitAsync(4, checkpointIndex: 10L);
        True(barrier.SetResult(MemberResult.Unavailable));
        False(result.IsCompleted);
        True(barrier.SetResult(MemberResult.Unavailable));
        True(result.IsCompleted);
        Equal(new(2, false), await result);

        if (consumeBeforeLateReplies)
            barrier.Reuse();
        Equal(0, barrier.ReuseCount);
        False(barrier.SetResult(MemberResult.Touched));
        Equal(0, barrier.ReuseCount);
        False(barrier.SetResult(MemberResult.Replicated(10L)));

        if (!consumeBeforeLateReplies)
        {
            Equal(0, barrier.ReuseCount);
            barrier.Reuse();
        }

        Equal(1, barrier.ReuseCount);
        False(barrier.IsCompleted);

        result = barrier.WaitAsync(2, checkpointIndex: 20L);
        Equal(20L, barrier.Checkpoint);
        False(result.IsCompleted);
        True(barrier.SetResult(MemberResult.Replicated(20L)));
        False(result.IsCompleted);
        True(barrier.SetResult(MemberResult.Replicated(20L)));
        True(result.IsCompleted);
        Equal(new(2, true), await result);
        Equal(20L, barrier[0].ReplicatedIndex);
        Equal(20L, barrier[1].ReplicatedIndex);
        barrier.Reuse();
        Equal(2, barrier.ReuseCount);
    }

    [Fact]
    public static async Task CheckMixedResponses()
    {
        var barrier = new ReplicationBarrier();
        var task = barrier.WaitAsync(7, 0L).AsTask();

        True(barrier.SetResult(MemberResult.Replicated(10)));
        True(barrier.SetResult(MemberResult.Replicated(10)));
        True(barrier.SetResult(MemberResult.Touched));
        True(barrier.SetResult(MemberResult.Touched));
        False(barrier.IsCompleted);

        True(barrier.SetResult(MemberResult.Replicated(10)));
        True(barrier.SetResult(MemberResult.Touched));
        True(barrier.SetResult(MemberResult.Touched));
        True(barrier.IsCompleted);

        Equal(new(7, true), await task.WaitAsync(TestToken));
    }

    [Fact]
    public static async Task CheckReplicatedMajority()
    {
        var barrier = new ReplicationBarrier();
        var task = barrier.WaitAsync(7, 0L).AsTask();

        True(barrier.SetResult(MemberResult.Touched));
        True(barrier.SetResult(MemberResult.Touched));
        True(barrier.SetResult(MemberResult.Touched));
        True(barrier.SetResult(MemberResult.Touched));
        True(barrier.IsCompleted);

        False(barrier.SetResult(MemberResult.Touched));

        Equal(new(4, true), await task.WaitAsync(TestToken));
    }

    [Fact]
    public static async Task CheckNoResponseMajority()
    {
        var barrier = new ReplicationBarrier();
        var task = barrier.WaitAsync(7, 0L).AsTask();

        True(barrier.SetResult(MemberResult.Unavailable));
        True(barrier.SetResult(MemberResult.Unavailable));
        True(barrier.SetResult(MemberResult.Unavailable));
        True(barrier.SetResult(MemberResult.Unavailable));
        False(barrier.SetResult(MemberResult.Unavailable));
        True(barrier.IsCompleted);

        Equal(new(4, false), await task.WaitAsync(TestToken));
    }

    [Theory]
    [InlineData(10)]
    [InlineData(100)]
    public static async Task Overflow(int expectedCount)
    {
        var barrier = new ReplicationBarrier();
        var task = barrier.WaitAsync(expectedCount, 0L).AsTask();

        for (var i = 0; i < expectedCount; i++)
        {
            barrier.SetResult(MemberResult.Touched);
        }

        var (quorum, hasConsensus) = await task.WaitAsync(TestToken);
        Equal(expectedCount / 2 + 1, quorum);
        True(hasConsensus);
    }

    [Fact]
    public static async Task ConsensusFor3()
    {
        var barrier = new ReplicationBarrier();
        var task = barrier.WaitAsync(memberCount: 3, 0L).AsTask();
        True(barrier.SetResult(MemberResult.Unavailable));
        True(barrier.SetResult(MemberResult.Touched));
        True(barrier.SetResult(MemberResult.Replicated(2L)));

        var result = await task.WaitAsync(TestToken);
        True(result.HasConsensus);
    }

    [Fact]
    public static async Task NoConsensusFor3()
    {
        var barrier = new ReplicationBarrier();
        var task = barrier.WaitAsync(memberCount: 3, 0L).AsTask();
        True(barrier.SetResult(MemberResult.Unavailable));
        True(barrier.SetResult(MemberResult.Replicated(2L)));
        True(barrier.SetResult(MemberResult.Unavailable));

        var result = await task.WaitAsync(TestToken);
        False(result.HasConsensus);
    }

    [Fact]
    public static async Task ConsensusFor2()
    {
        var barrier = new ReplicationBarrier();
        var task = barrier.WaitAsync(memberCount: 2, 0L).AsTask();
        True(barrier.SetResult(MemberResult.Touched));
        True(barrier.SetResult(MemberResult.Replicated(2L)));

        var result = await task.WaitAsync(TestToken);
        True(result.HasConsensus);
    }

    private sealed class TrackingReplicationBarrier : ReplicationBarrier
    {
        internal int ReuseCount { get; private set; }

        protected override void ReuseCore() => ReuseCount++;
    }
}