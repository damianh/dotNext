using DotNext.Benchmarks.DurableWrite.Oracles;
using DotNext.Net.Cluster.Consensus.Raft.InProcess;

namespace DotNext.Raft.FaultCampaign.Driver;

/// <summary>
/// Feeds the applied histories that the driver polls from the node processes to the <see cref="OnlineHistoryChecker"/>,
/// in the order each node applied them.
/// </summary>
/// <remarks>
/// In the durable-write tool the state machine calls the checker in-process. Here the history crosses a process
/// boundary, so this class replays it: a page that continues the known history is reported entry by entry through
/// <see cref="OnlineHistoryChecker.OnApplied"/>; a page from a new incarnation (a restarted process) or a new epoch (a
/// snapshot was restored) replaces the history and is reported through <see cref="OnlineHistoryChecker.OnRestored"/>,
/// which re-checks the whole prefix. A restarted node may come back with a shorter history than it had applied before,
/// if its commit index was not yet persisted; that is not a violation, and the entries are checked again when the
/// node applies them again.
/// </remarks>
internal sealed class HistoryFeed(OnlineHistoryChecker checker, int nodes)
{
    private readonly NodeHistory[] histories = Enumerable.Range(0, nodes).Select(static _ => new NodeHistory()).ToArray();

    internal (int From, Guid Incarnation, int Epoch) NextRequest(int node)
    {
        var history = histories[node];
        return (history.Entries.Count, history.Incarnation, history.Epoch);
    }

    internal IReadOnlyList<AppliedEntry> Current(int node) => histories[node].Entries;

    /// <summary>
    /// The number of times the history of a node was replaced: restarts and snapshot restores.
    /// </summary>
    internal int Replacements(int node) => histories[node].Replacements;

    /// <returns><see langword="false"/> if the page does not continue the known history and does not replace it either;
    /// it is dropped and the history is requested again.</returns>
    internal bool Ingest(int node, Guid incarnation, int epoch, int from, IReadOnlyList<AppliedEntry> entries)
    {
        var history = histories[node];
        if (incarnation != history.Incarnation || epoch != history.Epoch)
        {
            if (from is not 0)
                return false;

            history.Incarnation = incarnation;
            history.Epoch = epoch;
            history.Entries.Clear();
            history.Entries.AddRange(entries);
            history.Replacements++;
            checker.OnRestored(node, history.Entries);
            return true;
        }

        if (from != history.Entries.Count)
            return false;

        foreach (var entry in entries)
        {
            history.Entries.Add(entry);
            checker.OnApplied(node, in entry);
        }

        return true;
    }

    private sealed class NodeHistory
    {
        internal Guid Incarnation;
        internal int Epoch = -1;
        internal int Replacements;
        internal readonly List<AppliedEntry> Entries = [];
    }
}

/// <summary>
/// The recovery oracle that the campaign adds to the durable-write tool's oracles: after a fault is removed and every
/// node has caught up with the leader's commit index, every node holds every acknowledged write at its index.
/// </summary>
internal static class RecoveryAudit
{
    /// <param name="acknowledged">The acknowledged writes with the index each one was applied at.</param>
    /// <param name="histories">The applied history of each node, from index 1.</param>
    internal static SafetyViolationException? Check(IReadOnlyList<AcknowledgedWrite> acknowledged, IReadOnlyList<IReadOnlyList<AppliedEntry>> histories)
    {
        foreach (var write in acknowledged)
        {
            for (var node = 0; node < histories.Count; node++)
            {
                var history = histories[node];
                if (write.Index < 1L || write.Index > history.Count)
                {
                    return new(OnlineHistoryChecker.Durability,
                        $"'{write.Key}' was acknowledged by node {write.Node} at index {write.Index}, but node {node} has " +
                        $"applied {history.Count} entries after it caught up with the leader");
                }

                if (history[(int)(write.Index - 1L)] is var entry && entry.Key != write.Key)
                {
                    return new(OnlineHistoryChecker.Durability,
                        $"'{write.Key}' was acknowledged by node {write.Node} at index {write.Index}, but node {node} has " +
                        $"applied '{WriteKey.ToPayloadString(entry.Key)}' (term {entry.Term}) there");
                }
            }
        }

        return null;
    }
}
