using DotNext.Benchmarks.DurableWrite.Oracles;
using DotNext.Net.Cluster.Consensus.Raft.InProcess;

namespace DotNext.Raft.FaultCampaign.Driver;

/// <summary>
/// The oracles of a partition episode for the writes sent to the isolated node.
/// </summary>
/// <remarks>
/// A write that reaches the isolated node after its links are cut is appended, if at all, only to its own log: every
/// byte it sends to a peer is dropped. In a strict episode the majority elects a leader in a higher term and commits
/// writes in it before the network heals, so the isolated node cannot be elected again before it truncates that entry,
/// and no leader can ever commit it. Hence such a write is never acknowledged and never applied by any node. An episode
/// that heals before the majority has committed in a new term is not strict: the old leader may still commit, after the
/// heal, the writes it appended during the partition.
/// </remarks>
internal sealed class PartitionOracle
{
    internal const string MinorityAcknowledgment = "minority acknowledgment";
    internal const string MinorityWriteApplied = "minority write applied";

    private readonly Lock sync = new();
    private readonly Dictionary<WriteKey, MinorityWrite> writes = [];
    private Window? active;
    private SafetyViolationException? violation;

    internal SafetyViolationException? Violation => Volatile.Read(in violation);

    /// <summary>
    /// Starts recording the writes sent to <paramref name="isolated"/>. Call after its links are cut.
    /// </summary>
    internal void Begin(int episode, int isolated, bool strict)
    {
        lock (sync)
            active = new(episode, isolated, strict);
    }

    /// <summary>
    /// Stops recording. Call before the network heals.
    /// </summary>
    internal void End()
    {
        lock (sync)
            active = null;
    }

    /// <summary>
    /// Records a write that a client is about to send to <paramref name="target"/>.
    /// </summary>
    internal void OnSubmitting(int target, WriteKey key)
    {
        lock (sync)
        {
            if (active is { } window && window.Isolated == target)
                writes[key] = new(window.Episode, target, window.Strict);
        }
    }

    internal void OnOutcome(WriteKey key, WriteOutcome outcome)
    {
        lock (sync)
        {
            if (!writes.TryGetValue(key, out var write))
                return;

            write.Outcome = outcome;
            if (outcome is WriteOutcome.Acknowledged && write.Strict)
            {
                Report(new(MinorityAcknowledgment,
                    $"node {write.Node} acknowledged {key}, which it received after it was isolated in episode {write.Episode}"));
            }
        }
    }

    /// <summary>
    /// Checks that no node applied a write sent to an isolated node during a strict episode.
    /// </summary>
    internal void Check(IReadOnlyList<IReadOnlyList<AppliedEntry>> histories)
    {
        lock (sync)
        {
            for (var node = 0; node < histories.Count; node++)
            {
                foreach (var entry in histories[node])
                {
                    if (entry.Key is { } key && writes.TryGetValue(key, out var write) && write.Strict)
                    {
                        Report(new(MinorityWriteApplied,
                            $"{key}, sent to node {write.Node} while it was isolated in episode {write.Episode}, is applied at " +
                            $"index {entry.Index} (term {entry.Term}) on node {node}"));
                        return;
                    }
                }
            }
        }
    }

    private void Report(SafetyViolationException e) => Interlocked.CompareExchange(ref violation, e, null);

    /// <summary>
    /// The writes sent to the node isolated in <paramref name="episode"/>, by outcome; a write still in flight counts as unknown.
    /// </summary>
    internal MinorityWriteCounts Count(int episode)
    {
        lock (sync)
        {
            var sent = writes.Values.Where(w => w.Episode == episode).ToArray();
            return new()
            {
                Sent = sent.Length,
                Acknowledged = sent.Count(static w => w.Outcome is WriteOutcome.Acknowledged),
                Rejected = sent.Count(static w => w.Outcome is WriteOutcome.Rejected),
                Unknown = sent.Count(static w => w.Outcome is not (WriteOutcome.Acknowledged or WriteOutcome.Rejected)),
            };
        }
    }

    private sealed record Window(int Episode, int Isolated, bool Strict);

    private sealed class MinorityWrite(int episode, int node, bool strict)
    {
        internal int Episode => episode;
        internal int Node => node;
        internal bool Strict => strict;
        internal WriteOutcome? Outcome { get; set; }
    }
}

internal sealed class MinorityWriteCounts
{
    public int Sent { get; init; }
    public int Acknowledged { get; init; }
    public int Rejected { get; init; }
    public int Unknown { get; init; }
}
