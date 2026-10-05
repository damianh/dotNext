using System.Collections.Concurrent;
using System.Diagnostics;
using System.Diagnostics.Metrics;
using System.Globalization;
using System.Runtime.InteropServices;

namespace DotNext.Benchmarks.DurableWrite;

/// <summary>
/// The opt-in breakdown of a cell (<c>--diagnostics</c>, #123): persist cycles and their phases, lock wait and hold
/// times, Raft broadcast rounds and heartbeat gaps, role transitions, a per-second series and process I/O counters.
/// </summary>
/// <remarks>
/// It subscribes to the persist phase and lock histograms of the <c>DotNext.IO.WriteAheadLog</c> meter and to the
/// Raft server and client meters. Without <c>--diagnostics</c> it is never created and none of those instruments has
/// a listener, so the write-ahead log takes no timestamps for them.
/// </remarks>
internal sealed class DiagnosticsCollector : IDisposable
{
    internal const string PersistPhaseInstrument = "persist-phase-duration";
    internal const string LockWaitInstrument = "lock-wait-duration";
    internal const string LockHoldInstrument = "lock-hold-duration";

    private const string RaftServerMeter = "DotNext.Net.Cluster.Consensus.Raft.Server";
    private const string RaftClientMeter = "DotNext.Net.Cluster.Consensus.Raft.Client";
    private const string ServerAddressTag = "dotnext.raft.server.address";
    private const string ClientAddressTag = "dotnext.raft.client.address";
    private const string MessageTypeTag = "dotnext.raft.client.message";
    private const string PhaseTag = "dotnext.wal.phase", CauseTag = "dotnext.wal.cause", LockTag = "dotnext.wal.lock";
    private const int MaxEvents = 500;
    private const int MaxSeconds = 3600;

    // The lower election timeout of the replicated cells, see RaftMode.CreateNodesAsync.
    internal const double ElectionTimeoutMs = 1000D;

    internal static bool IsDiagnosticInstrument(Instrument instrument)
        => instrument.Name is PersistPhaseInstrument or LockWaitInstrument or LockHoldInstrument;

    private readonly MeterListener listener = new();
    private readonly ConcurrentDictionary<(int Node, string Cause, string Phase), LatencyHistogram> phases = new();
    private readonly ConcurrentDictionary<(int Node, string Lock, string Cause), LatencyHistogram> lockWaits = new();
    private readonly ConcurrentDictionary<(int Node, string Lock, string Cause), LatencyHistogram> lockHolds = new();
    private readonly ConcurrentDictionary<(string Message, string Remote), LatencyHistogram> responses = new();
    private readonly ConcurrentDictionary<string, int> endpoints = new(StringComparer.Ordinal);
    private readonly ConcurrentDictionary<int, RaftNode> raftNodes = new();
    private readonly ConcurrentQueue<DiagnosticsEvent> events = new();
    private readonly SecondBucket[] seconds;
    private readonly int voters;
    private long lateResponses, measureStart, eventCount;
    private volatile bool measuring;
    private long[]? appendedAtStart, committedAtStart, flushedAtStart;
    private long[]? appendedAtEnd, committedAtEnd, flushedAtEnd;
    private IoSample? ioStart, ioEnd;
    private readonly string root;

    internal DiagnosticsCollector(CellSpec spec, string root)
    {
        voters = spec.Voters;
        this.root = root;
        seconds = new SecondBucket[(int)Math.Min(MaxSeconds, Math.Ceiling(spec.Duration.TotalSeconds) + 2D)];
        for (var i = 0; i < seconds.Length; i++)
            seconds[i] = new();

        listener.InstrumentPublished = static (instrument, listener) =>
        {
            switch (instrument.Meter.Name)
            {
                case WalMeterListener.MeterName when IsDiagnosticInstrument(instrument):
                case RaftServerMeter:
                case RaftClientMeter:
                    listener.EnableMeasurementEvents(instrument);
                    break;
            }
        };

        listener.SetMeasurementEventCallback<double>(OnMeasurement);
        listener.SetMeasurementEventCallback<int>(OnMeasurement);
        listener.Start();
    }

