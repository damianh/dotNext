using System.Diagnostics;
using System.Globalization;
using System.Text.Json;
using System.Text.Json.Serialization;
using DotNext.Benchmarks.DurableWrite;
using DotNext.Net.Cluster.Consensus.Raft.InProcess;

const int ExitOk = 0, ExitError = 1, ExitUsage = 2, ExitViolation = 3, ExitLiveness = 4, ExitIncomplete = 5;
const long MinFreeBytesPerCell = 2L << 30;

RunOptions options;
try
{
    options = RunOptions.Parse(args);
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

    var cells = Profiles.Build(options);
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
                // The open loop is offered as a fraction of the busiest closed-loop cell with the same voters and entry size.
                var reference = spec.Kind is CellKind.RaftOpen
                    ? report.Cells
                        .Where(c => c.Kind is nameof(CellKind.RaftClosed) && c.Voters == spec.Voters && c.EntrySize == spec.EntrySize && c.CompletedPerSecond > 0D)
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

    static string Us(long? value) => value is { } us ? us >= 10_000L ? $"{us / 1000D:F1}ms" : $"{us}us" : "-";
}
