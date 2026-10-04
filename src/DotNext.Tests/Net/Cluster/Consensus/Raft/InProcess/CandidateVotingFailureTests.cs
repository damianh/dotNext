using System.Runtime.CompilerServices;
using Microsoft.Extensions.Logging;

namespace DotNext.Net.Cluster.Consensus.Raft.InProcess;

using IO.Log;
using Membership;

/// <summary>
/// #26: a storage failure inside the candidate's voting task must be supervised like the heartbeat worker (#8):
/// reported above Debug with the exception and followed by a transition, so the election timer keeps running.
/// </summary>
public sealed class CandidateVotingFailureTests : RaftTest
{
    [Fact(Timeout = TestTimeouts.Default)]
    public static async Task WriteBarrierAppendFailureIsReportedAndElectionResumes()
    {
        var storage = new FaultingPersistentState();
        var logger = new CapturingLogger();
        await using var cluster = new InProcessClusterFixture(
            3,
            index => index is 0 ? storage : new ConsensusOnlyState());
        var candidate = cluster.Leader;
        candidate.CapturedLogger = logger;
        await cluster.StartAsync();

        // the votes are granted, then the candidate fails to append its no-op write barrier
        var injected = new IOException("Injected write barrier append failure.");
        var failureObserved = storage.FailNextAppend(injected);
        candidate.StartElectionTimer();
        cluster.TimeProvider.Advance(TimeSpan.FromMilliseconds(100));
        await failureObserved.WaitAsync(DefaultTimeout, TestToken);
        var failedTerm = candidate.Term;

        // the failure is transient, so a supervised node wins one of the next elections
        var elected = await ElectWithinAsync(cluster, candidate, electionTimeouts: 10);

        var trace = Describe(logger, candidate);
        True(
            logger.Entries.Any(entry => entry.Level >= LogLevel.Error && object.ReferenceEquals(injected, entry.Exception)),
            $"The failure was not reported above Debug:{Environment.NewLine}{trace}");
        True(elected, $"The node did not leave the failed candidate state:{Environment.NewLine}{trace}");
        True(candidate.Term > failedTerm, trace);
    }

    private static async Task<bool> ElectWithinAsync(InProcessClusterFixture cluster, InProcessCluster candidate, int electionTimeouts)
    {
        var election = candidate.WaitForLeadershipAsync(TestToken);
        for (var i = 0; i < electionTimeouts; i++)
        {
            cluster.TimeProvider.Advance(TimeSpan.FromMilliseconds(100));
            try
            {
                await election.WaitAsync(TimeSpan.FromMilliseconds(200), TestToken);
                return true;
            }
            catch (TimeoutException)
            {
                // the next election timeout
            }
        }

        return false;
    }

    private static string Describe(CapturingLogger logger, InProcessCluster node)
        => string.Join(Environment.NewLine, logger.Entries.Select(static entry => $"{entry.Level} {entry.EventName} {entry.Exception?.GetType().Name}"))
           + $"{Environment.NewLine}state={Accessors<InProcessClusterMember>.State(node)?.GetType().Name}, term={node.Term}";

    private static class Accessors<TMember>
        where TMember : class, IRaftClusterMember, IDisposable
    {
        [UnsafeAccessor(UnsafeAccessorKind.Field, Name = "state")]
        internal static extern ref RaftState<TMember> State(RaftCluster<TMember> cluster);
    }
    private sealed class CapturingLogger : ILogger
    {
        private readonly List<Entry> entries = [];

        internal Entry[] Entries
        {
            get
            {
                lock (entries)
                    return entries.ToArray();
            }
        }

        IDisposable ILogger.BeginScope<TState>(TState state) => null;

        bool ILogger.IsEnabled(LogLevel logLevel) => true;

        void ILogger.Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception exception,
            Func<TState, Exception, string> formatter)
        {
            lock (entries)
                entries.Add(new(logLevel, eventId.Name, exception));
        }

        internal readonly record struct Entry(LogLevel Level, string EventName, Exception Exception);
    }

    private sealed class FaultingPersistentState : IPersistentState, IDisposable
    {
        private readonly IPersistentState inner = new ConsensusOnlyState();
        private AppendFailure appendFailure;

        internal Task FailNextAppend(Exception exception)
        {
            var failure = new AppendFailure(exception);
            if (Interlocked.CompareExchange(ref appendFailure, failure, null) is not null)
                throw new InvalidOperationException("An append failure is already pending.");

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
        public ValueTask<long> AppendAsync<TEntry>(TEntry entry, CancellationToken token = default)
            where TEntry : IRaftLogEntry
        {
            if (Interlocked.Exchange(ref appendFailure, null) is not { } failure)
                return inner.AppendAsync(entry, token);

            failure.Observed.TrySetResult();
            return ValueTask.FromException<long>(failure.Exception);
        }

        public void Dispose() => (inner as IDisposable)?.Dispose();

        private sealed class AppendFailure(Exception exception)
        {
            internal readonly Exception Exception = exception;
            internal readonly TaskCompletionSource Observed = new(TaskCreationOptions.RunContinuationsAsynchronously);
        }
    }
}
