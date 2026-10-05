using System.Diagnostics;
using System.Globalization;
using System.Text.Json;
using System.Text.Json.Serialization;
using DotNext.Benchmarks.DurableWrite;
using DotNext.Net.Cluster.Consensus.Raft.InProcess;

const int ExitOk = 0, ExitError = 1, ExitUsage = 2, ExitViolation = 3, ExitLiveness = 4, ExitIncomplete = 5;
const long MinFreeBytesPerCell = 2L << 30;

RunOptions options;
IReadOnlyList<CellSpec> cells;
try
{
    options = RunOptions.Parse(args);
    cells = Profiles.Build(options);
}
catch (UsageException e)
{
    if (e.Message is { Length: > 0 } message)
        Console.Error.WriteLine(message);

    Console.WriteLine(RunOptions.Usage);
    return e.Message is { Length: > 0 } ? ExitUsage : ExitOk;
}

var runDirectory = Path.Combine(Path.GetFullPath(options.WorkDirectory),
    $"run-{DateTime.UtcNow:yyyyMMdd-HHmmss}-{Environment.ProcessId}");
Directory.CreateDirectory(runDirectory);

var report = new RunReport
{
    StartedUtc = DateTimeOffset.UtcNow,
    Profile = options.Profile.ToString().ToLowerInvariant(),
    Mode = options.Mode.ToString().ToLowerInvariant(),
    Injection = Profiles.InjectionName(options.Injection),
    Bounds = new()
    {
        MaxEntriesPerCell = options.MaxEntries,
        MaxPayloadBytesPerCell = options.MaxPayloadBytes,
        MaxDurationMinutes = options.MaxDuration.TotalMinutes,
    },
    Durability = NodeStorage.Describe(),
    Environment = EnvironmentInfo.Collect(runDirectory),
};

using var runTimeout = new CancellationTokenSource(options.MaxDuration);
Console.CancelKeyPress += (_, e) =>
{
    e.Cancel = true;
    runTimeout.Cancel();
};

var exitCode = ExitOk;
try
{
    var environment = report.Environment;
    Console.WriteLine($"revision {environment.Revision ?? "unknown"}; {environment.Os}; {environment.CpuModel} x{environment.ProcessorCount}; {environment.Runtime}");
    Console.WriteLine($"work directory {runDirectory} ({environment.FileSystem} on {environment.Device})");

    var probe = FsyncProbe.Run(runDirectory);
    report.FsyncProbe = probe;
    Console.WriteLine($"fsync probe: flushed 4 KiB write p50 {probe.Flushed.P50Us} us, p99 {probe.Flushed.P99Us} us; buffered p50 {probe.Buffered.P50Us} us"
        + (probe.Distinguishable ? string.Empty : " (a flush is not distinguishable from a buffered write on this device)"));

    if (options.Diagnostics)
    {
        var syncProbe = SyncProbe.Run(runDirectory);
        report.SyncProbe = syncProbe;
        Console.WriteLine($"sync probe: directory flush p50 {syncProbe.DirectoryFlush.P50Us} us; publish p50 {syncProbe.Publish.P50Us} us; delete+flush p50 {syncProbe.DeleteAndFlush.P50Us} us");
    }

    Console.WriteLine($"{cells.Count} cells, profile {report.Profile}");
    Console.WriteLine();

    foreach (var spec in cells)
    {
        // The matrix was cut short (--max-duration or Ctrl+C): the run did not check what it was asked to.
        if (runTimeout.IsCancellationRequested)
        {
            report.Incomplete = $"max duration reached before {spec.Name}; {cells.Count - report.Cells.Count} of {cells.Count} cells not run";
            exitCode = ExitIncomplete;
            break;
        }

        var cellRoot = Path.Combine(runDirectory, spec.Name);
        using var run = new CellRun(spec, options, cellRoot, runTimeout.Token);
        report.Cells.Add(run.Report);

        if (EnvironmentInfo.GetFreeBytes(runDirectory) is { } free && free < MinFreeBytesPerCell)
        {
            run.Report.StoppedBy = "skipped: free disk";
            report.Incomplete ??= $"{spec.Name} skipped: {free / (1024 * 1024)} MiB free";
            Console.WriteLine($"{spec.Name}: skipped, {free / (1024 * 1024)} MiB free");
            continue;
        }

        var violated = false;
        try
        {
            if (spec.IsReplicated)
            {
                // The open loop is offered as a fraction of the busiest closed-loop cell with the same voters and entry
                // size, in the same --repeat round.
                var reference = spec.Kind is CellKind.RaftOpen
                    ? report.Cells
                        .Where(c => c.Kind is nameof(CellKind.RaftClosed) && c.Voters == spec.Voters && c.EntrySize == spec.EntrySize && c.CompletedPerSecond > 0D
                            && c.Repeat == spec.Repeat)
                        .MaxBy(static c => c.CompletedPerSecond)
                    : null;

                await RaftMode.RunAsync(run, probe, reference is null ? null : (reference.CompletedPerSecond, reference.Name)).ConfigureAwait(false);
            }
            else
            {
                await WalMode.RunAsync(run, probe).ConfigureAwait(false);
            }
        }
        catch (LivenessFailureException e)
        {
            run.Report.StoppedBy = "liveness";
            report.LivenessFailure = $"{spec.Name}: {e.Message}";
            exitCode = int.Max(exitCode, ExitLiveness);
        }
        catch (OperationCanceledException) when (runTimeout.IsCancellationRequested)
        {
            run.Report.StoppedBy = "max-duration";
        }
        finally
        {
            if (run.CompleteOracles() is { } violation)
            {
                violated = true;
                report.Violation ??= new() { Cell = spec.Name, Oracle = violation.Oracle, Message = violation.Message };
                exitCode = ExitViolation;
            }
        }

        // A cell cut short by the run deadline skipped some of its final checks.
        if (run.Report.StoppedBy is "max-duration")
        {
            report.Incomplete ??= $"max duration reached during {spec.Name}; its final checks did not all run";
            exitCode = exitCode is ExitOk ? ExitIncomplete : exitCode;
        }

        PrintCell(run.Report);

        // Keep the data of a failed cell for diagnosis.
        if (!options.KeepData && !violated && exitCode is ExitOk)
            NodeStorage.Delete(cellRoot);

        if (exitCode is not ExitOk)
            break;
    }

    if (options.Repeat > 1)
        report.Repeats = SummarizeRepeats(cells, report.Cells);
}
catch (Exception e)
{
    Console.Error.WriteLine(e);
    exitCode = exitCode is ExitOk ? ExitError : exitCode;
}

