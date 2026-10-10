using Microsoft.Extensions.Logging;

namespace DotNext.Net.Cluster.Consensus.Raft.InProcess;

using IO.Log;
using Membership;

/// <summary>
/// #146: pre-vote and vote rounds are decided as soon as the outcome is known over the full configuration,
/// so a silent member (a request that never completes) cannot hold the election.
/// </summary>
public sealed class MajorityElectionTests : RaftTest
{
    private static readonly TimeSpan ElectionTimeout = TimeSpan.FromMilliseconds(100);

    [Fact(Timeout = TestTimeouts.Default)]
    public static async Task MajorityElectsLeaderWhileMemberIsSilent()
    {
        await using var cluster = new InProcessClusterFixture(3);
        var (n0, n1, n2) = (cluster.Nodes[0], cluster.Nodes[1], cluster.Nodes[2]);
        await cluster.StartAsync();
        cluster.HoldFollowers();
        StartElection(cluster);

        // node 2 never answers: the pre-votes of nodes 0 and 1 are a majority
        await cluster.Network.DeliverAsync(await PendingAsync(cluster, 1, RaftMessageType.PreVote));
        var vote1 = await PendingAsync(cluster, 1, RaftMessageType.Vote);
        var vote2 = await PendingAsync(cluster, 2, RaftMessageType.Vote);
        await cluster.Network.DeliverAsync(vote1);

        Equal(n0.EndPoint, (await n0.WaitForLeaderAsync(DefaultTimeout, TestToken)).EndPoint);
        Equal(1L, n0.Term);

        // the outstanding vote request is canceled with the candidate state
        await vote2.Completion.WaitAsync(DefaultTimeout, TestToken).ConfigureAwait(ConfigureAwaitOptions.SuppressThrowing);
        True(vote2.Completion.IsCanceled);

        // the outstanding pre-vote request is left to finish, without effect
        var preVote2 = Single(cluster.Network.PendingMessages,
            message => message.TargetId == n2.Id && message.MessageType is RaftMessageType.PreVote);
        await cluster.Network.DeliverAsync(preVote2);
        True(preVote2.Completion.IsCompletedSuccessfully);
        Equal(n0.EndPoint, n0.Leader?.EndPoint);
        Equal(1L, n0.Term);
        Equal(1L, n1.Term);
    }

    [Fact(Timeout = TestTimeouts.Default)]
    public static async Task PreVoteStopsWhenMajorityIsImpossible()
    {
        await using var cluster = new InProcessClusterFixture(5);
        var n0 = cluster.Nodes[0];
        var logger = new EventLogger();
        n0.CapturedLogger = logger;
        await cluster.StartAsync();
        cluster.HoldFollowers();
        StartElection(cluster);

        // 3 of 5 rejected: node 4 cannot make a majority
        for (var i = 1; i <= 3; i++)
            cluster.Network.Drop(await PendingAsync(cluster, i, RaftMessageType.PreVote));

        var downgraded = await logger.WaitAsync(0, nameof(DowngradedToFollowerState));
        Equal(0L, downgraded.Term);
        Equal(0L, n0.Term);
        Contains(cluster.Network.PendingMessages,
            message => message.TargetId == cluster.Nodes[4].Id && message.MessageType is RaftMessageType.PreVote);
        DoesNotContain(cluster.Network.PendingMessages, static message => message.MessageType is RaftMessageType.Vote);
    }

    [Fact(Timeout = TestTimeouts.Default)]
    public static async Task PreVoteStopsWhenLeaderRejectsWhileMembersAreSilent()
    {
        var candidateClock = new ManualTimeProvider();
        await using var cluster = new InProcessClusterFixture(
            5,
            clockFactory: (member, clock) => member is 1 ? candidateClock : clock,
            aggressiveLeaderStickiness: true);
        await cluster.StartLeaderAsync();
        var candidate = cluster.Nodes[1];
        var logger = new EventLogger();
        candidate.CapturedLogger = logger;
        foreach (var node in cluster.Nodes.Where(node => !object.ReferenceEquals(node, candidate)))
            cluster.Network.Hold(candidate.EndPoint, node.EndPoint);

        // Only the candidate's clock advances; the leader remains active and rejects its pre-vote.
        candidate.StartElectionTimer();
        candidateClock.Advance(ElectionTimeout);
        var rejection = await cluster.Network.WaitForMessageAsync(
            candidate.EndPoint, cluster.Leader.EndPoint, RaftMessageType.PreVote, TestToken)
            .WaitAsync(DefaultTimeout, TestToken);
        var pending = await cluster.Network.WaitForMessageAsync(
            candidate.EndPoint, cluster.Nodes[2].EndPoint, RaftMessageType.PreVote, TestToken)
            .WaitAsync(DefaultTimeout, TestToken);
        await cluster.Network.DeliverAsync(rejection);
        Equal(PreVoteResult.RejectedByLeader, (await IsType<Task<Result<PreVoteResult>>>(rejection.Completion)).Value);

        Equal(1L, (await logger.WaitAsync(0, nameof(DowngradedToFollowerState))).Term);
        Equal(1L, candidate.Term);
        False(pending.Completion.IsCompleted);
        DoesNotContain(cluster.Network.PendingMessages,
            message => message.SourceId == candidate.Id && message.MessageType is RaftMessageType.Vote);
    }

