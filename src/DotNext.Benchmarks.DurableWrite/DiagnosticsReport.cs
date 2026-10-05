namespace DotNext.Benchmarks.DurableWrite;

// The diagnostics of one cell (--diagnostics, #123). Everything covers the measured window only.
internal sealed class CellDiagnostics
{
    // Writes acknowledged in the measured window; the per-ack ratios divide by it.
    public long Acknowledged { get; init; }

    // Counter deltas over the measured window, per node.
    public List<NodeCounterDelta> Nodes { get; } = [];

    // Persist cycles per node and cause. A cycle is one pass through the append persist (cause "append") or the
    // checkpoint flush (cause "flush", "snapshot"); its phases add up to the time spent under the persistence lock.
    public List<PersistCycleReport> PersistCycles { get; } = [];

    // Wait and hold times per node, lock and cause. Hold times are recorded for the persistence lock only.
    public List<LockReport> Locks { get; } = [];

    // Replicated cells only.
    public RaftDiagnostics? Raft { get; set; }

    // Role transitions and leader claims, capped at 500.
    public List<DiagnosticsEvent> Events { get; } = [];
    public long EventsDropped { get; set; }

    // One entry per second of the measured window.
    public List<SecondReport> Seconds { get; } = [];

    public IoReport? Io { get; set; }
}

internal sealed class NodeCounterDelta
{
    public int Node { get; init; }
    public long Appended { get; init; }
    public long Committed { get; init; }
    public long Flushed { get; init; }
}

internal sealed class PersistCycleReport
{
    public int Node { get; init; }
    public required string Cause { get; init; }
    public long Cycles { get; init; }
    public double CyclesPerSecond { get; init; }
    public double CyclesPerAck { get; init; }

    // Entries appended per append cycle, or committed per flush cycle: the effective group commit.
    public double? EntriesPerCycle { get; init; }
    public double TotalMs { get; init; }
    public double MsPerAck { get; init; }
    public List<PhaseReport> Phases { get; init; } = [];
}

internal sealed class PhaseReport
{
    public required string Phase { get; init; }
    public double TotalMs { get; init; }
    public required LatencySummary Duration { get; init; }
}

internal sealed class LockReport
{
    public int Node { get; init; }
    public required string Lock { get; init; }
    public required string Cause { get; init; }
    public required LatencySummary Wait { get; init; }
    public double WaitTotalMs { get; init; }
    public LatencySummary? Hold { get; init; }
    public double? HoldTotalMs { get; init; }
}

internal sealed class RaftDiagnostics
{
    // The lower bound of the randomized election timeout: a follower that hears nothing for longer may start an election.
    public double ElectionTimeoutMs { get; init; }

    // The longest time between two successful broadcast rounds of a leader, and between two heartbeats a follower received.
    public double MaxBroadcastGapMs { get; set; }
    public double MaxHeartbeatGapMs { get; set; }

    // Responses that arrived after the round already had a majority.
    public long LateResponses { get; init; }
    public List<RaftNodeReport> Nodes { get; } = [];

    // Response time of the requests the nodes sent, per message type and remote node.
    public List<ResponseTimeReport> ResponseTime { get; } = [];
}

internal sealed class RaftNodeReport
{
    public int Node { get; init; }

    // Duration of a successful broadcast round, while the node led.
    public LatencySummary? BroadcastTime { get; init; }

    // Time between the ends of two successful broadcast rounds of the same leadership.
    public LatencySummary? BroadcastGap { get; init; }

    // Time between two heartbeats the node received as a follower.
    public LatencySummary? HeartbeatGap { get; init; }
    public long ToLeader { get; init; }
    public long ToCandidate { get; init; }
    public long ToFollower { get; init; }
}

internal sealed class ResponseTimeReport
{
    public required string Message { get; init; }
    public required string Remote { get; init; }
    public required LatencySummary Duration { get; init; }
}

internal sealed class DiagnosticsEvent
{
    public double AtMs { get; init; }
    public int Node { get; init; }
    public required string Kind { get; init; }
    public long? Term { get; init; }
}

internal sealed class SecondReport
{
    public int Second { get; init; }
    public long Acked { get; init; }
    public long MaxUncommitted { get; init; }
    public double MaxBroadcastMs { get; init; }
    public double MaxHeartbeatGapMs { get; init; }
    public double MaxCommitLockWaitMs { get; init; }
    public double MaxPersistenceLockWaitMs { get; init; }
    public long Transitions { get; init; }
}

// Process I/O counters over the measured window; see IoSample for what each source counts.
internal sealed class IoReport
{
    public required string Source { get; init; }
    public string? Device { get; init; }
    public long ReadOps { get; init; }
    public long WriteOps { get; init; }
    public long? OtherOps { get; init; }
    public long WriteBytes { get; init; }
    public long? DeviceWrites { get; init; }
    public long? DeviceFlushes { get; init; }
    public double? OtherOpsPerAck { get; init; }
    public double? DeviceFlushesPerAck { get; init; }
}

// The spread of one cell over the --repeat rounds.
internal sealed class RepeatSummary
{
    public required string Cell { get; init; }
    public int Rounds { get; init; }
    public required SpreadReport CompletedPerSecond { get; init; }
    public required SpreadReport AckP50Us { get; init; }
    public required SpreadReport AckP99Us { get; init; }
    public required SpreadReport LeaderChanges { get; init; }
}

internal sealed class SpreadReport
{
    public double Min { get; init; }
    public double Median { get; init; }
    public double Max { get; init; }
    public double Mean { get; init; }

    // Standard deviation over the mean, in percent.
    public double CvPercent { get; init; }

    // (max - min) / median, in percent.
    public double RangePercent { get; init; }

    internal static SpreadReport Of(IReadOnlyList<double> values)
    {
        if (values.Count is 0)
            return new();

        var sorted = values.Order().ToArray();
        var median = sorted.Length % 2 is 1
            ? sorted[sorted.Length / 2]
            : (sorted[sorted.Length / 2 - 1] + sorted[sorted.Length / 2]) / 2D;
        var mean = sorted.Average();
        var deviation = sorted.Length > 1 ? Math.Sqrt(sorted.Sum(v => (v - mean) * (v - mean)) / (sorted.Length - 1)) : 0D;
        return new()
        {
            Min = sorted[0],
            Median = Math.Round(median, 1),
            Max = sorted[^1],
            Mean = Math.Round(mean, 1),
            CvPercent = mean > 0D ? Math.Round(deviation / mean * 100D, 1) : 0D,
            RangePercent = median > 0D ? Math.Round((sorted[^1] - sorted[0]) / median * 100D, 1) : 0D,
        };
    }
}
