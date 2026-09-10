using System.Diagnostics.CodeAnalysis;
using System.Net;

namespace DotNext.Net.Cluster.Consensus.Raft;

using IO;
using IO.Log;
using StateMachine;

[Collection(TestCollections.Raft)]
public sealed class RaftLogFreshnessTests : RaftTest
{
    public static TheoryData<long, long, long, long, bool> FreshnessCases => new()
    {
        { 2L, 100L, 1L, 11L, false },
        { 2L, 100L, 1L, 100L, false },
        { 2L, 100L, 1L, 101L, false },
        { 2L, 100L, 2L, 11L, false },
        { 2L, 100L, 2L, 100L, true },
        { 2L, 100L, 2L, 101L, true },
        { 2L, 100L, 3L, 11L, true },
        { 2L, 100L, 3L, 100L, true },
        { 2L, 100L, 3L, 101L, true },
        { 1L, 100L, 2L, 11L, true },
        { 0L, 0L, 0L, 0L, true },
        { 0L, 0L, 1L, 1L, true },
        { 1L, 1L, 0L, 0L, false },
    };

    [Theory]
    [MemberData(nameof(FreshnessCases))]
    public static async Task CompareLogs(long localTerm, long localIndex, long candidateTerm, long candidateIndex, bool accepted)
    {
        await using var local = CreateLog();
        await using var candidate = CreateLog();
        await SeedLogAsync(local, localTerm, localIndex);
        await SeedLogAsync(candidate, candidateTerm, candidateIndex);

        Equal(accepted, await local.IsUpToDateAsync(candidate.LastEntryIndex,
            await candidate.GetTermAsync(candidate.LastEntryIndex, TestToken), TestToken));
    }

    [Theory]
    [MemberData(nameof(FreshnessCases))]
    public static async Task PreVoteUsesLogFreshness(long localTerm, long localIndex, long candidateTerm, long candidateIndex, bool accepted)
    {
        await using var fixture = new ElectionFixture();
        await fixture.InitializeAsync(localTerm, localIndex, candidateTerm, candidateIndex);
        var voter = fixture.Voter;
        var candidate = fixture.Candidate;
        var term = voter.AuditTrail.Term;

        // Equal local metadata proves the membership, term, version and stickiness guards allow this request.
        Equal(PreVoteResult.Accepted, (await voter.ReceivePreVoteAsync(candidate.Id, term + 1L,
            localIndex, localTerm, candidate.AuditTrail.Version, TestToken)).Value);

        var result = await fixture.CandidateToVoter.PreVoteAsync(term, candidateIndex,
            await candidate.AuditTrail.GetTermAsync(candidateIndex, TestToken), TestToken);
        Equal(accepted ? PreVoteResult.Accepted : PreVoteResult.RejectedByFollower, result.Value);
        Equal(term, voter.AuditTrail.Term);
        True(voter.AuditTrail.IsVotedFor(default));
    }

    [Theory]
    [MemberData(nameof(FreshnessCases))]
    public static async Task VoteUsesLogFreshness(long localTerm, long localIndex, long candidateTerm, long candidateIndex, bool accepted)
    {
        // Keep the positive control independent: granting its vote must not affect the regression.
        await using (var control = new ElectionFixture())
        {
            await control.InitializeAsync(localTerm, localIndex, candidateTerm, candidateIndex);
            True((await control.Voter.ReceiveVoteAsync(control.Candidate.Id, control.Voter.AuditTrail.Term,
                localIndex, localTerm, control.Candidate.AuditTrail.Version, TestToken)).Value);
        }

        await using var fixture = new ElectionFixture();
        await fixture.InitializeAsync(localTerm, localIndex, candidateTerm, candidateIndex);
        var voter = fixture.Voter;
        var term = voter.AuditTrail.Term;
        var result = await fixture.CandidateToVoter.VoteAsync(term, candidateIndex,
            await fixture.Candidate.AuditTrail.GetTermAsync(candidateIndex, TestToken), TestToken);

        Equal(accepted, result.Value);
        Equal(term, voter.AuditTrail.Term);
        True(voter.AuditTrail.IsVotedFor(fixture.Candidate.Id));
        Equal(!accepted, voter.AuditTrail.IsVotedFor(default));
    }

