using Microsoft.Extensions.Logging;

namespace DotNext.Net.Cluster.Consensus.Raft.InProcess;

using Diagnostics;
using IO.Log;
using Membership;

/// <summary>
/// #115: a local storage read failure on the leader must not be reported as an unresponsive healthy peer.
/// </summary>
public sealed class LeaderReadFailureAttributionTests : RaftTest
{
    private const int LocalLogReadFailedEventId = 74049;
    private const int UnresponsiveMemberDetectedEventId = 74037;

    [Fact(Timeout = TestTimeouts.Default)]
    public static async Task LocalReadFailureIsNotAttributedToLaggingPeer()
    {
        var storage = new ReadFaultingPersistentState();
        var logger = new CapturingLogger();
        var armed = new StrongBox<bool>();
        await using var cluster = new InProcessClusterFixture(
            3,
            index => index is 0 ? storage : new ConsensusOnlyState(),
            failureDetectorFactory: (_, _) => new MissCountingDetector(armed, threshold: 3));
        cluster.Leader.CapturedLogger = logger;

        // node-2 misses index 1, so only its replication needs to read that range
        await cluster.StartLeaderAsync(laggingFollower: 2);
        foreach (var node in cluster.Nodes.Skip(1))
            cluster.Network.Release(cluster.Leader.EndPoint, node.EndPoint);

        var injected = new IOException("Injected local read failure.");
        storage.FailReadsFrom(1L, injected);
        armed.Value = true;

        for (var i = 0; i < 6; i++)
        {
            try
            {
                await cluster.Leader.ForceReplicationAsync(TestToken).AsTask().WaitAsync(DefaultTimeout, TestToken);
            }
            catch (NotLeaderException)
            {
                Fail($"Leadership lost at round {i}:{Environment.NewLine}{string.Join(Environment.NewLine, logger.Entries.Select(static e => $"{e.Level} {e.EventId.Id} {e.EventId.Name} {e.Message} {e.Exception?.GetType().Name}"))}");
            }
        }

        // unresponsive member processing is asynchronous
        for (var i = 0; i < 20 && !logger.Entries.Any(static e => e.EventId.Name?.EndsWith("UnresponsiveMemberDetected") is true); i++)
            await Task.Delay(50, TestToken);

        var trace = string.Join(Environment.NewLine, logger.Entries.Select(static e => $"{e.Level} {e.EventId.Id} {e.EventId.Name} {e.Message} {e.Exception?.GetType().Name}"));
        True(cluster.Leader.LeadershipToken is { IsCancellationRequested: false }, trace);
        False(
            logger.Entries.Any(static e => e.EventId.Name?.EndsWith("UnresponsiveMemberDetected") is true),
            $"A healthy peer was reported as unresponsive because of a local read failure:{Environment.NewLine}{trace}");
        True(
            logger.Entries.Any(e => object.ReferenceEquals(injected, e.Exception) && e.EventId.Id is not 0),
            $"The local read failure was not reported with a dedicated event:{Environment.NewLine}{trace}");

        var reported = logger.Entries.Where(static e => e.EventId.Id is LocalLogReadFailedEventId).ToArray();
        NotEmpty(reported);
        All(reported, e =>
        {
            Equal(LogLevel.Error, e.Level);
            Same(injected, e.Exception);
            Contains(cluster.Nodes[2].EndPoint.ToString(), e.Message);
        });

        // one event per failed replication round, not per entry
        InRange(reported.Length, 1, 6);
        DoesNotContain(logger.Entries, static e => e.EventId.Id is 0);
    }

    [Fact(Timeout = TestTimeouts.Default)]
    public static async Task FullLocalReadFailureStepsDownWithoutBlamingPeers()
    {
        var storage = new ReadFaultingPersistentState();
        var logger = new CapturingLogger();
        var armed = new StrongBox<bool>();
        await using var cluster = new InProcessClusterFixture(
            3,
            index => index is 0 ? storage : new ConsensusOnlyState(),
            failureDetectorFactory: (_, _) => new MissCountingDetector(armed, threshold: 3));
        cluster.Leader.CapturedLogger = logger;

        await cluster.StartLeaderAsync();
        foreach (var node in cluster.Nodes.Skip(1))
            cluster.Network.Release(cluster.Leader.EndPoint, node.EndPoint);

        var leadershipToken = cluster.Leader.LeadershipToken;
        var injected = new IOException("Injected local read failure.");
        storage.FailAllReads(injected);
        armed.Value = true;

        await ThrowsAsync<NotLeaderException>(
            cluster.Leader.ForceReplicationAsync(TestToken).AsTask().WaitAsync(DefaultTimeout, TestToken));
        True(leadershipToken.IsCancellationRequested);

        var trace = Trace(logger);
        DoesNotContain(logger.Entries, static e => e.EventId.Id is UnresponsiveMemberDetectedEventId);
        DoesNotContain(logger.Entries, static e => e.EventId.Id is 0);
        foreach (var node in cluster.Nodes.Skip(1))
        {
            True(
                logger.Entries.Any(e => e.EventId.Id is LocalLogReadFailedEventId
                                        && object.ReferenceEquals(injected, e.Exception)
                                        && e.Message.Contains(node.EndPoint.ToString())),
                $"No local read failure was reported for {node.EndPoint}:{Environment.NewLine}{trace}");
        }
    }