    /// <summary>
    /// Maps the server address tag of the Raft meters to a node.
    /// </summary>
    internal void RegisterNode(int node, string endpoint) => endpoints[endpoint] = node;

    internal void OnLeaderClaim(int node, long term) => AddEvent(node, "leader-claim", term);

    internal void Begin(WalMeterListener meter)
    {
        (appendedAtStart, committedAtStart, flushedAtStart) = Snapshot(meter);
        ioStart = IoSample.Take(root);
        measureStart = Stopwatch.GetTimestamp();
        measuring = true;
    }

    internal void End(WalMeterListener meter)
    {
        if (!measuring)
            return;

        measuring = false;
        ioEnd = IoSample.Take(root);
        (appendedAtEnd, committedAtEnd, flushedAtEnd) = Snapshot(meter);
    }

    internal void OnAcknowledged(int count)
    {
        if (measuring && Bucket() is { } bucket)
            Interlocked.Add(ref bucket.Acked, count);
    }

    internal void OnBacklog(in BacklogSample sample)
    {
        if (measuring && Bucket() is { } bucket)
            SecondBucket.Max(ref bucket.MaxUncommitted, sample.Uncommitted);
    }

    private (long[], long[], long[]) Snapshot(WalMeterListener meter)
    {
        var appended = new long[voters];
        var committed = new long[voters];
        var flushed = new long[voters];
        for (var node = 0; node < voters; node++)
        {
            var counters = meter[node];
            appended[node] = counters.Read(ref counters.Appended);
            committed[node] = counters.Read(ref counters.Committed);
            flushed[node] = counters.Read(ref counters.Flushed);
        }

        return (appended, committed, flushed);
    }

    private SecondBucket? Bucket()
    {
        var index = (long)Stopwatch.GetElapsedTime(Volatile.Read(in measureStart)).TotalSeconds;
        return index >= 0L && index < seconds.Length ? seconds[index] : null;
    }

    private double ElapsedMs() => Math.Round(Stopwatch.GetElapsedTime(Volatile.Read(in measureStart)).TotalMilliseconds, 1);

    private void AddEvent(int node, string kind, long? term = null)
    {
        if (!measuring || Interlocked.Increment(ref eventCount) > MaxEvents)
            return;

        events.Enqueue(new() { AtMs = ElapsedMs(), Node = node, Kind = kind, Term = term });
    }

    private void OnMeasurement(Instrument instrument, double measurement, ReadOnlySpan<KeyValuePair<string, object?>> tags, object? state)
    {
        if (!measuring)
            return;

        switch (instrument.Name)
        {
            case PersistPhaseInstrument:
                if (GetNode(tags) is { } node && GetString(tags, CauseTag) is { } cause && GetString(tags, PhaseTag) is { } phase)
                    phases.GetOrAdd((node, cause, phase), static _ => new()).RecordMilliseconds(measurement);

                break;
            case LockWaitInstrument:
                if (GetNode(tags) is { } waiter && GetString(tags, LockTag) is { } lockName && GetString(tags, CauseTag) is { } waitCause)
                {
                    lockWaits.GetOrAdd((waiter, lockName, waitCause), static _ => new()).RecordMilliseconds(measurement);
                    if (Bucket() is { } bucket)
                    {
                        if (lockName is "commit")
                            SecondBucket.Max(ref bucket.MaxCommitLockWaitUs, (long)(measurement * 1000D));
                        else if (lockName is "persistence")
                            SecondBucket.Max(ref bucket.MaxPersistenceLockWaitUs, (long)(measurement * 1000D));
                    }
                }

                break;
            case LockHoldInstrument:
                if (GetNode(tags) is { } holder && GetString(tags, LockTag) is { } held && GetString(tags, CauseTag) is { } holdCause)
                    lockHolds.GetOrAdd((holder, held, holdCause), static _ => new()).RecordMilliseconds(measurement);

                break;
            case "broadcast-time":
                if (GetRaftNode(tags) is { } leader)
                {
                    leader.BroadcastTime.RecordMilliseconds(measurement);
                    var now = Stopwatch.GetTimestamp();
                    var previous = Interlocked.Exchange(ref leader.LastBroadcast, now);
                    if (previous is not 0L)
                        leader.BroadcastGap.RecordTicks(now - previous);

                    if (Bucket() is { } bucket)
                        SecondBucket.Max(ref bucket.MaxBroadcastUs, (long)(measurement * 1000D));
                }

                break;
            case "response-time":
                if (GetString(tags, MessageTypeTag) is { } message)
                {
                    var remote = GetString(tags, ClientAddressTag) is { } address && endpoints.TryGetValue(address, out var id)
                        ? id.ToString(CultureInfo.InvariantCulture)
                        : "?";
                    responses.GetOrAdd((message, remote), static _ => new()).RecordMilliseconds(measurement);
                }

                break;
        }
    }

