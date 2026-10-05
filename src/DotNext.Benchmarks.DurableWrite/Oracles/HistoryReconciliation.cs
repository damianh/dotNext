using DotNext.Net.Cluster.Consensus.Raft.InProcess;

namespace DotNext.Benchmarks.DurableWrite.Oracles;

/// <summary>
/// Reconciles what a node's state machine reported with what its own write-ahead log applied and recovered.
/// </summary>
/// <remarks>
/// The online checks see only the entries a state machine reports. If a node silently loses its last applied entries,
/// no later callback exposes the gap, the log still reports them as applied, and another node holds the acknowledged
/// write; so each node's history is checked against its own log:
/// <list type="bullet">
/// <item>after the catch-up, while the node runs: every index the log applied past the end of the history must be an
/// entry without a write (a no-op, which the log does not pass to the state machine);</item>
/// <item>after the node is stopped: the log it recovers from its storage alone must hold every entry it applied,
/// with the same write at the same index.</item>
/// </list>
/// </remarks>
internal static class HistoryReconciliation
{
    /// <summary>
    /// Checks the end of a node's history against the entries its log applied.
    /// </summary>
    /// <param name="node">The node.</param>
    /// <param name="history">The entries the state machine reported, from index 1.</param>
    /// <param name="appliedIndex">The last index the log applied, read before <paramref name="history"/>.</param>
    /// <param name="walTail">The log entries after the end of <paramref name="history"/>, up to <paramref name="appliedIndex"/>.</param>
    /// <returns>The violation, or <see langword="null"/>.</returns>
    internal static SafetyViolationException? CheckApplied(int node, IReadOnlyList<AppliedEntry> history, long appliedIndex,
        IReadOnlyList<AppliedEntry> walTail)
    {
        var expected = history.Count + 1L;
        foreach (var entry in walTail)
        {
            if (entry.Index > appliedIndex)
                break;

            if (entry.Index != expected)
            {
                return new(OnlineHistoryChecker.Reconciliation,
                    $"the log of node {node} returned index {entry.Index} where index {expected} was expected");
            }

            if (entry.Key is { } key)
            {
                return new(OnlineHistoryChecker.Reconciliation,
                    $"the log of node {node} applied '{key}' at index {entry.Index}, but its state machine reported only " +
                    $"{history.Count} entries");
            }

            expected++;
        }

        return expected > appliedIndex
            ? null
            : new(OnlineHistoryChecker.Reconciliation,
                $"the log of node {node} applied {appliedIndex} entries, but only indexes up to {expected - 1L} could be read back");
    }

    /// <summary>
    /// Checks a node's history against the log it recovered from its storage after it stopped.
    /// </summary>
    /// <param name="node">The node.</param>
    /// <param name="history">The entries the state machine reported, from index 1.</param>
    /// <param name="recovered">The recovered log, from index 1 without gaps; see <c>NodeStorage.RecoverAsync</c>.</param>
    /// <returns>The violation, or <see langword="null"/>.</returns>
    internal static SafetyViolationException? CheckRecovered(int node, IReadOnlyList<AppliedEntry> history, IReadOnlyList<AppliedEntry> recovered)
    {
        if (recovered.Count < history.Count)
        {
            return new(OnlineHistoryChecker.Reconciliation,
                $"node {node} applied {history.Count} entries but recovered only {recovered.Count} from its storage");
        }

        for (var i = 0; i < history.Count; i++)
        {
            var applied = history[i];
            var durable = recovered[i];
            if (applied.Key != durable.Key || (applied.Key is not null && applied.Term != durable.Term))
            {
                return new(OnlineHistoryChecker.Reconciliation,
                    $"node {node} applied index {i + 1} as (term {applied.Term}, '{WriteKey.ToPayloadString(applied.Key)}') " +
                    $"but recovered it as (term {durable.Term}, '{WriteKey.ToPayloadString(durable.Key)}')");
            }
        }

        return null;
    }
}
