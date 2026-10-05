using System.Collections.Concurrent;
using System.Diagnostics;
using System.Diagnostics.Metrics;

namespace DotNext.Benchmarks.DurableWrite;

/// <summary>
/// Listens to the "DotNext.IO.WriteAheadLog" meter for the duration of one cell.
/// </summary>
/// <remarks>
/// Every write-ahead log in the cell is created with a <c>node</c> measurement tag, so the counters are kept per node.
/// The flush and apply duration histograms are not tagged by the write-ahead log; they cover all the nodes of the cell.
/// </remarks>
internal sealed class WalMeterListener : IDisposable
{
    internal const string MeterName = "DotNext.IO.WriteAheadLog";
    internal const string NodeTag = "node";

    private readonly MeterListener listener = new();
    private readonly ConcurrentDictionary<int, NodeCounters> nodes = new();
    private volatile bool measuring;

    internal WalMeterListener()
    {
        listener.InstrumentPublished = static (instrument, listener) =>
        {
            if (instrument.Meter.Name is MeterName)
                listener.EnableMeasurementEvents(instrument);
        };

        listener.SetMeasurementEventCallback<long>(OnMeasurement);
        listener.SetMeasurementEventCallback<double>(OnMeasurement);
        listener.Start();
    }

    // The checkpoint flush of the background flusher, across all nodes, in the measured window.
    internal LatencyHistogram CheckpointFlushDuration { get; } = new();

    // The time each batch of committed entries took to apply, across all nodes, in the measured window.
    internal LatencyHistogram ApplyDuration { get; } = new();

    /// <summary>
    /// Starts or stops recording the durations; the counters always record, because the oracles need totals.
    /// </summary>
    internal bool Measuring
    {
        set => measuring = value;
    }

    internal NodeCounters this[int node] => nodes.GetOrAdd(node, static _ => new());

    private void OnMeasurement(Instrument instrument, long measurement, ReadOnlySpan<KeyValuePair<string, object?>> tags, object? state)
    {
        if (GetNode(tags) is not { } node)
            return;

        var counters = this[node];
        switch (instrument.Name)
        {
            case "entries-append-count":
                Interlocked.Add(ref counters.Appended, measurement);
                break;
            case "entries-flush-count":
                Interlocked.Add(ref counters.Flushed, measurement);
                break;
            case "entries-commit-count":
                Interlocked.Add(ref counters.Committed, measurement);
                break;
            case "entries-apply-count":
                Interlocked.Add(ref counters.Applied, measurement);
                break;
            case "entries-append-bytes":
                Interlocked.Add(ref counters.AppendBytes, measurement);
                break;
            case "entries-deleted-bytes":
                Interlocked.Add(ref counters.DeletedBytes, measurement);
                break;
        }
    }

    private void OnMeasurement(Instrument instrument, double measurement, ReadOnlySpan<KeyValuePair<string, object?>> tags, object? state)
    {
        if (!measuring)
            return;

        switch (instrument.Name)
        {
            case "entries-flush-duration":
                CheckpointFlushDuration.RecordMilliseconds(measurement);
                break;
            case "entries-apply-duration":
                ApplyDuration.RecordMilliseconds(measurement);
                break;
        }
    }

    private static int? GetNode(ReadOnlySpan<KeyValuePair<string, object?>> tags)
    {
        foreach (var (key, value) in tags)
        {
            if (key is NodeTag && value is int node)
                return node;
        }

        return null;
    }

    public void Dispose() => listener.Dispose();
}

internal sealed class NodeCounters
{
    internal long Appended, Flushed, Committed, Applied, AppendBytes, DeletedBytes;

    internal long Read(ref long field) => Interlocked.Read(ref field);
}

/// <summary>
/// CPU and GC of this process over the measured window.
/// </summary>
internal readonly struct ProcessSample
{
    private readonly TimeSpan cpu, gcPause;
    private readonly long timestamp, allocated;
    private readonly int gen0, gen1, gen2;

    private ProcessSample(TimeSpan cpu, long timestamp)
    {
        this.cpu = cpu;
        this.timestamp = timestamp;
        gcPause = GC.GetTotalPauseDuration();
        allocated = GC.GetTotalAllocatedBytes();
        gen0 = GC.CollectionCount(0);
        gen1 = GC.CollectionCount(1);
        gen2 = GC.CollectionCount(2);
    }

    internal static ProcessSample Take()
    {
        using var process = Process.GetCurrentProcess();
        return new(process.TotalProcessorTime, Stopwatch.GetTimestamp());
    }

    internal static ProcessReport Delta(in ProcessSample start, in ProcessSample end)
    {
        var elapsed = Stopwatch.GetElapsedTime(start.timestamp, end.timestamp).TotalSeconds;
        var cpuSeconds = (end.cpu - start.cpu).TotalSeconds;
        using var process = Process.GetCurrentProcess();
        return new()
        {
            CpuSeconds = Math.Round(cpuSeconds, 3),
            CpuCoresUsed = elapsed > 0D ? Math.Round(cpuSeconds / elapsed, 3) : 0D,
            CpuPercentOfMachine = elapsed > 0D ? Math.Round(cpuSeconds / elapsed / Environment.ProcessorCount * 100D, 1) : 0D,
            Gen0Collections = end.gen0 - start.gen0,
            Gen1Collections = end.gen1 - start.gen1,
            Gen2Collections = end.gen2 - start.gen2,
            GcPauseMilliseconds = Math.Round((end.gcPause - start.gcPause).TotalMilliseconds, 3),
            AllocatedBytes = end.allocated - start.allocated,
            WorkingSetBytes = process.WorkingSet64,
        };
    }
}

internal static class DiskUsage
{
    internal static long GetFileBytes(string directory)
    {
        if (!Directory.Exists(directory))
            return 0L;

        var total = 0L;
        foreach (var file in new DirectoryInfo(directory).EnumerateFiles("*", SearchOption.AllDirectories))
        {
            try
            {
                total += file.Length;
            }
            catch (IOException)
            {
                // deleted by a compaction meanwhile
            }
        }

        return total;
    }
}
