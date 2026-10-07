using System.Net;
using Microsoft.Extensions.Logging.Abstractions;

namespace DotNext.Net.Cluster.Consensus.Raft.ReplicationUtils;

using IO;

/// <summary>
/// Regressions for #125: a heartbeat round with nothing new must not wait for a slow member when a majority
/// already stores the leader's log up to an entry of the leader's term. Group commit acknowledges concurrent
/// proposals together, so the forced round that follows a batch often has nothing new to replicate.
/// </summary>
public sealed class ReplicationProcessHeartbeatTests : Test
{
    private const long LeaderTerm = 3L;
    private const int MemberCount = 3; // the leader, the scripted member and a silent member

    [Fact(Timeout = TestTimeouts.Default)]
    public static async Task EmptyHeartbeatAfterLeaderTermEntryCountsTowardCommitment()
    {
        var member = new ScriptedMember();
        var (log, process) = await StartAsync(member, 1L, LeaderTerm);
        using (process)
        {
            var barrier = new ReplicationBarrier();
            var round = StartRound(barrier, log.LastEntryIndex, process);

            // the third member never answers, so the round completes only if the empty heartbeat counts
            var (quorum, consensus) = await round.AsTask().WaitAsync(DefaultTimeout, TestToken);
            True(consensus);
            Equal(2, quorum);
            Equal((2L, 0), Single(member.Appends));
            Equal(2L, barrier[1].ReplicatedIndex);
            Equal(3L, member.State.NextIndex);
        }
    }

    [Fact(Timeout = TestTimeouts.Default)]
    public static async Task EmptyHeartbeatAfterOlderTermEntryWaitsForCommitMajority()
    {
        var member = new ScriptedMember();
        var (log, process) = await StartAsync(member, 1L, 2L);
        using (process)
        {
            var barrier = new ReplicationBarrier();
            var round = StartRound(barrier, log.LastEntryIndex, process);

            // the member stores no entry of the leader's term, so the round still waits for the third member
            await member.Answered.WaitAsync(DefaultTimeout, TestToken);
            await Task.Delay(50, TestToken);
            False(round.IsCompleted);

            barrier.SetResult(MemberResult.Unavailable);
            var (quorum, consensus) = await round.AsTask().WaitAsync(DefaultTimeout, TestToken);
            True(consensus);
            Equal(3, quorum);
            Equal(0L, barrier[1].ReplicatedIndex);
        }
    }

    private static ValueTask<ReplicationResult> StartRound(ReplicationBarrier barrier, long checkpoint, ReplicationProcess process)
    {
        var round = barrier.WaitAsync(MemberCount, checkpoint);
        new ReplicationProcess().Replicate(barrier); // the leader
        process.Replicate(barrier);
        return round;
    }

    private static async Task<(IPersistentState, ReplicationProcess<ScriptedMember>)> StartAsync(ScriptedMember member, params long[] terms)
    {
        IPersistentState log = new ConsensusOnlyState();
        foreach (var term in terms)
            await log.AppendAsync(new EmptyLogEntry { Term = term }, TestToken);

        await log.UpdateTermAsync(LeaderTerm, false, TestToken);

        // the member is up to date, so the round sends an empty heartbeat
        ((IRaftClusterMember)member).State.Initialize(log);

        var process = new ReplicationProcess<ScriptedMember>(member, queueSize: 1)
        {
            AuditTrail = log,
            Term = LeaderTerm,
            Logger = NullLogger.Instance,
        };

        process.Start(TestToken);
        return (log, process);
    }

    private sealed class ScriptedMember : IRaftClusterMember
    {
        private readonly TaskCompletionSource answered = new(TaskCreationOptions.RunContinuationsAsynchronously);
        private IRaftClusterMember.ReplicationState state;

        internal ref IRaftClusterMember.ReplicationState State => ref state;

        internal Task Answered => answered.Task;

        internal List<(long PrevLogIndex, int Count)> Appends { get; } = [];

        public EndPoint EndPoint { get; } = new DnsEndPoint("follower", 0);
        public bool IsLeader => false;
        public bool IsRemote => true;
        public ClusterMemberStatus Status => ClusterMemberStatus.Available;
        ref IRaftClusterMember.ReplicationState IRaftClusterMember.State => ref state;
        public event Action<ClusterMemberStatusChangedEventArgs> MemberStatusChanged { add { } remove { } }

        public Task<Result<bool>> VoteAsync(long term, long lastLogIndex, long lastLogTerm, CancellationToken token)
            => throw new NotSupportedException();

        // a follower answers an accepted empty heartbeat with Replicated: no entry of the leader's term arrived
        public Task<Result<ReplicationStatus>> AppendEntriesAsync<TEntry, TList>(long term, TList entries, long prevLogIndex, long prevLogTerm, long commitIndex, CancellationToken token)
            where TEntry : IRaftLogEntry
            where TList : IReadOnlyList<TEntry>
        {
            Equal(LeaderTerm, term);
            Appends.Add((prevLogIndex, entries.Count));
            answered.TrySetResult();
            return Task.FromResult<Result<ReplicationStatus>>(new()
            {
                Term = LeaderTerm,
                Value = new() { Result = HeartbeatResult.Replicated, LastIndex = prevLogIndex + entries.Count },
            });
        }

        public Task<Result<HeartbeatResult>> InstallSnapshotAsync(long term, IRaftLogEntry snapshot, long snapshotIndex, IDataTransferObject configuration, long configurationVersion, CancellationToken token)
            => throw new NotSupportedException();

        public Task<long?> SynchronizeAsync(long commitIndex, CancellationToken token) => throw new NotSupportedException();
        public ValueTask<IReadOnlyDictionary<string, string>> GetMetadataAsync(bool refresh, CancellationToken token) => throw new NotSupportedException();
        public Task<bool> ResignAsync(CancellationToken token) => throw new NotSupportedException();
        public ValueTask CancelPendingRequestsAsync() => ValueTask.CompletedTask;
    }
}