    [Fact(Timeout = TestTimeouts.Default)]
    public static async Task CandidateStepsDownWhenMajorityIsImpossible()
    {
        await using var cluster = new InProcessClusterFixture(5);
        var n0 = cluster.Nodes[0];
        var logger = new EventLogger();
        n0.CapturedLogger = logger;
        await cluster.StartAsync();
        cluster.HoldFollowers();
        StartElection(cluster);

        // 3 of 5 pre-votes accepted while nodes 3 and 4 are silent
        await cluster.Network.DeliverAsync(await PendingAsync(cluster, 1, RaftMessageType.PreVote));
        await cluster.Network.DeliverAsync(await PendingAsync(cluster, 2, RaftMessageType.PreVote));

        // 3 of 5 votes lost: node 4 cannot make a majority
        var vote4 = await PendingAsync(cluster, 4, RaftMessageType.Vote);
        for (var i = 1; i <= 3; i++)
            cluster.Network.Drop(await PendingAsync(cluster, i, RaftMessageType.Vote));

        var completed = await logger.WaitAsync(0, nameof(VotingCompleted));
        Equal(1L, completed.Term);
        Equal(1L, (await logger.WaitAsync(completed.Index, nameof(DowngradedToFollowerState))).Term);
        Null(n0.Leader);
        Equal(1L, n0.Term);

        await vote4.Completion.WaitAsync(DefaultTimeout, TestToken).ConfigureAwait(ConfigureAwaitOptions.SuppressThrowing);
        True(vote4.Completion.IsCanceled);
    }

    [Fact(Timeout = TestTimeouts.Default)]
    public static async Task FourMemberClusterNeedsThreeVotes()
    {
        await using var cluster = new InProcessClusterFixture(4);
        var n0 = cluster.Nodes[0];
        var logger = new EventLogger();
        n0.CapturedLogger = logger;

        await cluster.StartAsync();
        cluster.HoldFollowers();

        // round 1: 2 of 4 pre-votes accepted is not a majority
        StartElection(cluster);
        await cluster.Network.DeliverAsync(await PendingAsync(cluster, 1, RaftMessageType.PreVote));
        cluster.Network.Drop(await PendingAsync(cluster, 2, RaftMessageType.PreVote));
        cluster.Network.Drop(await PendingAsync(cluster, 3, RaftMessageType.PreVote));
        var downgraded = await logger.WaitAsync(0, nameof(DowngradedToFollowerState));
        Equal(0L, downgraded.Term);
        Equal(0L, n0.Term);

        // round 2: 3 of 4 pre-votes are a majority, 2 of 4 votes are not
        cluster.TimeProvider.Advance(ElectionTimeout);
        await cluster.Network.DeliverAsync(await PendingAsync(cluster, 1, RaftMessageType.PreVote));
        await cluster.Network.DeliverAsync(await PendingAsync(cluster, 2, RaftMessageType.PreVote));
        await cluster.Network.DeliverAsync(await PendingAsync(cluster, 1, RaftMessageType.Vote));
        cluster.Network.Drop(await PendingAsync(cluster, 2, RaftMessageType.Vote));
        cluster.Network.Drop(await PendingAsync(cluster, 3, RaftMessageType.Vote));
        var completed = await logger.WaitAsync(downgraded.Index, nameof(VotingCompleted));
        Equal(1L, completed.Term);
        Equal(1L, (await logger.WaitAsync(completed.Index, nameof(DowngradedToFollowerState))).Term);
        False(logger.Contains(nameof(TransitionToLeaderStateStarted)));
        Null(n0.Leader);

        // round 3: 3 of 4 votes elect the leader while node 3 is silent
        cluster.TimeProvider.Advance(ElectionTimeout);
        await cluster.Network.DeliverAsync(await PendingAsync(cluster, 1, RaftMessageType.PreVote));
        await cluster.Network.DeliverAsync(await PendingAsync(cluster, 2, RaftMessageType.PreVote));
        await cluster.Network.DeliverAsync(await PendingAsync(cluster, 1, RaftMessageType.Vote));
        await cluster.Network.DeliverAsync(await PendingAsync(cluster, 2, RaftMessageType.Vote));
        Equal(n0.EndPoint, (await n0.WaitForLeaderAsync(DefaultTimeout, TestToken)).EndPoint);
        Equal(2L, n0.Term);
        Equal(2L, (await logger.WaitAsync(0, nameof(TransitionToLeaderStateStarted))).Term);
    }