    [Fact]
    public static async Task ShorterNewerLogCanWinElection()
    {
        await using var fixture = new ElectionFixture();
        await fixture.InitializeAsync(1L, 100L, 2L, 11L, currentTerm: 2L);
        await fixture.Candidate.AuditTrail.UpdateVotedForAsync(fixture.Candidate.Id, TestToken);
        await fixture.Other.AuditTrail.UpdateVotedForAsync(fixture.Candidate.Id, TestToken);
        fixture.Other.Available = false;
        var elected = new TaskCompletionSource<Member>(TaskCreationOptions.RunContinuationsAsynchronously);
        fixture.Candidate.LeaderChanged += (_, leader) =>
        {
            if (leader is not null)
                elected.TrySetResult(leader);
        };

        Equal(PreVoteResult.Accepted, (await fixture.Voter.ReceivePreVoteAsync(fixture.Candidate.Id,
            3L, 100L, 1L, fixture.Candidate.AuditTrail.Version, TestToken)).Value);

        // The old leader's term-1 suffix never reached a majority. The other two nodes
        // could elect a term-2 leader with the shared prefix, then append term 2 at index 11.
        // Now the third node is unavailable: the old leader's vote is needed for a majority.
        fixture.Candidate.BeginElection();

        Equal(PreVoteResult.Accepted,
            (await fixture.CandidateToVoter.PreVoteReceived.Task.WaitAsync(DefaultTimeout, TestToken)).Value);
        True((await fixture.CandidateToVoter.VoteReceived.Task.WaitAsync(DefaultTimeout, TestToken)).Value);
        Equal(fixture.Candidate.Id, (await elected.Task.WaitAsync(DefaultTimeout, TestToken)).Id);
        Equal(3L, fixture.Candidate.AuditTrail.Term);
        Equal(3L, fixture.Voter.AuditTrail.Term);
        True(fixture.Voter.AuditTrail.IsVotedFor(fixture.Candidate.Id));
    }

    [Theory]
    [InlineData("term")]
    [InlineData("membership")]
    [InlineData("version")]
    [InlineData("prior-vote")]
    [InlineData("stickiness")]
    public static async Task FreshLogDoesNotBypassEligibility(string rejection)
    {
        await using var fixture = new ElectionFixture();
        await fixture.InitializeAsync(1L, 100L, 2L, 11L);
        var voter = fixture.Voter;
        var candidate = fixture.Candidate;
        var sender = rejection is "membership" ? ClusterMemberId.FromEndPoint(new IPEndPoint(IPAddress.Loopback, 4999)) : candidate.Id;
        var term = voter.AuditTrail.Term - (rejection is "term" ? 1L : 0L);
        var version = candidate.AuditTrail.Version + (rejection is "version" ? 1 : 0);

        if (rejection is "prior-vote")
            await voter.AuditTrail.UpdateVotedForAsync(fixture.Other.Id, TestToken);
        if (rejection is "stickiness")
            voter.RefreshLeaderStickiness();

        var preVote = await voter.ReceivePreVoteAsync(sender, term, 11L, 2L, version, TestToken);
        // Pre-vote does not consult the vote recorded for the current term.
        Equal(rejection is "prior-vote" ? PreVoteResult.Accepted : PreVoteResult.RejectedByFollower, preVote.Value);
        False((await voter.ReceiveVoteAsync(sender, term, 11L, 2L, version, TestToken)).Value);
        Equal(4L, voter.AuditTrail.Term);
        True(voter.AuditTrail.IsVotedFor(rejection is "prior-vote" ? fixture.Other.Id : default));
    }

    private static WriteAheadLog CreateLog()
        => new(new() { Location = GetTempPath() }, IStateMachine.CreateNoOp());