if (exitCode is ExitOk && report.Incomplete is not null)
    exitCode = ExitIncomplete;

report.FinishedUtc = DateTimeOffset.UtcNow;
report.ExitCode = exitCode;
Console.WriteLine();
Console.WriteLine(exitCode switch
{
    ExitOk => "OK: every oracle passed",
    ExitViolation => $"SAFETY VIOLATION in {report.Violation?.Cell}: {report.Violation?.Message}",
    ExitLiveness => $"LIVENESS FAILURE: {report.LivenessFailure}",
    ExitIncomplete => $"INCOMPLETE: {report.Incomplete}",
    _ => "ERROR",
});

var json = JsonSerializer.Serialize(report, new JsonSerializerOptions
{
    WriteIndented = true,
    PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
    DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
    Converters = { new JsonStringEnumConverter(JsonNamingPolicy.CamelCase) },
});

if (options.OutputPath is { } output)
{
    Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(output))!);
    await File.WriteAllTextAsync(output, json).ConfigureAwait(false);
    Console.WriteLine($"report: {Path.GetFullPath(output)}");
}

if (!options.KeepData && exitCode is ExitOk)
{
    try
    {
        NodeStorage.Delete(runDirectory);
    }
    catch (Exception e) when (e is IOException or UnauthorizedAccessException)
    {
        Console.Error.WriteLine($"could not delete {runDirectory}: {e.Message}");
    }
}
else
{
    Console.WriteLine($"node data kept in {runDirectory}");
}

return exitCode;

