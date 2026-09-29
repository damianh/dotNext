namespace DotNext.Net.Cluster.Consensus.Raft;

using IO.Log;

public partial class RaftCluster<TMember>
{
    // Instance of ReplicationWithSenderTermDetector<TEntry>.
    // Protected by transition lock
    private object? cachedTermDetector;

    private bool UseTermDetector<TEntry>(ref ILogEntryProducer<TEntry> entries, long expectedTerm)
        where TEntry : IRaftLogEntry
    {
        var entriesCopy = entries;
        if (entriesCopy.RemainingCount is 0L)
            return false;
        
        if (cachedTermDetector is ReplicationWithSenderTermDetector<TEntry> detector)
        {
            cachedTermDetector = null;
        }
        else
        {
            detector = new();
        }

        detector.Initialize(entriesCopy, expectedTerm);
        entries = detector;
        return true;
    }

    private bool IsReplicatedWithExpectedTerm<TEntry>(ILogEntryProducer<TEntry> entries, out bool configurationDetected)
        where TEntry : IRaftLogEntry
    {
        bool replicated;
        if (entries is ReplicationWithSenderTermDetector<TEntry> detector)
        {
            replicated = detector.IsReplicatedWithExpectedTerm;
            configurationDetected = detector.IsConfigurationDetected;
            detector.Reset();
            cachedTermDetector = detector;
        }
        else
        {
            replicated = configurationDetected = false;
        }

        return replicated;
    }
}

file sealed class ReplicationWithSenderTermDetector<TEntry> : ILogEntryProducer<TEntry>, IResettable
    where TEntry : IRaftLogEntry
{
    private ILogEntryProducer<TEntry> entries = ILogEntryProducer<TEntry>.Empty;
    private long expectedTerm;
    private bool replicatedWithExpectedTerm, configurationDetected;

    public bool IsReplicatedWithExpectedTerm => replicatedWithExpectedTerm;

    public bool IsConfigurationDetected => configurationDetected;

    public void Initialize(ILogEntryProducer<TEntry> entries, long expectedTerm)
    {
        this.entries = entries;
        this.expectedTerm = expectedTerm;
        configurationDetected = false;
    }

    public void Reset() => entries = ILogEntryProducer<TEntry>.Empty;

    TEntry IAsyncEnumerator<TEntry>.Current
    {
        get
        {
            var entry = entries.Current;
            replicatedWithExpectedTerm |= expectedTerm == entry.Term;
            configurationDetected |= entry.IsConfiguration;
            return entry;
        }
    }

    long ILogEntryProducer<TEntry>.RemainingCount => entries.RemainingCount;

    ValueTask<bool> IAsyncEnumerator<TEntry>.MoveNextAsync() => entries.MoveNextAsync();

    ValueTask IAsyncDisposable.DisposeAsync() => entries.DisposeAsync();
}