    private static async Task SeedLogAsync(WriteAheadLog log, long lastTerm, long lastIndex, long currentTerm = 4L)
    {
        IPersistentState state = log;
        await state.UpdateTermAsync(currentTerm, resetLastVote: true, TestToken);
        for (var index = 1L; index <= lastIndex; index++)
            await log.AppendAsync(new EmptyLogEntry { Term = index <= 10L ? Math.Min(1L, lastTerm) : lastTerm }, TestToken);

        Equal(lastIndex, log.LastEntryIndex);
        Equal(lastTerm, await log.GetTermAsync(lastIndex, TestToken));
        Equal(0L, log.LastCommittedEntryIndex);
        Equal(currentTerm, state.Term);
        True(state.IsVotedFor(default));
    }

    private sealed class ElectionFixture : IAsyncDisposable
    {
        internal Node Voter { get; } = new(4101);
        internal Node Candidate { get; } = new(4102);
        internal Node Other { get; } = new(4103);
        internal Member CandidateToVoter { get; private set; }

        internal async Task InitializeAsync(long localTerm, long localIndex, long candidateTerm, long candidateIndex, long currentTerm = 4L)
        {
            await SeedLogAsync(Voter.Log, localTerm, localIndex, currentTerm);
            await SeedLogAsync(Candidate.Log, candidateTerm, candidateIndex, currentTerm);
            await SeedLogAsync(Other.Log, candidateTerm, candidateIndex, currentTerm);
            Node[] nodes = [Voter, Candidate, Other];
            foreach (var node in nodes)
            {
                await node.ConfigureAsync(nodes);
                await node.StartAsync(TestToken);
                True(node.Readiness.IsCompletedSuccessfully);
                True(node.AuditTrail.IsVotedFor(Candidate.Id));
            }

            CandidateToVoter = Candidate.GetMember(Voter.Id);
        }

        public async ValueTask DisposeAsync()
        {
            await Candidate.DisposeAsync();
            await Voter.DisposeAsync();
            await Other.DisposeAsync();
            await Candidate.Log.DisposeAsync();
            await Voter.Log.DisposeAsync();
            await Other.Log.DisposeAsync();
        }
    }

    private sealed class Configuration : IClusterMemberConfiguration
    {
        public double HeartbeatThreshold => 0.5D;
        public ElectionTimeout ElectionTimeout => new() { LowerValue = 1000, UpperValue = 1000 };
        public bool Standby => false;
        public bool IsLeaderLeaseEnabled => false;
    }

    private sealed class Node : RaftCluster<Member>
    {
        internal WriteAheadLog Log => (WriteAheadLog)AuditTrail;
        internal IPEndPoint Address { get; }
        internal ClusterMemberId Id => ClusterMemberId.FromEndPoint(Address);
        internal bool Available { get; set; } = true;

        [SetsRequiredMembers]
        internal Node(int port) : base(new Configuration())
        {
            Address = new(IPAddress.Loopback, port);
            AuditTrail = CreateLog();
        }

        internal async Task ConfigureAsync(Node[] nodes)
        {
            await using var scope = await ChangeConfigurationAsync(TestToken);
            foreach (var node in nodes)
                scope.MarkAsAdded(new Member(this, node));
        }

        internal Member GetMember(ClusterMemberId id) => TryGetMember(id);
        internal void BeginElection() => StartFollowing();
        internal void RefreshLeaderStickiness()
            => ((IRaftStateMachine)this).UpdateLeaderStickiness(new Diagnostics.Timestamp());

        internal ValueTask<Result<bool>> ReceiveVoteAsync(ClusterMemberId sender, long term, long index, long logTerm, int version, CancellationToken token)
            => VoteAsync(sender, term, index, logTerm, version, token);

        internal ValueTask<Result<PreVoteResult>> ReceivePreVoteAsync(ClusterMemberId sender, long nextTerm, long index, long logTerm, int version, CancellationToken token)
            => PreVoteAsync(sender, nextTerm, index, logTerm, version, token);