static void PrintCell(CellReport cell)
{
    var ack = cell.AckLatency;
    var line = string.Create(CultureInfo.InvariantCulture,
        $"{cell.Name,-40} done {cell.CompletedPerSecond,8:F0}/s offered {cell.OfferedPerSecond,8:F0}/s ack p50 {Us(ack?.P50Us),8} p99 {Us(ack?.P99Us),8} p99.9 {Us(ack?.P999Us),8}"
        + $" acked {cell.AcknowledgedTotal,7} leaders {cell.LeaderChanges} wa {cell.WriteAmplification?.ClusterFileRatio:F2} [{cell.StoppedBy}]");
    if (cell.SuspectBuffered)
        line += " SUSPECT-BUFFERED";

    if (cell.Oracles.Violation is { } violation)
        line += $" VIOLATION {violation}";

    Console.WriteLine(line);

    if (cell.Diagnostics is { } diagnostics)
    {
        // The leader (or the single WAL) carries the client path; the followers show the group commit of replication.
        var roles = cell.Nodes.ToDictionary(static n => n.Node, static n => n.Role);
        var shown = cell.Nodes.FirstOrDefault(static n => n.Role is "leader" or "single")?.Node ?? 0;
        foreach (var cycle in diagnostics.PersistCycles)
        {
            var role = roles.GetValueOrDefault(cycle.Node, "?");
            Console.WriteLine(string.Create(CultureInfo.InvariantCulture,
                $"    node {cycle.Node} {role,-15} {cycle.Cause,-6} cycles {cycle.Cycles,7} ({cycle.CyclesPerAck:F2}/ack, {cycle.EntriesPerCycle?.ToString("F1", CultureInfo.InvariantCulture) ?? "-"} entries/cycle) {cycle.MsPerAck:F3} ms/ack: ")
                + string.Join(", ", cycle.Phases.Select(static p => string.Create(CultureInfo.InvariantCulture, $"{p.Phase} {Us(p.Duration.P50Us)}"))));
        }

        foreach (var l in diagnostics.Locks.Where(l => l.Node == shown && l.Wait.Count > 0L))
        {
            Console.WriteLine(string.Create(CultureInfo.InvariantCulture,
                $"    node {shown} lock {l.Lock,-12} {l.Cause,-8} n {l.Wait.Count,7} wait p50 {Us(l.Wait.P50Us),8} p99 {Us(l.Wait.P99Us),8} max {Us(l.Wait.MaxUs),8}")
                + (l.Hold is { } hold ? string.Create(CultureInfo.InvariantCulture, $" hold p50 {Us(hold.P50Us),8} p99 {Us(hold.P99Us),8}") : string.Empty));
        }

        if (diagnostics.Raft is { } raft)
        {
            Console.WriteLine(string.Create(CultureInfo.InvariantCulture,
                $"    raft max round gap {raft.MaxBroadcastGapMs:F1} ms, max heartbeat gap {raft.MaxHeartbeatGapMs:F1} ms (election timeout >= {raft.ElectionTimeoutMs:F0} ms), transitions {raft.Nodes.Sum(static n => n.ToLeader + n.ToCandidate + n.ToFollower)}"));
        }

        if (diagnostics.Io is { } io)
        {
            Console.WriteLine(string.Create(CultureInfo.InvariantCulture,
                $"    io {io.Source}: write ops {io.WriteOps}, other ops {io.OtherOps?.ToString(CultureInfo.InvariantCulture) ?? "-"} ({io.OtherOpsPerAck?.ToString("F1", CultureInfo.InvariantCulture) ?? "-"}/ack), device flushes {io.DeviceFlushes?.ToString(CultureInfo.InvariantCulture) ?? "-"} ({io.DeviceFlushesPerAck?.ToString("F1", CultureInfo.InvariantCulture) ?? "-"}/ack)"));
        }
    }

    static string Us(long? value) => value is { } us ? us >= 10_000L ? $"{us / 1000D:F1}ms" : $"{us}us" : "-";
}

static List<RepeatSummary> SummarizeRepeats(IReadOnlyList<CellSpec> specs, IReadOnlyList<CellReport> cells)
{
    var baseNames = specs.ToDictionary(static s => s.Name, static s => s.BaseName, StringComparer.Ordinal);
    var result = new List<RepeatSummary>();
    foreach (var group in cells
        .Where(static c => c.MeasuredSeconds > 0D)
        .GroupBy(c => baseNames.GetValueOrDefault(c.Name, c.Name), StringComparer.Ordinal)
        .Where(static g => g.Count() > 1))
    {
        var rounds = group.ToList();
        var summary = new RepeatSummary
        {
            Cell = group.Key,
            Rounds = rounds.Count,
            CompletedPerSecond = SpreadReport.Of(rounds.Select(static c => c.CompletedPerSecond).ToList()),
            AckP50Us = SpreadReport.Of(rounds.Select(static c => (double)(c.AckLatency?.P50Us ?? 0L)).ToList()),
            AckP99Us = SpreadReport.Of(rounds.Select(static c => (double)(c.AckLatency?.P99Us ?? 0L)).ToList()),
            LeaderChanges = SpreadReport.Of(rounds.Select(static c => (double)c.LeaderChanges).ToList()),
        };

        result.Add(summary);
        Console.WriteLine(string.Create(CultureInfo.InvariantCulture,
            $"spread {summary.Cell,-40} x{summary.Rounds} done/s median {summary.CompletedPerSecond.Median:F0} cv {summary.CompletedPerSecond.CvPercent:F1}% range {summary.CompletedPerSecond.RangePercent:F1}%; ack p50 cv {summary.AckP50Us.CvPercent:F1}%; ack p99 median {summary.AckP99Us.Median:F0}us range {summary.AckP99Us.RangePercent:F1}%; leader changes {summary.LeaderChanges.Min:F0}..{summary.LeaderChanges.Max:F0}"));
    }

    return result;
}
