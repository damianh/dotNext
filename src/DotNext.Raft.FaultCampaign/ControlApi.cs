using System.Text.Json;
using System.Text.Json.Serialization;
using DotNext.Benchmarks.DurableWrite.Oracles;

namespace DotNext.Raft.FaultCampaign;

internal enum Transport
{
    Http,
    Tcp,
}

/// <summary>
/// A test-only failure, to show that the campaign's oracles catch it. A node injects the first two into itself; the
/// driver's proxies inject the last one.
/// </summary>
internal enum NodeInjection
{
    None = 0,

    // The state machine drops the first keyed entry at index 100 or above (HistoryStateMachine, FailureInjection.DropApplied).
    DropApplied,

    // The node deletes its data directory every time it starts, so it forgets its log, term and vote.
    VolatileStorage,

    // A partition leaks: isolating a node leaves its link to one peer intact, so the "isolated" node keeps a majority.
    PartitionLeak,
}

/// <summary>
/// The control API between the driver and a node process. It is plain HTTP/JSON on a loopback port separate from Raft.
/// </summary>
internal static class ControlApi
{
    internal const string Write = "/write";
    internal const string Status = "/status";
    internal const string History = "/history";

    // The write outcome, as an HTTP status code.
    internal const int Acknowledged = 200;
    internal const int Rejected = 409; // NotLeaderException without a cause: not appended anywhere
    internal const int Unknown = 503;  // the leadership was lost, or the write failed or timed out: it may be committed later

    internal static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web)
    {
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
    };

    internal static long[] Encode(in AppliedEntry entry)
        => entry.Key is { } key
            ? [entry.Index, entry.Term, key.Mode, key.Client, key.Seq]
            : [entry.Index, entry.Term, -1L, 0L, 0L];

    internal static AppliedEntry Decode(long[] record)
    {
        if (record is not [var index, var term, var mode, var client, var seq])
            throw new FormatException($"a history record has {record.Length} fields, not 5");

        return new(index, term, mode < 0L ? null : new WriteKey(checked((byte)mode), checked((int)client), seq));
    }
}

internal enum WriteOutcome
{
    Acknowledged,
    Rejected,
    Unknown,
}

internal sealed class NodeStatus
{
    public required int Id { get; init; }
    public required int Pid { get; init; }
    public required Guid Incarnation { get; init; }
    public required long Term { get; init; }
    public required bool IsLeader { get; init; }
    public string? Leader { get; init; }
    public required long LastEntryIndex { get; init; }
    public required long CommitIndex { get; init; }
    public required long AppliedIndex { get; init; }
    public required long SnapshotIndex { get; init; }

    // Snapshots restored by the state machine; the first RestoresAtStartup were restored from its own disk at startup,
    // the rest were installed from the leader.
    public required int Restores { get; init; }
    public required int RestoresAtStartup { get; init; }
    public required int Snapshots { get; init; }
    public required int SnapshotFailures { get; init; }
}

/// <summary>
/// Applied entries of one node from <see cref="From"/> (zero-based position). The history of a node is append-only
/// within one incarnation (process) and epoch (count of snapshot restores); when either changes, the page starts at 0.
/// </summary>
internal sealed class HistoryPage
{
    public required Guid Incarnation { get; init; }
    public required int Epoch { get; init; }
    public required int From { get; init; }
    public required int Total { get; init; }
    public required long[][] Entries { get; init; }
}