        internal async Task<Result<ReplicationStatus>> ReceiveEntriesAsync<TEntry, TList>(Node sender, long term, TList entries,
            long previousIndex, long previousTerm, long commitIndex, CancellationToken token)
            where TEntry : IRaftLogEntry
            where TList : IReadOnlyList<TEntry>
        {
            await using var producer = new LogEntryProducer<TEntry>(entries);
            return await AppendEntriesAsync(sender.Id, term, producer, previousIndex, previousTerm, commitIndex, sender.AuditTrail.Version, token);
        }
    }

    private sealed class Member(Node sender, Node receiver) : IRaftClusterMember, IDisposable
    {
        private IRaftClusterMember.ReplicationState replicationState;
        internal TaskCompletionSource<Result<PreVoteResult>> PreVoteReceived { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        internal TaskCompletionSource<Result<bool>> VoteReceived { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public ClusterMemberId Id => receiver.Id;
        public EndPoint EndPoint => receiver.Address;
        public bool IsRemote => !ReferenceEquals(sender, receiver);
        public bool IsLeader => sender.Leader?.Id == Id;
        public ClusterMemberStatus Status => receiver.Available ? ClusterMemberStatus.Available : ClusterMemberStatus.Unavailable;
        ref IRaftClusterMember.ReplicationState IRaftClusterMember.State => ref replicationState;
        public event Action<ClusterMemberStatusChangedEventArgs> MemberStatusChanged { add { } remove { } }

        private void EnsureAvailable(CancellationToken token)
        {
            token.ThrowIfCancellationRequested();
            if (!receiver.Available)
                throw new MemberUnavailableException(this);
        }

        public async Task<Result<PreVoteResult>> PreVoteAsync(long term, long lastLogIndex, long lastLogTerm, CancellationToken token)
        {
            EnsureAvailable(token);
            // Match RaftClusterMember's local-member shortcut; remote responses always use the real handler.
            var result = IsRemote
                ? await receiver.ReceivePreVoteAsync(sender.Id, term + 1L, lastLogIndex, lastLogTerm, sender.AuditTrail.Version, token)
                : new Result<PreVoteResult> { Term = term, Value = PreVoteResult.Accepted };
            PreVoteReceived.TrySetResult(result);
            return result;
        }

        public async Task<Result<bool>> VoteAsync(long term, long lastLogIndex, long lastLogTerm, CancellationToken token)
        {
            EnsureAvailable(token);
            var result = IsRemote
                ? await receiver.ReceiveVoteAsync(sender.Id, term, lastLogIndex, lastLogTerm, sender.AuditTrail.Version, token)
                : new Result<bool> { Term = term, Value = true };
            VoteReceived.TrySetResult(result);
            return result;
        }

        public Task<Result<ReplicationStatus>> AppendEntriesAsync<TEntry, TList>(long term, TList entries, long prevLogIndex,
            long prevLogTerm, long commitIndex, CancellationToken token)
            where TEntry : IRaftLogEntry
            where TList : IReadOnlyList<TEntry>
        {
            EnsureAvailable(token);
            return receiver.ReceiveEntriesAsync<TEntry, TList>(sender, term, entries, prevLogIndex, prevLogTerm, commitIndex, token);
        }

        public Task<Result<HeartbeatResult>> InstallSnapshotAsync(long term, IRaftLogEntry snapshot, long snapshotIndex,
            IDataTransferObject configuration, long configurationVersion, CancellationToken token)
            => throw new NotSupportedException();

        public Task<long?> SynchronizeAsync(long commitIndex, CancellationToken token) => throw new NotSupportedException();
        public ValueTask<IReadOnlyDictionary<string, string>> GetMetadataAsync(bool refresh, CancellationToken token) => throw new NotSupportedException();
        public Task<bool> ResignAsync(CancellationToken token) => throw new NotSupportedException();
        public ValueTask CancelPendingRequestsAsync() => ValueTask.CompletedTask;
        public void Dispose() { }
    }
}
