namespace DotNext.Raft.FaultCampaign.Driver;

// The JSON report (report.json). Property names are serialized in camelCase; the schema version changes when a field
// changes meaning.
internal sealed class CampaignReport
{
    public int SchemaVersion => 1;
    public string Tool => "DotNext.Raft.FaultCampaign";
    public required DateTimeOffset StartedUtc { get; init; }
    public DateTimeOffset? FinishedUtc { get; set; }
    public required string Command { get; init; }
    public required string Transport { get; init; }
    public required int Seed { get; init; }
    public required string Injection { get; init; }
    public required string[] Schedule { get; init; }
    public required CampaignBounds Bounds { get; init; }
    public required NodeSettings NodeSettings { get; init; }
    public required EnvironmentInfo Environment { get; init; }
    public List<EpisodeReport> Episodes { get; } = [];
    public WorkloadReport Workload { get; } = new();
    public List<NodeReport> Nodes { get; } = [];
    public SignalReport Signals { get; } = new();

    // The proxies in front of the nodes' Raft ports, through which every peer connection passes.
    public ProxyStatistics? Proxy { get; set; }
    public ViolationReport? Violation { get; set; }
    public string? LivenessFailure { get; set; }

    // Why the schedule did not run in full (exit code 5): the run deadline, free disk, or a fault that did not take effect.
    public string? Incomplete { get; set; }
    public string? Error { get; set; }
    public string Verdict { get; set; } = "error";
    public int ExitCode { get; set; } = 1;
}

internal sealed class CampaignBounds
{
    public required int Nodes { get; init; }
    public required double MaxDurationMinutes { get; init; }
    public required double RecoveryTimeoutSeconds { get; init; }
    public required long WarmupEntries { get; init; }
    public required int MinAcknowledgedAfterRecovery { get; init; }
    public required int Clients { get; init; }
    public required int PayloadBytes { get; init; }
    public required int SnapshotInterval { get; init; }
}

internal sealed class NodeSettings
{
    public required string Host { get; init; }
    public required string Storage { get; init; }
    public required int LowerElectionTimeoutMs { get; init; }
    public required int UpperElectionTimeoutMs { get; init; }
    public required double RequestTimeoutSeconds { get; init; }
    public required double ReplicateTimeoutSeconds { get; init; }
}

internal sealed class EpisodeReport
{
    public required int Number { get; init; }
    public required string Fault { get; init; }
    public required int HoldMs { get; init; }
    public int? LeaderBefore { get; set; }
    public long? TermBefore { get; set; }
    public int[] Victims { get; set; } = [];

    // Lagging-snapshot only: the follower's last log index when it was killed, and the leader's snapshot index when
    // it was restarted.
    public long? VictimLastEntryIndex { get; set; }
    public long? LeaderSnapshotIndexAtRestart { get; set; }
    public int? SnapshotsInstalled { get; set; }

    // Partition episodes only. CutPeers: the peers that lost contact with the isolated node (the victim). MajorityLeader
    // and MajorityTerm: the leader of the majority side before the hold. StepDownSeconds: from the cut until the
    // isolated leader stopped reporting that it leads, if it did before the heal. NewLeaderAtHeal: the majority side had
    // a leader in a new term when the partition healed. MinorityWrites: the writes sent to the isolated node while it
    // was cut, by outcome.
    public int[]? CutPeers { get; set; }
    public int? MajorityLeader { get; set; }
    public long? MajorityTerm { get; set; }
    public long? AcknowledgedDuringPartition { get; set; }
    public double? StepDownSeconds { get; set; }
    public bool? NewLeaderAtHeal { get; set; }
    public double? PartitionSeconds { get; set; }
    public MinorityWriteCounts? MinorityWrites { get; set; }
    public int? LeaderAfter { get; set; }
    public long? TermAfter { get; set; }
    public double? RecoverySeconds { get; set; }
    public long? CommitIndexAtCheckpoint { get; set; }
    public long AcknowledgedTotal { get; set; }
    public string Outcome { get; set; } = "not run";
}

internal sealed class WorkloadReport
{
    public long Acknowledged { get; set; }
    public long Rejected { get; set; }
    public long Unknown { get; set; }
}

internal sealed class NodeReport
{
    public required int Id { get; init; }
    public required int Incarnations { get; init; }
    public required int HistoryReplacements { get; init; }
    public required long AppliedEntries { get; init; }
    public required string[] Logs { get; init; }
    public required string[] UnexpectedExits { get; init; }

    // Failed background snapshots (HistoryStateMachine.OnSnapshotFailed), summed over the incarnations of the node.
    public required int SnapshotFailures { get; init; }
}

internal sealed class SignalReport
{
    public int LogLines { get; set; }
    public SortedDictionary<string, int> Expected { get; } = new(StringComparer.Ordinal);
    public SortedDictionary<string, int> Unclassified { get; } = new(StringComparer.Ordinal);
    public SortedDictionary<string, int> Unexpected { get; } = new(StringComparer.Ordinal);

    // Failed background snapshots reported by the nodes' status: unexpected (exit 6), as no injected fault causes one.
    public int SnapshotFailures { get; set; }

    // The first lines of each unexpected or unclassified rule, with their file and line number.
    public List<string> Examples { get; } = [];
}

internal sealed class ViolationReport
{
    public required string Episode { get; init; }
    public required string Oracle { get; init; }
    public required string Message { get; init; }

    internal static ViolationReport Create(string episode, SafetyViolationException e)
    {
        var prefix = e.Oracle + ": ";
        return new()
        {
            Episode = episode,
            Oracle = e.Oracle,
            Message = e.Message.StartsWith(prefix, StringComparison.Ordinal) ? e.Message[prefix.Length..] : e.Message,
        };
    }
}