    [Fact(Timeout = TestTimeouts.Default)]
    public static async Task HigherTermResponseAfterDecisionIsHandledByReplication()
    {
        var state = new GatedAppendState();
        await using var cluster = new InProcessClusterFixture(3, index => index is 0 ? state : new ConsensusOnlyState());
        var n0 = cluster.Nodes[0];
        var logger = new EventLogger();
        n0.CapturedLogger = logger;
        await cluster.States[2].UpdateTermAsync(5L, resetLastVote: true, TestToken);
        await cluster.StartAsync();
        cluster.HoldFollowers();
        StartElection(cluster);

        await cluster.Network.DeliverAsync(await PendingAsync(cluster, 1, RaftMessageType.PreVote));
        var vote1 = await PendingAsync(cluster, 1, RaftMessageType.Vote);
        var vote2 = await PendingAsync(cluster, 2, RaftMessageType.Vote);

        // the candidate wins and appends its write barrier, then the higher-term response of node 2 arrives
        var (appending, release) = state.HoldNextAppend();
        await cluster.Network.DeliverAsync(vote1);
        await appending.WaitAsync(DefaultTimeout, TestToken);
        await cluster.Network.DeliverAsync(vote2);
        var response = await IsType<Task<Result<bool>>>(vote2.Completion);
        Equal(5L, response.Term);
        False(response.Value);
        release.SetResult();

        // the decided candidate is not affected by the late response
        Equal(n0.EndPoint, (await n0.WaitForLeaderAsync(DefaultTimeout, TestToken)).EndPoint);
        var leading = await logger.WaitAsync(0, nameof(TransitionToLeaderStateCompleted));
        Equal(1L, leading.Term);

        // the leader learns the higher term from its replication, as from any other response
        await cluster.Network.DeliverAsync(await PendingAsync(cluster, 1, RaftMessageType.AppendEntries));
        await cluster.Network.DeliverAsync(await PendingAsync(cluster, 2, RaftMessageType.AppendEntries));

        Equal(5L, (await logger.WaitAsync(leading.Index, nameof(DowngradedToFollowerState))).Term);
        Equal(5L, n0.Term);
        NotEqual(n0.EndPoint, n0.Leader?.EndPoint);
    }

    private static void StartElection(InProcessClusterFixture cluster)
    {
        cluster.Leader.StartElectionTimer();
        cluster.TimeProvider.Advance(ElectionTimeout);
    }

    private static Task<PendingMessage> PendingAsync(InProcessClusterFixture cluster, int member, RaftMessageType type)
        => cluster.PendingAsync(member, type).WaitAsync(DefaultTimeout, TestToken);

    private const string DowngradedToFollowerState = nameof(DowngradedToFollowerState);
    private const string VotingCompleted = nameof(VotingCompleted);
    private const string TransitionToLeaderStateStarted = nameof(TransitionToLeaderStateStarted);
    private const string TransitionToLeaderStateCompleted = nameof(TransitionToLeaderStateCompleted);

    private sealed class EventLogger : ILogger
    {
        private readonly List<Entry> entries = [];
        private TaskCompletionSource changed = new(TaskCreationOptions.RunContinuationsAsynchronously);

        // Waits for the first event with the given name logged after the entry with the given index (0: any).
        internal async Task<Entry> WaitAsync(int after, string eventName)
        {
            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(TestToken);
            timeout.CancelAfter(DefaultTimeout);
            for (;;)
            {
                Task signal;
                lock (entries)
                {
                    if (entries.FirstOrDefault(entry => entry.Index > after && entry.EventName.EndsWith('.' + eventName)) is { } result)
                        return result;

                    signal = changed.Task;
                }

                try
                {
                    await signal.WaitAsync(timeout.Token);
                }
                catch (OperationCanceledException) when (!TestToken.IsCancellationRequested)
                {
                    lock (entries)
                        throw new TimeoutException($"{eventName} was not logged:{Environment.NewLine}{string.Join(Environment.NewLine, entries)}");
                }
            }
        }

