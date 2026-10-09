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

    // The recorded writes without an outcome; drained completes when there are none; healed is the gate that new
    // writes to the isolated node wait at while the partition ends.
    private int pending;
    private TaskCompletionSource? drained, healed;

    internal SafetyViolationException? Violation => Volatile.Read(in violation);

    /// <summary>
    /// Cuts the links of <paramref name="isolated"/> and starts recording the writes sent to it, atomically with respect
    /// to <see cref="OnSubmittingAsync"/>: a write is recorded if and only if it is submitted after the cut.
    /// </summary>
    internal T Begin<T>(int episode, int isolated, bool strict, Func<T> cut)
    {
        lock (sync)
        {
            var result = cut();
            active = new(episode, isolated, strict);
            return result;
        }
    }

    /// <summary>
    /// Heals the network once every recorded write has its outcome, so that each of them was received, handled and
    /// answered by the isolated node while it was cut. Meanwhile, new writes to the isolated node wait for the heal and
    /// are not recorded. A write still without an outcome after <paramref name="timeout"/> is only counted.
    /// </summary>
    /// <returns>The number of writes still without an outcome at the heal.</returns>
    internal async Task<int> EndAsync(Action heal, TimeSpan timeout, CancellationToken token)
    {
        Task drain;
        lock (sync)
        {
            healed = new(TaskCreationOptions.RunContinuationsAsynchronously);
            drained = new(TaskCreationOptions.RunContinuationsAsynchronously);
            if (pending is 0)
                drained.SetResult();

            drain = drained.Task;
        }

        try
        {
            await drain.WaitAsync(timeout, token).ConfigureAwait(false);
        }
        catch (Exception e) when (e is TimeoutException or OperationCanceledException)
        {
            // heal anyway
        }

        lock (sync)
        {
            var late = 0;
            foreach (var write in writes.Values)
            {
                if (write.Pending)
                {
                    write.Pending = false;
                    write.Strict = false;
                    late++;
                }
            }

            pending = 0;
            active = null;
            heal();
            healed.SetResult();
            healed = null;
            drained = null;
            return late;
        }
    }

    /// <summary>
    /// Records a write that a client is about to send to <paramref name="target"/>. While the partition is ending, a
    /// write to the isolated node waits for the heal and is not recorded.
    /// </summary>
    internal ValueTask OnSubmittingAsync(int target, WriteKey key, CancellationToken token)
    {
        Task gate;
        lock (sync)
        {
            if (active is not { } window || window.Isolated != target)
                return ValueTask.CompletedTask;

            if (healed is null)
            {
                writes[key] = new(window.Episode, target, window.Strict) { Pending = true };
                pending++;
                return ValueTask.CompletedTask;
            }

            gate = healed.Task;
        }

        return new(gate.WaitAsync(token));
    }

    internal void OnOutcome(WriteKey key, WriteOutcome outcome)
    {
        lock (sync)
        {
            if (!writes.TryGetValue(key, out var write))
                return;

            write.Outcome = outcome;
            if (write.Pending)
            {
                write.Pending = false;
                if (--pending is 0)
                    drained?.TrySetResult();
            }

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
        internal bool Strict { get; set; } = strict;
        internal bool Pending { get; set; }
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
