namespace DotNext.Net.Cluster.Consensus.Raft.InProcess;

using IO.Log;
using Membership;
using Threading;

public sealed class HeartbeatFailureTests : RaftTest
{
    [Fact]
    public static async Task CommitFailureInvalidatesLeadership()
    {
        var storage = new FaultingPersistentState();
        await using var cluster = new InProcessClusterFixture(
            3,
            index => index is 0 ? storage : new ConsensusOnlyState());
        await cluster.StartLeaderAsync();

        var leadership = cluster.Leader.LeadershipToken;
        var failureObserved = storage.FailNextCommit(new IOException("Injected commit failure."));
        var replication = cluster.Leader.ReplicateAsync(
            new EmptyLogEntry { Term = cluster.Leader.Term },
            TestToken).AsTask();

        foreach (var message in await cluster.PendingRoundAsync())
            await cluster.Network.DeliverAsync(message);

        await failureObserved.WaitAsync(DefaultTimeout, TestToken);
        await leadership.WaitAsync().AsTask().WaitAsync(DefaultTimeout, TestToken);
        await ThrowsAsync<NotLeaderException>(replication.WaitAsync(DefaultTimeout, TestToken));
        await ThrowsAsync<NotLeaderException>(
            cluster.Leader.ForceReplicationAsync(TestToken).AsTask());
    }

    [Fact]
    public static async Task TimerFailureInvalidatesLeadership()
    {
        await using var cluster = new InProcessClusterFixture(3);
        await cluster.StartLeaderAsync();

        var leadership = cluster.Leader.LeadershipToken;
        var failureObserved = cluster.TimeProvider.FailNextTimer(
            new OperationCanceledException("Injected timer cancellation.", new CancellationToken(canceled: true)));
        var replication = cluster.Leader.ForceReplicationAsync(TestToken).AsTask();
        foreach (var message in await cluster.PendingRoundAsync())
            await cluster.Network.DeliverAsync(message);
        await replication.WaitAsync(DefaultTimeout, TestToken);

        await failureObserved.WaitAsync(DefaultTimeout, TestToken);
        await leadership.WaitAsync().AsTask().WaitAsync(DefaultTimeout, TestToken);
        await ThrowsAsync<NotLeaderException>(
            cluster.Leader.ForceReplicationAsync(TestToken).AsTask());
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public static async Task FailureDuringShutdownDoesNotDeadlock(bool dispose)
    {
        var storage = new FaultingPersistentState();
        var cluster = new InProcessClusterFixture(
            3,
            index => index is 0 ? storage : new ConsensusOnlyState());
        var disposalStarted = false;
        try
        {
            await cluster.StartLeaderAsync();

            var leadership = cluster.Leader.LeadershipToken;
            var failureObserved = storage.FailNextCommit(new IOException("Injected commit failure."));
            var replication = cluster.Leader.ReplicateAsync(
                new EmptyLogEntry { Term = cluster.Leader.Term },
                TestToken).AsTask();
            foreach (var message in await cluster.PendingRoundAsync())
                await cluster.Network.DeliverAsync(message);
            await failureObserved.WaitAsync(DefaultTimeout, TestToken);

            Task shutdown;
            if (dispose)
            {
                disposalStarted = true;
                shutdown = cluster.DisposeAsync().AsTask();
            }
            else
            {
                shutdown = cluster.Leader.StopAsync(TestToken);
            }

            await shutdown.WaitAsync(DefaultTimeout, TestToken);
            await ThrowsAsync<NotLeaderException>(replication.WaitAsync(DefaultTimeout, TestToken));
            True(leadership.IsCancellationRequested);
        }
        finally
        {
            if (!disposalStarted)
                await cluster.DisposeAsync();
        }
    }

    private sealed class FaultingPersistentState : IPersistentState, IDisposable
    {
        private readonly IPersistentState inner = new ConsensusOnlyState();
        private CommitFailure commitFailure;

        internal Task FailNextCommit(Exception exception)
        {
            ArgumentNullException.ThrowIfNull(exception);

            var failure = new CommitFailure(exception);
            if (Interlocked.CompareExchange(ref commitFailure, failure, null) is not null)
                throw new InvalidOperationException("A commit failure is already pending.");

            return failure.Observed.Task;
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
        {
            if (Interlocked.Exchange(ref commitFailure, null) is not { } failure)
                return inner.CommitAsync(endIndex, token);

            failure.Observed.TrySetResult();
            return ValueTask.FromException<long>(failure.Exception);
        }

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

        public ValueTask<long> AppendAsync<TEntry>(TEntry entry, CancellationToken token = default)
            where TEntry : IRaftLogEntry
            => inner.AppendAsync(entry, token);

        public void Dispose() => (inner as IDisposable)?.Dispose();

        private sealed class CommitFailure(Exception exception)
        {
            internal readonly Exception Exception = exception;
            internal readonly TaskCompletionSource Observed = new(TaskCreationOptions.RunContinuationsAsynchronously);
        }
    }
}