    private void OnMeasurement(Instrument instrument, int measurement, ReadOnlySpan<KeyValuePair<string, object?>> tags, object? state)
    {
        if (!measuring)
            return;

        switch (instrument.Name)
        {
            case "incoming-heartbeats-count":
                if (GetRaftNode(tags) is { } follower)
                {
                    var now = Stopwatch.GetTimestamp();
                    var previous = Interlocked.Exchange(ref follower.LastHeartbeat, now);
                    if (previous is not 0L)
                    {
                        follower.HeartbeatGap.RecordTicks(now - previous);
                        if (Bucket() is { } bucket)
                            SecondBucket.Max(ref bucket.MaxHeartbeatGapUs, (long)Stopwatch.GetElapsedTime(previous, now).TotalMicroseconds);
                    }
                }

                break;
            case "transitions-to-leader-count":
                OnTransition(tags, "leader", static (n, c) => Interlocked.Add(ref n.ToLeader, c), measurement);
                break;
            case "transitions-to-candidate-count":
                OnTransition(tags, "candidate", static (n, c) => Interlocked.Add(ref n.ToCandidate, c), measurement);
                break;
            case "transitions-to-follower-count":
                OnTransition(tags, "follower", static (n, c) => Interlocked.Add(ref n.ToFollower, c), measurement);
                break;
            case "post-quorum-responses":
                Interlocked.Add(ref lateResponses, measurement);
                break;
        }
    }

    private void OnTransition(ReadOnlySpan<KeyValuePair<string, object?>> tags, string kind, Action<RaftNode, int> count, int measurement)
    {
        if (GetRaftNode(tags) is not { } node)
            return;

        count(node, measurement);

        // A gap spans one role only.
        Interlocked.Exchange(ref node.LastHeartbeat, 0L);
        Interlocked.Exchange(ref node.LastBroadcast, 0L);
        if (Bucket() is { } bucket)
            Interlocked.Increment(ref bucket.Transitions);

        AddEvent(node.Id, kind);
    }

    private RaftNode? GetRaftNode(ReadOnlySpan<KeyValuePair<string, object?>> tags)
        => GetString(tags, ServerAddressTag) is { } address && endpoints.TryGetValue(address, out var id)
            ? raftNodes.GetOrAdd(id, static id => new(id))
            : null;

    private static int? GetNode(ReadOnlySpan<KeyValuePair<string, object?>> tags)
    {
        foreach (var (key, value) in tags)
        {
            if (key is WalMeterListener.NodeTag && value is int node)
                return node;
        }

        return null;
    }

    private static string? GetString(ReadOnlySpan<KeyValuePair<string, object?>> tags, string name)
    {
        foreach (var (key, value) in tags)
        {
            if (key == name)
                return value as string;
        }

        return null;
    }

