namespace DotNext.Benchmarks.DurableWrite;

// The JSON report. Property names are serialized in camelCase; the schema version changes when a field changes meaning.
internal sealed class RunReport
{
    public int SchemaVersion => 1;
    public string Tool => "DotNext.Benchmarks.DurableWrite";
    public required DateTimeOffset StartedUtc { get; init; }
    public DateTimeOffset? FinishedUtc { get; set; }
    public required string Profile { get; init; }
    public required string Mode { get; init; }
    public required string Injection { get; init; }
    public required RunBounds Bounds { get; init; }
    public required DurabilitySettings Durability { get; init; }
    public required EnvironmentInfo Environment { get; init; }
    public FsyncProbe? FsyncProbe { get; set; }
    public List<CellReport> Cells { get; } = [];
    public ViolationReport? Violation { get; set; }
    public string? LivenessFailure { get; set; }
    public int ExitCode { get; set; }
}

internal sealed class RunBounds
{
    public required long MaxEntriesPerCell { get; init; }
    public required long MaxPayloadBytesPerCell { get; init; }
    public required double MaxDurationMinutes { get; init; }
}

/// <summary>
/// The write-ahead log settings that make an acknowledgment durable, as used by every cell.
/// </summary>
internal sealed class DurabilitySettings
{
    public required string FlushInterval { get; init; }
    public required int ChunkSize { get; init; }
    public required bool NoBuffering { get; init; }
    public required string HashAlgorithm { get; init; }
    public required string AppendPersistence { get; init; }
    public required string Acknowledgment { get; init; }
}

internal sealed class ViolationReport
{
    public required string Cell { get; init; }
    public required string Oracle { get; init; }
    public required string Message { get; init; }
}

internal sealed class CellReport
{
    public required string Name { get; init; }
    public required string Kind { get; init; }
    public required int Voters { get; init; }
    public required int EntrySize { get; init; }
    public required int Concurrency { get; init; }
    public required int BatchSize { get; init; }
    public required string Memory { get; init; }
    public double? OfferedRatePerSecond { get; set; }
    public string? OfferedRateReference { get; set; }
    public required double WarmupSeconds { get; init; }
    public required double DurationSeconds { get; init; }
    public long SnapshotInterval { get; init; }
    public string? Injection { get; init; }

    public double MeasuredSeconds { get; set; }
    public string StoppedBy { get; set; } = "duration";

    // Load, counted in the measured window only.
    public long Offered { get; set; }
    public long Completed { get; set; }
    public long Rejected { get; set; }
    public long Unknown { get; set; }
    public long Overloaded { get; set; }
    public double OfferedPerSecond { get; set; }
    public double CompletedPerSecond { get; set; }
    public double PayloadMiBPerSecond { get; set; }

    // Acknowledged writes over the whole cell, warmup included; every one of them is checked by the oracles.
    public long AcknowledgedTotal { get; set; }
    public int LeaderChanges { get; set; }

    public LatencySummary? AckLatency { get; set; }
    public LatencySummary? AppendLatency { get; set; }
    public LatencySummary? CommitApplyLatency { get; set; }
    public LatencySummary? CheckpointFlushDuration { get; set; }
    public LatencySummary? ApplyDuration { get; set; }
    public LatencySummary? ReplicaApplyLag { get; set; }
    public BacklogReport? Backlog { get; set; }
    public ProcessReport? Process { get; set; }
    public WriteAmplificationReport? WriteAmplification { get; set; }
    public List<NodeReport> Nodes { get; } = [];
    public OracleReport Oracles { get; } = new();

    // The acknowledgment is cheaper than a synchronous flush measured on this device: the numbers may describe buffered writes.
    public bool SuspectBuffered { get; set; }
}

internal sealed class BacklogReport
{
    public long Samples { get; set; }
    public long MaxUncommitted { get; set; }
    public long MaxUnapplied { get; set; }
    public long MaxFollowerLag { get; set; }
    public long MaxInFlight { get; set; }
}

internal sealed class ProcessReport
{
    public double CpuSeconds { get; init; }
    public double CpuCoresUsed { get; init; }
    public double CpuPercentOfMachine { get; init; }
    public int Gen0Collections { get; init; }
    public int Gen1Collections { get; init; }
    public int Gen2Collections { get; init; }
    public double GcPauseMilliseconds { get; init; }
    public long AllocatedBytes { get; init; }
    public long WorkingSetBytes { get; init; }
}

internal sealed class WriteAmplificationReport
{
    // Payload of the writes acknowledged during the whole cell.
    public long PayloadBytes { get; init; }

    // Bytes the write-ahead log of the first durable node (the leader at the start) appended: payload plus per-entry metadata.
    public long LeaderAppendBytes { get; init; }

    public long ClusterAppendBytes { get; init; }

    // Length of every file under the node directories at the end of the cell (log chunks, metadata, checkpoints, snapshots).
    public long ClusterFileBytes { get; init; }

    public double LeaderAppendRatio { get; init; }
    public double ClusterAppendRatio { get; init; }
    public double ClusterFileRatio { get; init; }
}

internal sealed class NodeReport
{
    public required int Node { get; init; }
    public required string Role { get; init; }
    public required bool Durable { get; init; }
    public long Appended { get; set; }
    public long Flushed { get; set; }
    public long Committed { get; set; }
    public long Applied { get; set; }
    public long AppendBytes { get; set; }
    public long DeletedBytes { get; set; }
    public long FileBytes { get; set; }
    public long LastIndex { get; set; }
    public int Snapshots { get; set; }
    public int SnapshotFailures { get; set; }
    public int Restores { get; set; }
    public long RecoveredEntries { get; set; }
    public long? RelayedBytes { get; set; }
}

internal sealed class OracleReport
{
    public List<string> Checked { get; } = [];
    public long AcknowledgedChecked { get; set; }
    public long DurableEntriesAudited { get; set; }
    public string? Violation { get; set; }
}
