using System.Text;
using System.Text.Json;

namespace DotNext.Raft.FaultCampaign.Driver;

internal enum SignalClass
{
    // A documented signal of a fault the campaign injects, such as a request to a killed peer.
    Expected,

    // A documented signal of a failure the campaign does not inject: the run fails with exit code 6.
    Unexpected,

    // A warning or error that RAFT-REVIEW "Failure signals and operator actions (#26)" does not list. It is reported, not failed.
    Unclassified,
}

internal readonly record struct Signal(SignalClass Class, string Rule, string File, int Line, string Text);

/// <summary>
/// Classifies the log lines of the node processes by the failure signals documented in RAFT-REVIEW.md,
/// "Failure signals and operator actions (#26)". A node logs one JSON object per line (the JSON console formatter).
/// </summary>
internal sealed class LogClassifier
{
    // LogMessages ids (offset 74000) that no injected fault explains: zombie and failure-induced standby transitions,
    // supervised worker failures, leader-local read failures, malformed peer input, and the failure detector, which
    // the campaign does not configure.
    private static readonly Dictionary<int, string> UnexpectedEvents = new()
    {
        [74028] = "74028 FailedToProcessRequest (malformed peer input)",
        [74030] = "74030 TransitionToFollowerStateFailed (zombie)",
        [74031] = "74031 TransitionToCandidateStateFailed (zombie)",
        [74032] = "74032 TransitionToLeaderStateFailed (failure-induced standby)",
        [74035] = "74035 LeaderStateExitedWithError",
        [74037] = "74037 UnresponsiveMemberDetected",
        [74048] = "74048 VotingFailed",
        [74049] = "74049 LocalLogReadFailed",
        [75002] = "75002 UnhandledException (HTTP 500, malformed peer input)",
    };

    // A peer that is down or restarting: the request to it fails and replication is retried (#26).
    private static readonly Dictionary<int, string> ExpectedEvents = new()
    {
        [74010] = "74010 MemberUnavailable",
        [74015] = "74015 ReplicationFailed (retried)",
        [75001] = "75001 MemberUnavailable (HTTP)",
    };

    // Integrity failure at open or a terminal WAL failure, wherever it surfaces.
    private static readonly string[] IntegrityMarkers =
    [
        "IntegrityException",
        "HashMismatchException",
        "MissingPageException",
        "WriteAheadLog+InternalException",
    ];

    private readonly Dictionary<string, long> offsets = new(StringComparer.Ordinal);
    private readonly Dictionary<string, int> lineNumbers = new(StringComparer.Ordinal);

    internal List<Signal> Signals { get; } = [];

    internal int Lines { get; private set; }

    internal IEnumerable<Signal> Unexpected => Signals.Where(static s => s.Class is SignalClass.Unexpected);

    /// <summary>
    /// Classifies the complete lines appended to a log file since the last scan.
    /// </summary>
    internal void Scan(string path)
    {
        if (!File.Exists(path))
            return;

        using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
        var offset = offsets.GetValueOrDefault(path);
        if (offset >= stream.Length)
            return;

        stream.Position = offset;
        var buffer = new byte[stream.Length - offset];
        stream.ReadExactly(buffer);

        // A line still being written has no newline yet; it is classified on the next scan.
        var end = Array.LastIndexOf(buffer, (byte)'\n');
        if (end < 0)
            return;

        offsets[path] = offset + end + 1;
        var number = lineNumbers.GetValueOrDefault(path);
        foreach (var line in Encoding.UTF8.GetString(buffer, 0, end).Split('\n'))
        {
            number++;
            if (Classify(line) is { } signal)
                Signals.Add(signal with { File = Path.GetFileName(path), Line = number });
        }

        lineNumbers[path] = number;
        Lines = lineNumbers.Values.Sum();
    }

    internal static Signal? Classify(string line)
    {
        if (string.IsNullOrWhiteSpace(line))
            return null;

        var text = line.Length > 2000 ? line[..2000] : line;
        if (!line.StartsWith('{'))
        {
            // Not from the logger: the runtime's report of an unhandled exception, or a stack trace that follows it.
            if (line.StartsWith("Unhandled exception", StringComparison.Ordinal))
                return new(SignalClass.Unexpected, "unhandled exception", "", 0, text);

            return ContainsIntegrityMarker(line) is { } marker
                ? new(SignalClass.Unexpected, marker, "", 0, text)
                : null;
        }

        int eventId;
        string level, category;
        try
        {
            using var json = JsonDocument.Parse(line);
            var root = json.RootElement;
            eventId = root.TryGetProperty("EventId", out var id) && id.ValueKind is JsonValueKind.Number ? id.GetInt32() : 0;
            level = root.TryGetProperty("LogLevel", out var l) ? l.GetString() ?? "" : "";
            category = root.TryGetProperty("Category", out var c) ? c.GetString() ?? "" : "";
        }
        catch (JsonException)
        {
            return new(SignalClass.Unclassified, "unparsed log line", "", 0, text);
        }

        if (UnexpectedEvents.TryGetValue(eventId, out var rule))
            return new(SignalClass.Unexpected, rule, "", 0, text);

        if (ContainsIntegrityMarker(line) is { } integrity)
            return new(SignalClass.Unexpected, integrity, "", 0, text);

        if (ExpectedEvents.TryGetValue(eventId, out rule) && level is "Warning")
            return new(SignalClass.Expected, rule, "", 0, text);

        switch (level)
        {
            case "Critical":
                return new(SignalClass.Unexpected, $"Critical {eventId} {category}", "", 0, text);
            case "Error" or "Warning":
                // A failed request to a peer is logged as EventId 0 and is expected while a peer is down or restarting (#26).
                return eventId is 0 && category.StartsWith("DotNext.", StringComparison.Ordinal)
                    ? new(SignalClass.Expected, $"{level} 0 request to a peer failed", "", 0, text)
                    : new(SignalClass.Unclassified, $"{level} {eventId} {category}", "", 0, text);
            default:
                return null;
        }
    }

    private static string? ContainsIntegrityMarker(string line)
    {
        foreach (var marker in IntegrityMarkers)
        {
            if (line.Contains(marker, StringComparison.Ordinal))
                return marker;
        }

        return null;
    }
}
