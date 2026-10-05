using DotNext.Net.Cluster.Consensus.Raft.InProcess;

namespace DotNext.Benchmarks.DurableWrite.Oracles;

/// <summary>
/// The durability oracle: every acknowledged write must be in the durable log of a majority of the voters.
/// </summary>
/// <remarks>
/// A durable log is what a node recovers from its storage alone after the run: the snapshot plus the
/// write-ahead log, reopened with a new state machine. Raft acknowledges a write once a majority has it,
/// so if fewer than a majority recover it, an acknowledged write could be lost when those nodes restart.
/// A node without durable storage contributes an empty log.
/// </remarks>
internal static class DurabilityAudit
{
    /// <summary>
    /// Checks the acknowledged writes against the durable logs.
    /// </summary>
    /// <param name="acknowledged">The acknowledged writes and the index each one was applied at.</param>
    /// <param name="durableLogs">The recovered log of each voter, from index 1 without gaps.</param>
    /// <returns>The first violation, or <see langword="null"/>.</returns>
    internal static SafetyViolationException? Check(IReadOnlyList<AcknowledgedWrite> acknowledged, IReadOnlyList<IReadOnlyList<AppliedEntry>> durableLogs)
    {
        for (var node = 0; node < durableLogs.Count; node++)
        {
            var log = durableLogs[node];
            for (var i = 0; i < log.Count; i++)
            {
                if (log[i].Index != i + 1L)
                {
                    return new(OnlineHistoryChecker.Durability,
                        $"the durable log of node {node} holds index {log[i].Index} at position {i + 1}");
                }
            }
        }

        var majority = durableLogs.Count / 2 + 1;
        foreach (var write in acknowledged)
        {
            var holders = 0;
            if (write.Index > 0L)
            {
                foreach (var log in durableLogs)
                {
                    if (write.Index <= log.Count && log[(int)(write.Index - 1L)].Key == write.Key)
                        holders++;
                }
            }

            if (holders < majority)
            {
                return new(OnlineHistoryChecker.Durability,
                    $"'{write.Key}' was acknowledged by node {write.Node} at index {write.Index}, but only {holders} of " +
                    $"{durableLogs.Count} voters recovered it from storage; a majority is {majority}");
            }
        }

        return null;
    }
}