    /// <summary>
    /// Builds the breakdown of the measured window.
    /// </summary>
    /// <param name="acknowledged">The writes acknowledged in the measured window.</param>
    /// <param name="measuredSeconds">The length of the measured window.</param>
    internal CellDiagnostics Complete(long acknowledged, double measuredSeconds)
    {
        var result = new CellDiagnostics { Acknowledged = acknowledged };

        for (var node = 0; node < voters; node++)
        {
            if (appendedAtStart is null || appendedAtEnd is null)
                break;

            result.Nodes.Add(new()
            {
                Node = node,
                Appended = appendedAtEnd[node] - appendedAtStart[node],
                Committed = committedAtEnd![node] - committedAtStart![node],
                Flushed = flushedAtEnd![node] - flushedAtStart![node],
            });
        }

        foreach (var group in phases.GroupBy(static p => (p.Key.Node, p.Key.Cause)).OrderBy(static g => g.Key.Node).ThenBy(static g => g.Key.Cause, StringComparer.Ordinal))
        {
            // Every persist cycle records the pages phase, so its count is the number of cycles.
            var cycles = group.FirstOrDefault(static p => p.Key.Phase is "pages").Value?.Count ?? group.Max(static p => p.Value.Count);
            var totalMs = group.Sum(static p => TotalMs(p.Value));
            var counters = result.Nodes.FirstOrDefault(n => n.Node == group.Key.Node);
            long? entries = group.Key.Cause switch
            {
                "append" => counters?.Appended,
                "flush" => counters?.Committed,
                _ => null,
            };

            result.PersistCycles.Add(new()
            {
                Node = group.Key.Node,
                Cause = group.Key.Cause,
                Cycles = cycles,
                CyclesPerSecond = measuredSeconds > 0D ? Math.Round(cycles / measuredSeconds, 1) : 0D,
                CyclesPerAck = acknowledged > 0L ? Math.Round((double)cycles / acknowledged, 3) : 0D,
                EntriesPerCycle = entries is { } e && cycles > 0L ? Math.Round((double)e / cycles, 2) : null,
                TotalMs = Math.Round(totalMs, 1),
                MsPerAck = acknowledged > 0L ? Math.Round(totalMs / acknowledged, 3) : 0D,
                Phases = group
                    .OrderBy(static p => PhaseOrder(p.Key.Phase))
                    .Select(static p => new PhaseReport { Phase = p.Key.Phase, TotalMs = Math.Round(TotalMs(p.Value), 1), Duration = p.Value.Summarize() })
                    .ToList(),
            });
        }

        foreach (var (key, wait) in lockWaits.OrderBy(static p => p.Key.Node).ThenBy(static p => p.Key.Lock, StringComparer.Ordinal).ThenBy(static p => p.Key.Cause, StringComparer.Ordinal))
        {
            result.Locks.Add(new()
            {
                Node = key.Node,
                Lock = key.Lock,
                Cause = key.Cause,
                Wait = wait.Summarize(),
                WaitTotalMs = Math.Round(TotalMs(wait), 1),
                Hold = lockHolds.TryGetValue(key, out var hold) ? hold.Summarize() : null,
                HoldTotalMs = lockHolds.TryGetValue(key, out hold) ? Math.Round(TotalMs(hold), 1) : null,
            });
        }

        if (!raftNodes.IsEmpty || !responses.IsEmpty)
        {
            var raft = new RaftDiagnostics
            {
                ElectionTimeoutMs = ElectionTimeoutMs,
                LateResponses = Interlocked.Read(ref lateResponses),
            };

            foreach (var (id, node) in raftNodes.OrderBy(static p => p.Key))
            {
                raft.Nodes.Add(new()
                {
                    Node = id,
                    BroadcastTime = node.BroadcastTime.Count > 0L ? node.BroadcastTime.Summarize() : null,
                    BroadcastGap = node.BroadcastGap.Count > 0L ? node.BroadcastGap.Summarize() : null,
                    HeartbeatGap = node.HeartbeatGap.Count > 0L ? node.HeartbeatGap.Summarize() : null,
                    ToLeader = Interlocked.Read(ref node.ToLeader),
                    ToCandidate = Interlocked.Read(ref node.ToCandidate),
                    ToFollower = Interlocked.Read(ref node.ToFollower),
                });
            }

            foreach (var ((message, remote), histogram) in responses.OrderBy(static p => p.Key.Message, StringComparer.Ordinal).ThenBy(static p => p.Key.Remote, StringComparer.Ordinal))
                raft.ResponseTime.Add(new() { Message = message, Remote = remote, Duration = histogram.Summarize() });

            raft.MaxBroadcastGapMs = raft.Nodes.Max(static n => n.BroadcastGap?.MaxUs ?? 0L) / 1000D;
            raft.MaxHeartbeatGapMs = raft.Nodes.Max(static n => n.HeartbeatGap?.MaxUs ?? 0L) / 1000D;
            result.Raft = raft;
        }

        result.Events.AddRange(events.OrderBy(static e => e.AtMs));
        result.EventsDropped = Math.Max(0L, Interlocked.Read(ref eventCount) - MaxEvents);

        var last = (int)Math.Min(seconds.Length, Math.Ceiling(measuredSeconds));
        for (var i = 0; i < last; i++)
        {
            var bucket = seconds[i];
            result.Seconds.Add(new()
            {
                Second = i,
                Acked = Interlocked.Read(ref bucket.Acked),
                MaxUncommitted = Interlocked.Read(ref bucket.MaxUncommitted),
                MaxBroadcastMs = Interlocked.Read(ref bucket.MaxBroadcastUs) / 1000D,
                MaxHeartbeatGapMs = Interlocked.Read(ref bucket.MaxHeartbeatGapUs) / 1000D,
                MaxCommitLockWaitMs = Interlocked.Read(ref bucket.MaxCommitLockWaitUs) / 1000D,
                MaxPersistenceLockWaitMs = Interlocked.Read(ref bucket.MaxPersistenceLockWaitUs) / 1000D,
                Transitions = Interlocked.Read(ref bucket.Transitions),
            });
        }

        if (ioStart is { } start && ioEnd is { } end)
            result.Io = IoSample.Delta(start, end, acknowledged);

        return result;

        static double TotalMs(LatencyHistogram histogram) => histogram.Summarize() is var s ? s.MeanUs * s.Count / 1000D : 0D;
    }

