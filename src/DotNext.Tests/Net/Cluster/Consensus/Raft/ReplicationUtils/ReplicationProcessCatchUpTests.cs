using System.Net;
using Microsoft.Extensions.Logging.Abstractions;

namespace DotNext.Net.Cluster.Consensus.Raft.ReplicationUtils;

using IO;

/// <summary>
/// Regressions for #54: membership warm-up must accept a member whose log already matches the leader's
/// committed prefix, even though the acknowledgment carries no entry of the leader's term, and must
/// keep refusing a member that has not acknowledged that prefix.
/// </summary>
public sealed class ReplicationProcessCatchUpTests : Test
{
    private const long LeaderTerm = 3L;
    private const long Watermark = 2L;
    private const int Rounds = 3;

    [Fact(Timeout = TestTimeouts.Default)]
    public static async Task MatchingPrefixAcknowledgedByEmptyHeartbeatIsCaughtUp()
    {
        // an up-to-date follower answers an empty heartbeat with Replicated: no entry of the leader's term arrived
        var member = new ScriptedMember
        {
            OnAppend = static (prevLogIndex, count) => Replicated(HeartbeatResult.Replicated, prevLogIndex + count),
        };

        True(await CatchUpAsync(member));

        var append = Single(member.Appends);
        Equal((Watermark, 0), append);
        Empty(member.Snapshots);
    }

    [Fact(Timeout = TestTimeouts.Default)]
    public static async Task ReplicationWithLeaderTermIsCaughtUp()
    {
        var member = new ScriptedMember
        {
            OnAppend = static (prevLogIndex, count) => Replicated(HeartbeatResult.ReplicatedWithLeaderTerm, prevLogIndex + count),
        };

        True(await CatchUpAsync(member));
        Single(member.Appends);
    }

    [Theory(Timeout = TestTimeouts.Default)]
    [InlineData(HeartbeatResult.Replicated)]
    [InlineData(HeartbeatResult.ReplicatedWithLeaderTerm)]
    public static async Task LaggingMemberIsCaughtUpBySnapshot(HeartbeatResult snapshotResult)
    {
        // the follower has nothing, so the leader backs off to the committed prefix, which is a snapshot
        var member = new ScriptedMember
        {
            OnAppend = static (prevLogIndex, count) => prevLogIndex is 0L
                ? Replicated(HeartbeatResult.Replicated, count)
                : Rejected(lastIndex: 0L),
            OnSnapshot = _ => new() { Term = LeaderTerm, Value = snapshotResult },
        };

        True(await CatchUpAsync(member));

        Equal((Watermark, 0), Single(member.Appends));
        Equal(Watermark, Single(member.Snapshots));
    }

    [Fact(Timeout = TestTimeouts.Default)]
    public static async Task RejectingMemberIsNotCaughtUp()
    {
        var member = new ScriptedMember
        {
            OnAppend = static (_, _) => Rejected(lastIndex: 0L),
            OnSnapshot = static _ => new() { Term = LeaderTerm, Value = HeartbeatResult.Rejected },
        };

        False(await CatchUpAsync(member));
        Equal(Rounds, member.Appends.Count + member.Snapshots.Count);
    }

    [Fact(Timeout = TestTimeouts.Default)]
    public static async Task UnsupportedVersionIsNotCaughtUp()
    {
        var member = new ScriptedMember
        {
            OnAppend = static (_, _) => new()
            {
                Term = LeaderTerm,
                Value = new() { Result = HeartbeatResult.UnsupportedVersion, LastIndex = Watermark },
            },
        };

        False(await CatchUpAsync(member));
        Equal(Rounds, member.Appends.Count);
    }

    [Fact(Timeout = TestTimeouts.Default)]
    public static async Task HigherTermStopsWarmUp()
    {
        var member = new ScriptedMember
        {
            OnAppend = static (_, _) => new()
            {
                Term = LeaderTerm + 1L,
                Value = new() { Result = HeartbeatResult.Rejected, LastIndex = Watermark },
            },
        };

        False(await CatchUpAsync(member));
        Single(member.Appends);
    }

    [Fact(Timeout = TestTimeouts.Default)]
    public static async Task UnavailableMemberIsNotCaughtUp()
    {
        var member = new ScriptedMember
        {
            OnAppend = static (_, _) => throw new MemberUnavailableException(null),
        };

        False(await CatchUpAsync(member));
    }