    [Fact(Timeout = TestTimeouts.Default)]
    public static async Task PeerTransportFailureIsStillReportedAsUnresponsive()
    {
        var logger = new CapturingLogger();
        var armed = new StrongBox<bool>();
        await using var cluster = new InProcessClusterFixture(
            3,
            failureDetectorFactory: (_, _) => new MissCountingDetector(armed, threshold: 3));
        cluster.Leader.CapturedLogger = logger;

        await cluster.StartLeaderAsync();
        foreach (var node in cluster.Nodes.Skip(1))
            cluster.Network.Release(cluster.Leader.EndPoint, node.EndPoint);

        var peer = cluster.Nodes[2].EndPoint;
        for (var i = 0; i < 6; i++)
            _ = cluster.Network.FailNext(cluster.Leader.EndPoint, peer, RaftMessageType.AppendEntries,
                new IOException("Injected transport failure."));
        armed.Value = true;

        for (var i = 0; i < 6; i++)
            await cluster.Leader.ForceReplicationAsync(TestToken).AsTask().WaitAsync(DefaultTimeout, TestToken);

        // unresponsive member processing is asynchronous
        for (var i = 0; i < 20 && !logger.Entries.Any(static e => e.EventId.Id is UnresponsiveMemberDetectedEventId); i++)
            await Task.Delay(50, TestToken);

        var trace = Trace(logger);
        True(
            logger.Entries.Any(e => e.EventId.Id is UnresponsiveMemberDetectedEventId && e.Message.Contains(peer.ToString())),
            $"A peer failing at the transport was not reported as unresponsive:{Environment.NewLine}{trace}");
        DoesNotContain(logger.Entries, static e => e.EventId.Id is LocalLogReadFailedEventId);
    }

    private static string Trace(CapturingLogger logger)
        => string.Join(Environment.NewLine, logger.Entries.Select(static e => $"{e.Level} {e.EventId.Id} {e.EventId.Name} {e.Message} {e.Exception?.GetType().Name}"));

    private sealed class StrongBox<T>
    {
        internal volatile bool Value;
    }

    private sealed class MissCountingDetector(StrongBox<bool> armed, int threshold) : IFailureDetector
    {
        private int misses;

        public bool IsMonitoring => true;

        public bool IsHealthy => !armed.Value || Interlocked.Increment(ref misses) < threshold;

        public void ReportHeartbeat() => misses = 0;

        public void Reset() => misses = 0;
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
                entries.Add(new(logLevel, eventId, formatter(state, exception), exception));
        }

        internal readonly record struct Entry(LogLevel Level, EventId EventId, string Message, Exception Exception);
    }

    private sealed class ReadFaultingPersistentState : IPersistentState, IDisposable
    {
        private readonly IPersistentState inner = new ConsensusOnlyState();
        private volatile Exception readFailure;
        private long failFromIndex = long.MaxValue;

        private volatile bool failAll;

        internal void FailReadsFrom(long index, Exception exception)
        {
            failFromIndex = index;
            readFailure = exception;
        }

        internal void FailAllReads(Exception exception)
        {
            failAll = true;
            readFailure = exception;
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

        // only the replication read of the old range fails, as with a corrupted segment that only a lagging peer needs
        public ValueTask<TResult> ReadAsync<TResult>(
            ILogEntryConsumer<IRaftLogEntry, TResult> reader,
            long startIndex,
            long endIndex,
            CancellationToken token = default)
            => readFailure is { } failure && reader.GetType().Name.StartsWith("ReplicationProcess", StringComparison.Ordinal)
                                         && (failAll || startIndex <= endIndex && startIndex <= failFromIndex && endIndex >= failFromIndex)
                ? ValueTask.FromException<TResult>(failure)
                : inner.ReadAsync(reader, startIndex, endIndex, token);

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
    }
}