    private static int PhaseOrder(string phase) => phase switch
    {
        "pages" => 0,
        "data-directory" => 1,
        "metadata-directory" => 2,
        "checkpoint-upgrade" => 3,
        "checkpoint-intent" => 4,
        "checkpoint-slot" => 5,
        "checkpoint-commit" => 6,
        _ => 7,
    };

    public void Dispose() => listener.Dispose();

    private sealed class RaftNode(int id)
    {
        internal readonly int Id = id;
        internal readonly LatencyHistogram BroadcastTime = new(), BroadcastGap = new(), HeartbeatGap = new();
        internal long LastBroadcast, LastHeartbeat, ToLeader, ToCandidate, ToFollower;
    }

    private sealed class SecondBucket
    {
        internal long Acked, MaxUncommitted, MaxBroadcastUs, MaxHeartbeatGapUs, MaxCommitLockWaitUs, MaxPersistenceLockWaitUs, Transitions;

        internal static void Max(ref long location, long value)
        {
            for (var current = Volatile.Read(in location); value > current;)
            {
                var actual = Interlocked.CompareExchange(ref location, value, current);
                if (actual == current)
                    break;

                current = actual;
            }
        }
    }
}

/// <summary>
/// I/O counters of this process, and on Linux the completed requests of the block device under the work directory.
/// </summary>
/// <remarks>
/// On Windows, <c>GetProcessIoCounters</c> counts read, write and other I/O calls of the process; the other operations
/// include, but are not limited to, flushes. On Linux, <c>/proc/self/io</c> counts read and write system calls, and
/// <c>/proc/diskstats</c> counts the write and flush requests the device completed, for every process on the host.
/// Neither attributes a request to a phase; the persist phase histograms do that.
/// </remarks>
internal readonly record struct IoSample(string Source, long ReadOps, long WriteOps, long OtherOps, long WriteBytes,
    long? DeviceWrites, long? DeviceFlushes, string? DeviceName)
{
    internal static IoSample? Take(string directory)
    {
        try
        {
            if (OperatingSystem.IsWindows())
            {
                using var process = Process.GetCurrentProcess();
                return GetProcessIoCounters(process.Handle, out var counters)
                    ? new("GetProcessIoCounters", (long)counters.ReadOperationCount, (long)counters.WriteOperationCount,
                        (long)counters.OtherOperationCount, (long)counters.WriteTransferCount, null, null, null)
                    : null;
            }

            if (OperatingSystem.IsLinux() && File.Exists("/proc/self/io"))
            {
                long syscr = 0L, syscw = 0L, writeBytes = 0L;
                foreach (var line in File.ReadLines("/proc/self/io"))
                {
                    var parts = line.Split(':', 2, StringSplitOptions.TrimEntries);
                    if (parts.Length is not 2 || !long.TryParse(parts[1], NumberStyles.None, CultureInfo.InvariantCulture, out var value))
                        continue;

                    switch (parts[0])
                    {
                        case "syscr":
                            syscr = value;
                            break;
                        case "syscw":
                            syscw = value;
                            break;
                        case "write_bytes":
                            writeBytes = value;
                            break;
                    }
                }

                var (name, writes, flushes) = ReadDiskStats(directory);
                return new("/proc/self/io+/proc/diskstats", syscr, syscw, 0L, writeBytes, writes, flushes, name);
            }
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException or InvalidOperationException)
        {
            // not obtainable
        }

        return null;
    }

    internal static IoReport Delta(in IoSample start, in IoSample end, long acknowledged)
    {
        var other = end.OtherOps - start.OtherOps;
        var deviceFlushes = end.DeviceFlushes - start.DeviceFlushes;
        var deviceWrites = end.DeviceWrites - start.DeviceWrites;
        return new()
        {
            Source = end.Source,
            Device = end.DeviceName,
            ReadOps = end.ReadOps - start.ReadOps,
            WriteOps = end.WriteOps - start.WriteOps,
            OtherOps = OperatingSystem.IsWindows() ? other : null,
            WriteBytes = end.WriteBytes - start.WriteBytes,
            DeviceWrites = deviceWrites,
            DeviceFlushes = deviceFlushes,
            OtherOpsPerAck = OperatingSystem.IsWindows() && acknowledged > 0L ? Math.Round((double)other / acknowledged, 2) : null,
            DeviceFlushesPerAck = deviceFlushes is { } f && acknowledged > 0L ? Math.Round((double)f / acknowledged, 2) : null,
        };
    }

    // The block device of the mount that holds the directory, matched by its major:minor in /proc/self/mountinfo.
    private static (string?, long?, long?) ReadDiskStats(string directory)
    {
        if (!File.Exists("/proc/self/mountinfo") || !File.Exists("/proc/diskstats"))
            return (null, null, null);

        var path = Path.GetFullPath(directory);
        string? device = null, mountPoint = null;
        foreach (var line in File.ReadLines("/proc/self/mountinfo"))
        {
            var fields = line.Split(' ');
            if (fields.Length < 5)
                continue;

            var point = fields[4].Replace("\\040", " ", StringComparison.Ordinal);
            if ((point is "/" || path == point || path.StartsWith(point + "/", StringComparison.Ordinal))
                && (mountPoint is null || point.Length > mountPoint.Length))
            {
                mountPoint = point;
                device = fields[2];
            }
        }

        if (device is null)
            return (null, null, null);

        foreach (var line in File.ReadLines("/proc/diskstats"))
        {
            var fields = line.Split(' ', StringSplitOptions.RemoveEmptyEntries);
            if (fields.Length < 14 || $"{fields[0]}:{fields[1]}" != device)
                continue;

            // Field 8 is writes completed; field 18, from Linux 5.5, is flush requests completed.
            long? writes = long.TryParse(fields[7], NumberStyles.None, CultureInfo.InvariantCulture, out var w) ? w : null;
            long? flushes = fields.Length >= 19 && long.TryParse(fields[17], NumberStyles.None, CultureInfo.InvariantCulture, out var f) ? f : null;
            return (fields[2], writes, flushes);
        }

        return ($"{device} (not in /proc/diskstats)", null, null);
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct IoCounters
    {
        internal ulong ReadOperationCount, WriteOperationCount, OtherOperationCount;
        internal ulong ReadTransferCount, WriteTransferCount, OtherTransferCount;
    }

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetProcessIoCounters(IntPtr process, out IoCounters counters);
}

/// <summary>
/// With <c>--diagnostics</c>: the cost of the durability barriers a persist cycle uses besides a file flush, on
/// the device under the work directory.
/// </summary>
internal sealed class SyncProbe
{
    private const int Iterations = 100;

    // Open the directory and flush it: FlushFileBuffers on Windows, fsync on Unix.
    public required LatencySummary DirectoryFlush { get; init; }

    // Write 64 bytes to a new file, flush it, rename it over the destination (MoveFileEx with write-through on
    // Windows) and flush the directory: what publishing a checkpoint intent does.
    public required LatencySummary Publish { get; init; }

    // Delete a file and flush the directory: what committing a checkpoint does.
    public required LatencySummary DeleteAndFlush { get; init; }

    internal static SyncProbe Run(string directory)
    {
        var probeDirectory = Path.Combine(directory, "sync-probe");
        Directory.CreateDirectory(probeDirectory);
        var directoryFlush = new LatencyHistogram();
        var publish = new LatencyHistogram();
        var delete = new LatencyHistogram();
        var record = new byte[64];
        var temporary = Path.Combine(probeDirectory, "record.tmp");
        var destination = Path.Combine(probeDirectory, "record");
        try
        {
            for (var i = 0; i < Iterations; i++)
            {
                var start = Stopwatch.GetTimestamp();
                FlushDirectory(probeDirectory);
                directoryFlush.RecordTicks(Stopwatch.GetTimestamp() - start);

                start = Stopwatch.GetTimestamp();
                using (var file = File.OpenHandle(temporary, FileMode.Create, FileAccess.Write))
                {
                    RandomAccess.Write(file, record, 0L);
                    RandomAccess.FlushToDisk(file);
                }

                if (OperatingSystem.IsWindows())
                {
                    if (!MoveFileEx(temporary, destination, 0x1U | 0x8U))
                        throw new IOException("MoveFileEx failed", Marshal.GetLastPInvokeError());
                }
                else
                {
                    File.Move(temporary, destination, overwrite: true);
                }

                FlushDirectory(probeDirectory);
                publish.RecordTicks(Stopwatch.GetTimestamp() - start);

                start = Stopwatch.GetTimestamp();
                File.Delete(destination);
                FlushDirectory(probeDirectory);
                delete.RecordTicks(Stopwatch.GetTimestamp() - start);
            }
        }
        finally
        {
            Directory.Delete(probeDirectory, recursive: true);
        }

        return new() { DirectoryFlush = directoryFlush.Summarize(), Publish = publish.Summarize(), DeleteAndFlush = delete.Summarize() };
    }

    private static void FlushDirectory(string path)
    {
        if (OperatingSystem.IsWindows())
        {
            // GENERIC_WRITE, share all, OPEN_EXISTING, FILE_FLAG_BACKUP_SEMANTICS
            using var handle = CreateFile(path, 0x40000000U, 0x7U, IntPtr.Zero, 3U, 0x02000000U, IntPtr.Zero);
            if (handle.IsInvalid)
                throw new IOException("Cannot open the directory", Marshal.GetLastPInvokeError());

            RandomAccess.FlushToDisk(handle);
        }
        else
        {
            using var handle = new Microsoft.Win32.SafeHandles.SafeFileHandle(Open(path, 0), ownsHandle: true);
            if (handle.IsInvalid)
                throw new IOException("Cannot open the directory", Marshal.GetLastPInvokeError());

            RandomAccess.FlushToDisk(handle);
        }
    }

    [DllImport("kernel32.dll", EntryPoint = "MoveFileExW", CharSet = CharSet.Unicode, SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool MoveFileEx(string existingFileName, string newFileName, uint flags);

    [DllImport("kernel32.dll", EntryPoint = "CreateFileW", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern Microsoft.Win32.SafeHandles.SafeFileHandle CreateFile(string path, uint access, uint share, IntPtr securityAttributes,
        uint creationDisposition, uint flags, IntPtr template);

    [DllImport("libc", EntryPoint = "open", SetLastError = true)]
    private static extern IntPtr Open(byte[] path, int flags);

    private static IntPtr Open(string path, int flags)
        => Open(System.Text.Encoding.UTF8.GetBytes(path + '\0'), flags);
}