        internal bool Contains(string eventName)
        {
            lock (entries)
                return entries.Exists(entry => entry.EventName.EndsWith('.' + eventName));
        }

        IDisposable ILogger.BeginScope<TState>(TState state) => null;

        bool ILogger.IsEnabled(LogLevel logLevel) => true;

        void ILogger.Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception exception,
            Func<TState, Exception, string> formatter)
        {
            var term = state is IReadOnlyList<KeyValuePair<string, object>> properties
                && properties.FirstOrDefault(static p => p.Key is "Term").Value is long value
                ? value
                : -1L;

            lock (entries)
            {
                entries.Add(new(entries.Count + 1, eventId.Name ?? string.Empty, term));
                changed.TrySetResult();
                changed = new(TaskCreationOptions.RunContinuationsAsynchronously);
            }
        }

        internal sealed record Entry(int Index, string EventName, long Term);
    }

    // Holds the candidate's write barrier append, the window between its decision and the transition to leader.
    private sealed class GatedAppendState : IPersistentState, IDisposable
    {
        private readonly IPersistentState inner = new ConsensusOnlyState();
        private Gate gate;

        internal (Task Entered, TaskCompletionSource Release) HoldNextAppend()
        {
            var next = new Gate();
            if (Interlocked.CompareExchange(ref gate, next, null) is not null)
                throw new InvalidOperationException("An append is already held.");

            return (next.Entered.Task, next.Release);
        }

        public bool IsVotedFor(in ClusterMemberId id) => inner.IsVotedFor(in id);

        public long Term => inner.Term;

        public ValueTask<long> IncrementTermAsync(ClusterMemberId member, CancellationToken token = default)
            => inner.IncrementTermAsync(member, token);

        public ValueTask UpdateTermAsync(long term, bool resetLastVote, CancellationToken token = default)
            => inner.UpdateTermAsync(term, resetLastVote, token);

        public ValueTask UpdateVotedForAsync(ClusterMemberId member, CancellationToken token = default)
            => inner.UpdateVotedForAsync(member, token);

        public IClusterConfigurationStorage ConfigurationStorage
        {
            get => inner.ConfigurationStorage;
            set => inner.ConfigurationStorage = value;
        }

        public int Version => inner.Version;

        public bool IsLogEntryLengthAlwaysPresented => inner.IsLogEntryLengthAlwaysPresented;

        public long LastCommittedEntryIndex => inner.LastCommittedEntryIndex;

        public long LastEntryIndex => inner.LastEntryIndex;

        public ValueTask WaitForApplyAsync(CancellationToken token = default)
            => inner.WaitForApplyAsync(token);

        public ValueTask<long> CommitAsync(long endIndex, CancellationToken token = default)
            => inner.CommitAsync(endIndex, token);

        public Task InitializeAsync(CancellationToken token = default)
            => inner.InitializeAsync(token);

        public ValueTask<TResult> ReadAsync<TResult>(
            ILogEntryConsumer<IRaftLogEntry, TResult> reader,
            long startIndex,
            long endIndex,
            CancellationToken token = default)
            => inner.ReadAsync(reader, startIndex, endIndex, token);

        public ValueTask AppendAsync<TEntry>(
            ILogEntryProducer<TEntry> entries,
            long startIndex,
            bool skipCommitted = false,
            CancellationToken token = default)
            where TEntry : IRaftLogEntry
            => inner.AppendAsync(entries, startIndex, skipCommitted, token);

        public ValueTask AppendAsync<TEntry>(
            TEntry entry,
            long startIndex,
            CancellationToken token = default)
            where TEntry : IRaftLogEntry
            => inner.AppendAsync(entry, startIndex, token);

        // the candidate appends its no-op write barrier through this overload
        public async ValueTask<long> AppendAsync<TEntry>(TEntry entry, CancellationToken token = default)
            where TEntry : IRaftLogEntry
        {
            if (Interlocked.Exchange(ref gate, null) is { } held)
            {
                held.Entered.TrySetResult();
                await held.Release.Task.WaitAsync(token);
            }

            return await inner.AppendAsync(entry, token);
        }

        public void Dispose() => (inner as IDisposable)?.Dispose();

        private sealed class Gate
        {
            internal readonly TaskCompletionSource Entered = new(TaskCreationOptions.RunContinuationsAsynchronously);
            internal readonly TaskCompletionSource Release = new(TaskCreationOptions.RunContinuationsAsynchronously);
        }
    }
}
