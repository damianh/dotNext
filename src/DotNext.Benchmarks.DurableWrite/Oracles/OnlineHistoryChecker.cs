using DotNext.Net.Cluster.Consensus.Raft.InProcess;

namespace DotNext.Benchmarks.DurableWrite.Oracles;

/// <summary>
/// Checks the history while the workload runs. Every node reports each entry it applies, and the workload reports
/// each acknowledged write and leader claim.
/// </summary>
/// <remarks>
/// The incremental checks are:
/// <list type="bullet">
/// <item>apply order: each node applies indexes 1, 2, 3, ... without gaps or repeats (a snapshot install resets
/// the position to the snapshot index), a write is applied at one index only, and the writes of one closed-loop
/// client are applied in the order the client sent them;</item>
/// <item>committed-prefix agreement: the first node that applies an index fixes its term and write, and every
/// other node must apply the same;</item>
/// <item>acknowledged writes: an acknowledged write must already be applied somewhere;</item>
/// <item>election safety: at most one leader per term, through <see cref="SimulationHistory"/>.</item>
/// </list>
/// At the end of a run, <see cref="CheckFinal"/> feeds every node's applied prefix to the <see cref="SimulationHistory"/>
/// oracles of the seeded simulation. Violations are recorded, not thrown, because they are detected inside the state
/// machine, where an exception would fault the write-ahead log instead of reporting the oracle. The first one wins.
/// </remarks>
internal sealed class OnlineHistoryChecker
{
    internal const string ApplyOrder = "apply order";
    internal const string PrefixAgreement = "committed-prefix agreement";
    internal const string AcknowledgedWrites = "acknowledged writes";
    internal const string ElectionSafety = "election safety";
    internal const string Durability = "durability";
    internal const string Reconciliation = "history reconciliation";

    private readonly Lock sync = new();
    private readonly SimulationHistory history = new();
    private readonly List<(long Term, WriteKey? Key, int Node)> canonical = [];
    private readonly Dictionary<WriteKey, long> indexByKey = [];
    private readonly long[] lastApplied;
    private readonly Dictionary<int, long>[] lastSeqByClient;
    private readonly List<AcknowledgedWrite> acknowledged = [];
    private SafetyViolationException? violation;

    internal OnlineHistoryChecker(int nodes)
    {
        lastApplied = new long[nodes];
        lastSeqByClient = new Dictionary<int, long>[nodes];
        for (var i = 0; i < nodes; i++)
            lastSeqByClient[i] = [];
    }

    internal SafetyViolationException? Violation => Volatile.Read(in violation);

    internal long AppliedLength
    {
        get
        {
            lock (sync)
                return canonical.Count;
        }
    }

    internal long LastApplied(int node)
    {
        lock (sync)
            return lastApplied[node];
    }

    internal IReadOnlyList<AcknowledgedWrite> Acknowledged
    {
        get
        {
            lock (sync)
                return acknowledged.ToArray();
        }
    }

    internal void Report(SafetyViolationException e) => Interlocked.CompareExchange(ref violation, e, null);

    private void Report(string oracle, string message) => Report(new SafetyViolationException(oracle, message));

    /// <summary>
    /// The node has replaced its state with a snapshot (at startup, or installed from the leader).
    /// </summary>
    internal void OnRestored(int node, IReadOnlyList<AppliedEntry> prefix)
    {
        lock (sync)
        {
            var seqs = lastSeqByClient[node];
            seqs.Clear();
            for (var i = 0; i < prefix.Count; i++)
            {
                var entry = prefix[i];
                if (entry.Index != i + 1L)
                {
                    Report(ApplyOrder, $"the snapshot restored on node {node} holds index {entry.Index} at position {i + 1}");
                    break;
                }

                CheckClientOrder(node, entry, seqs);
                CheckAgreement(node, entry);
            }

            lastApplied[node] = prefix.Count;
        }
    }

    /// <summary>
    /// The node has applied one entry to its state machine.
    /// </summary>
    internal void OnApplied(int node, in AppliedEntry entry)
    {
        lock (sync)
        {
            var expected = lastApplied[node] + 1L;
            if (entry.Index != expected)
            {
                Report(ApplyOrder,
                    $"node {node} applied index {entry.Index} ('{WriteKey.ToPayloadString(entry.Key)}') after index {expected - 1L}");
            }

            lastApplied[node] = long.Max(lastApplied[node], entry.Index);
            CheckClientOrder(node, entry, lastSeqByClient[node]);
            CheckAgreement(node, entry);
        }
    }

