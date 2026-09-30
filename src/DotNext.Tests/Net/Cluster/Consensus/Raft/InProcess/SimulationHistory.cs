namespace DotNext.Net.Cluster.Consensus.Raft.InProcess;

/// <summary>
/// A committed log entry as observed on one node.
/// </summary>
internal readonly record struct CommittedEntry(long Index, long Term, string Payload);

/// <summary>
/// A safety oracle failed: the recorded history is impossible for a correct Raft cluster.
/// </summary>
internal sealed class SafetyViolationException(string oracle, string message)
    : Exception($"{oracle}: {message}")
{
    internal string Oracle => oracle;
}

/// <summary>
/// The cluster did not make progress after every fault was removed. Not a safety failure.
/// </summary>
internal sealed class LivenessFailureException(string message) : Exception(message);

/// <summary>
/// Independent safety oracles over what the simulation observed. They know nothing about the cluster,
/// only about claims, committed prefixes and acknowledged client writes, so they can be tested with synthetic histories.
/// </summary>
internal sealed class SimulationHistory
{
    private readonly Dictionary<long, int> leaderByTerm = [];
    private readonly Dictionary<long, (CommittedEntry Entry, int Node)> committed = [];
    private readonly List<AcknowledgedWrite> acknowledged = [];
    private readonly Dictionary<int, int> latestPrefixLength = [];

    internal int AcknowledgedCount => acknowledged.Count;

    /// <summary>
    /// Oracle (a), election safety: a node claimed leadership of <paramref name="term"/>.
    /// At most one node may do so per term. The same node may claim a term again after it restarted.
    /// </summary>
    internal void RecordLeaderClaim(int node, long term)
    {
        if (!leaderByTerm.TryAdd(term, node) && leaderByTerm[term] != node)
        {
            throw new SafetyViolationException(
                "election safety",
                $"nodes {leaderByTerm[term]} and {node} both claimed leadership of term {term}");
        }
    }

    /// <summary>
    /// Records that <see cref="IRaftCluster.ReplicateAsync"/> on <paramref name="node"/> completed successfully.
    /// </summary>
    /// <param name="payload">The unique payload of the write.</param>
    /// <param name="node">The node that acknowledged the write.</param>
    /// <param name="index">The index of the entry on that node, or -1 if it could not be determined.</param>
    internal void RecordAcknowledged(string payload, int node, long index)
        => acknowledged.Add(new(payload, node, index));

    /// <summary>
    /// Oracles (b) and (c): checks the committed prefix (indexes 1 to n, without gaps) of one node.
    /// </summary>
    /// <remarks>
    /// (b) Every index that two observations commit must hold the same term and payload, across nodes and across time.
    /// (c) Every acknowledged write must be at its index, with its payload, on every node whose prefix covers it.
    /// </remarks>
    internal void ObserveCommittedPrefix(int node, IReadOnlyList<CommittedEntry> prefix)
    {
        latestPrefixLength[node] = prefix.Count;
        foreach (var entry in prefix)
        {
            if (!committed.TryAdd(entry.Index, (entry, node)))
            {
                var (previous, previousNode) = committed[entry.Index];
                if (previous.Term != entry.Term || previous.Payload != entry.Payload)
                {
                    throw new SafetyViolationException(
                        "committed-prefix agreement",
                        $"index {entry.Index} is committed as (term {previous.Term}, '{previous.Payload}') on node {previousNode} " +
                        $"but as (term {entry.Term}, '{entry.Payload}') on node {node}");
                }
            }
        }

        var byPayload = default(Dictionary<string, long>);
        for (var i = 0; i < acknowledged.Count; i++)
        {
            var write = acknowledged[i];
            if (write.Index < 0L)
            {
                byPayload ??= prefix.GroupBy(static e => e.Payload).ToDictionary(static g => g.Key, static g => g.First().Index);
                if (byPayload.TryGetValue(write.Payload, out var found))
                    acknowledged[i] = write = write with { Index = found };
                else
                    continue;
            }

            if (write.Index <= prefix.Count)
            {
                var entry = prefix[(int)write.Index - 1];
                if (entry.Payload != write.Payload)
                {
                    throw new SafetyViolationException(
                        "acknowledged writes",
                        $"'{write.Payload}' was acknowledged by node {write.Node} at index {write.Index}, " +
                        $"but node {node} has committed '{entry.Payload}' (term {entry.Term}) there");
                }
            }
        }
    }

    /// <summary>
    /// Oracle (c) at the end of a run: every acknowledged write must be in some node's latest observed committed prefix.
    /// </summary>
    internal void RequireAcknowledgedWritesPreserved()
    {
        var longest = latestPrefixLength.Count is 0 ? 0 : latestPrefixLength.Values.Max();
        foreach (var write in acknowledged)
        {
            if (write.Index < 0L || write.Index > longest)
            {
                throw new SafetyViolationException(
                    "acknowledged writes",
                    $"'{write.Payload}' was acknowledged by node {write.Node} (index {write.Index}) but the longest committed prefix " +
                    $"observed at the end has {longest} entries and does not contain it");
            }
        }
    }

    private readonly record struct AcknowledgedWrite(string Payload, int Node, long Index);
}