    [Fact(Timeout = TestTimeouts.Default)]
    public static async Task CanceledWarmUpThrows()
    {
        var member = new ScriptedMember
        {
            OnAppend = static (prevLogIndex, count) => Replicated(HeartbeatResult.Replicated, prevLogIndex + count),
        };

        await ThrowsAnyAsync<OperationCanceledException>(() => CatchUpAsync(member, new(canceled: true)));
    }

    private static async Task<bool> CatchUpAsync(ScriptedMember member, CancellationToken token = default)
    {
        IPersistentState log = new ConsensusOnlyState();
        await log.AppendAsync(new EmptyLogEntry { Term = 1L }, TestToken);
        await log.AppendAsync(new EmptyLogEntry { Term = 2L }, TestToken);
        await log.CommitAsync(Watermark, TestToken);
        await log.UpdateTermAsync(LeaderTerm, false, TestToken);
        Equal(Watermark, log.LastEntryIndex);

        // as AddMemberAsync does: assume that the member is up-to-date with the leader
        ((IRaftClusterMember)member).State.Initialize(log);

        using var process = new ReplicationProcess<ScriptedMember>(member, queueSize: 1)
        {
            AuditTrail = log,
            Term = LeaderTerm,
            Logger = NullLogger.Instance,
        };

        return await process.CatchUpAsync(Rounds, token.CanBeCanceled ? token : TestToken)
            .AsTask()
            .WaitAsync(DefaultTimeout, TestToken);
    }

    private static Result<ReplicationStatus> Replicated(HeartbeatResult result, long lastIndex) => new()
    {
        Term = LeaderTerm,
        Value = new() { Result = result, LastIndex = lastIndex },
    };

    private static Result<ReplicationStatus> Rejected(long lastIndex) => new()
    {
        Term = LeaderTerm,
        Value = new() { Result = HeartbeatResult.Rejected, LastIndex = lastIndex },
    };

    private sealed class ScriptedMember : IRaftClusterMember
    {
        private IRaftClusterMember.ReplicationState state;

        internal Func<long, int, Result<ReplicationStatus>> OnAppend { get; init; }

        internal Func<long, Result<HeartbeatResult>> OnSnapshot { get; init; }

        internal List<(long PrevLogIndex, int Count)> Appends { get; } = [];

        internal List<long> Snapshots { get; } = [];

        public EndPoint EndPoint { get; } = new DnsEndPoint("joiner", 0);
        public bool IsLeader => false;
        public bool IsRemote => true;
        public ClusterMemberStatus Status => ClusterMemberStatus.Available;
        ref IRaftClusterMember.ReplicationState IRaftClusterMember.State => ref state;
        public event Action<ClusterMemberStatusChangedEventArgs> MemberStatusChanged { add { } remove { } }

        public Task<Result<bool>> VoteAsync(long term, long lastLogIndex, long lastLogTerm, CancellationToken token)
            => throw new NotSupportedException();

        public Task<Result<ReplicationStatus>> AppendEntriesAsync<TEntry, TList>(long term, TList entries, long prevLogIndex, long prevLogTerm, long commitIndex, CancellationToken token)
            where TEntry : IRaftLogEntry
            where TList : IReadOnlyList<TEntry>
        {
            Equal(LeaderTerm, term);
            Appends.Add((prevLogIndex, entries.Count));
            return Task.FromResult(OnAppend(prevLogIndex, entries.Count));
        }

        public Task<Result<HeartbeatResult>> InstallSnapshotAsync(long term, IRaftLogEntry snapshot, long snapshotIndex, IDataTransferObject configuration, long configurationVersion, CancellationToken token)
        {
            NotNull(OnSnapshot);
            Snapshots.Add(snapshotIndex);
            return Task.FromResult(OnSnapshot(snapshotIndex));
        }

        public Task<long?> SynchronizeAsync(long commitIndex, CancellationToken token) => throw new NotSupportedException();
        public ValueTask<IReadOnlyDictionary<string, string>> GetMetadataAsync(bool refresh, CancellationToken token) => throw new NotSupportedException();
        public Task<bool> ResignAsync(CancellationToken token) => throw new NotSupportedException();
        public ValueTask CancelPendingRequestsAsync() => ValueTask.CompletedTask;
    }
}