    private void CheckClientOrder(int node, in AppliedEntry entry, Dictionary<int, long> seqs)
    {
        if (entry.Key is not { Mode: WriteKey.ClosedLoop } key)
            return;

        // A closed-loop client sends its next write only after the previous one is acknowledged or its outcome is
        // unknown (the leadership was lost). Raft never commits a later write of that client before an earlier one:
        // the earlier write either precedes it in the log or has an older term and is never committed after it.
        if (seqs.TryGetValue(key.Client, out var previous) && key.Seq <= previous)
        {
            Report(ApplyOrder,
                $"node {node} applied '{key}' at index {entry.Index} after write {previous} of the same closed-loop client");
        }

        seqs[key.Client] = long.Max(previous, key.Seq);
    }

    private void CheckAgreement(int node, in AppliedEntry entry)
    {
        var position = entry.Index - 1L;
        if (position < canonical.Count)
        {
            var (term, key, firstNode) = canonical[(int)position];
            var firstSkipped = term is 0L;
            if (firstSkipped != entry.IsSkipped && (key ?? entry.Key) is { } write)
            {
                // A write that one node applied and another passed over: an entry was lost on one of them.
                var skipper = firstSkipped ? firstNode : node;
                var applier = firstSkipped ? node : firstNode;
                Report(ApplyOrder, $"node {skipper} skipped index {entry.Index}, which node {applier} applied as '{write}'");
            }
            else if (firstSkipped)
            {
                // The entry without payload was not passed to the first node, so its term is not known yet.
                if (!entry.IsSkipped)
                    canonical[(int)position] = (entry.Term, entry.Key, node);
            }
            else if (!entry.IsSkipped && (term != entry.Term || key != entry.Key))
            {
                Report(PrefixAgreement,
                    $"index {entry.Index} is applied as (term {term}, '{WriteKey.ToPayloadString(key)}') on node {firstNode} " +
                    $"but as (term {entry.Term}, '{WriteKey.ToPayloadString(entry.Key)}') on node {node}");
            }
        }
        else if (position == canonical.Count)
        {
            canonical.Add((entry.Term, entry.Key, node));
            if (entry.Key is { } key && !indexByKey.TryAdd(key, entry.Index))
            {
                Report(ApplyOrder, $"'{key}' is applied at index {indexByKey[key]} and again at index {entry.Index}");
            }
        }

        // A gap in the canonical log means the node skipped an index, which was reported above.
    }

    /// <summary>
    /// The cluster acknowledged a write to the client: <c>ReplicateAsync</c> returned on the leader <paramref name="node"/>.
    /// </summary>
    /// <returns>The index of the write, or -1 if it was not applied anywhere, which is a violation.</returns>
    internal long OnAcknowledged(int node, WriteKey key)
    {
        lock (sync)
        {
            if (!indexByKey.TryGetValue(key, out var index))
            {
                Report(AcknowledgedWrites, $"'{key}' was acknowledged by node {node} but no node has applied it");
                index = -1L;
            }

            acknowledged.Add(new(key, node, index));
            history.RecordAcknowledged(key.ToString(), node, index);
            return index;
        }
    }

    internal void OnLeaderClaim(int node, long term)
    {
        lock (sync)
        {
            try
            {
                history.RecordLeaderClaim(node, term);
            }
            catch (SafetyViolationException e)
            {
                Report(e);
            }
        }
    }

    /// <summary>
    /// Runs the <see cref="SimulationHistory"/> committed-prefix and acknowledged-write oracles over the applied
    /// prefix of every node, after the workload has stopped.
    /// </summary>
    /// <param name="prefixes">The applied prefix of each node, starting at index 1; <see langword="null"/> for a node
    /// that has no state machine.</param>
    internal void CheckFinal(IReadOnlyList<IReadOnlyList<AppliedEntry>?> prefixes)
    {
        lock (sync)
        {
            try
            {
                for (var node = 0; node < prefixes.Count; node++)
                {
                    if (prefixes[node] is not { } prefix)
                        continue;

                    var committed = new CommittedEntry[prefix.Count];
                    for (var i = 0; i < committed.Length; i++)
                    {
                        var entry = prefix[i];

                        // A skipped entry has no term of its own: whether a node skipped an entry another node
                        // applied is checked online, so here it takes the term and write applied at that index.
                        if (entry.IsSkipped && i < canonical.Count && canonical[i] is { Term: not 0L } known)
                            entry = entry with { Term = known.Term, Key = known.Key };

                        committed[i] = new(entry.Index, entry.Term, WriteKey.ToPayloadString(entry.Key));
                    }

                    history.ObserveCommittedPrefix(node, committed);
                }

                history.RequireAcknowledgedWritesPreserved();
            }
            catch (SafetyViolationException e)
            {
                Report(e);
            }
        }
    }
}
