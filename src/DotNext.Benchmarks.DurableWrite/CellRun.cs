using System.Diagnostics;
using DotNext.Benchmarks.DurableWrite.Oracles;
using DotNext.Net.Cluster.Consensus.Raft.InProcess;

namespace DotNext.Benchmarks.DurableWrite;

internal readonly record struct BacklogSample(long Uncommitted, long Unapplied, long FollowerLag);

/// <summary>
/// The state of one cell: its phases (warmup, measured window, drain), load counters, latency histograms,
/// bounds and oracles.
/// </summary>
/// <remarks>
/// Latencies and load are recorded in the measured window only. Every acknowledged write, warmup included, is checked
/// by the oracles. A cell stops at the end of its duration, or earlier when it reaches a bound or an oracle reports
/// a violation; the reason is reported as <see cref="CellReport.StoppedBy"/>.
/// </remarks>
internal sealed class CellRun : IDisposable
{
    private const long MinFreeBytes = 1L << 30;
    private static readonly TimeSpan SampleInterval = TimeSpan.FromMilliseconds(10);

    private readonly CancellationTokenSource stopSource;
    private readonly Payload payload;
    private volatile bool measuring;
    private string? stoppedBy;
    private long offered, completed, rejected, unknown, overloaded, acknowledgedTotal, acknowledgedPayload, inFlight, maxInFlight;
    private long measureStart, measureEnd;
    private ProcessSample processStart, processEnd;
    private readonly BacklogReport backlog = new();

    internal CellRun(CellSpec spec, RunOptions options, string root, CancellationToken runToken)
    {
        Spec = spec;
        Options = options;
        Root = root;
        RunToken = runToken;
        stopSource = CancellationTokenSource.CreateLinkedTokenSource(runToken);
        payload = new(spec.EntrySize);
        Diagnostics = options.Diagnostics ? new(spec, root) : null;
        Checker = new(spec.Voters);
        Timeline = new(ReplicaApplyLag);
        Report = new()
        {
            Name = spec.Name,
            Kind = spec.Kind.ToString(),
            Voters = spec.Voters,
            EntrySize = spec.EntrySize,
            Concurrency = spec.Concurrency,
            BatchSize = spec.BatchSize,
            Memory = Profiles.MemoryName(spec.Memory) + (spec.NoBuffering ? "+nobuffering" : string.Empty),
            NoBuffering = spec.NoBuffering,
            WarmupSeconds = spec.Warmup.TotalSeconds,
            DurationSeconds = spec.Duration.TotalSeconds,
            SnapshotInterval = spec.SnapshotInterval is long.MaxValue ? 0L : spec.SnapshotInterval,
            Injection = spec.Injection is FailureInjection.None ? null : Profiles.InjectionName(spec.Injection),
            Repeat = spec.Repeat,
        };
    }

    internal CellSpec Spec { get; }

    internal RunOptions Options { get; }

    internal string Root { get; }

    // The whole run; in-flight writes complete with this token after the cell has stopped, so their outcome is known.
    internal CancellationToken RunToken { get; }

    // Signaled when the cell must stop sending new writes.
    internal CancellationToken StopToken => stopSource.Token;

    internal bool IsStopped => stopSource.IsCancellationRequested;

    internal Payload Payload => payload;

    internal WalMeterListener Meter { get; } = new();

    // With --diagnostics only.
    internal DiagnosticsCollector? Diagnostics { get; }

    internal OnlineHistoryChecker Checker { get; }

    internal ApplyTimeline Timeline { get; }

    internal CellReport Report { get; }

    internal LatencyHistogram AckLatency { get; } = new();

    internal LatencyHistogram AppendLatency { get; } = new();

    internal LatencyHistogram CommitApplyLatency { get; } = new();

    internal LatencyHistogram ReplicaApplyLag { get; } = new();

    internal bool Measuring => measuring;

    internal long AcknowledgedTotal => Interlocked.Read(ref acknowledgedTotal);

    internal void Stop(string reason)
    {
        if (Interlocked.CompareExchange(ref stoppedBy, reason, null) is null)
            stopSource.Cancel();
    }

    internal void OnOffered(int count = 1)
    {
        if (measuring)
            Interlocked.Add(ref offered, count);
    }

    internal void OnOverloaded()
    {
        if (measuring)
            Interlocked.Increment(ref overloaded);
    }

    internal void BeginRequest(int count = 1)
    {
        var current = Interlocked.Add(ref inFlight, count);
        for (var max = Volatile.Read(in maxInFlight); current > max && measuring;)
        {
            var actual = Interlocked.CompareExchange(ref maxInFlight, current, max);
            if (actual == max)
                break;

            max = actual;
        }
    }

    internal void EndRequest(int count = 1) => Interlocked.Add(ref inFlight, -count);

    internal void OnRejected()
    {
        if (measuring)
            Interlocked.Increment(ref rejected);
    }

    internal void OnUnknown()
    {
        if (measuring)
            Interlocked.Increment(ref unknown);
    }

    /// <summary>
    /// Counts acknowledged writes and records their latency, from <paramref name="start"/> (the intended send time in
    /// the open loop) to now.
    /// </summary>
    internal void OnAcknowledged(int count, long start, long end)
    {
        var total = Interlocked.Add(ref acknowledgedTotal, count);
        var bytes = Interlocked.Add(ref acknowledgedPayload, (long)count * payload.Size);
        if (measuring)
        {
            Interlocked.Add(ref completed, count);
            AckLatency.RecordTicks(end - start);
            Diagnostics?.OnAcknowledged(count);
        }

        if (total >= Options.MaxEntries)
            Stop("max-entries");
        else if (bytes >= Options.MaxPayloadBytes)
            Stop("max-payload");
    }

    /// <summary>
    /// Runs the warmup and the measured window, sampling the backlog and watching the oracles and the free disk space.
    /// </summary>
    /// <param name="sampler">Reads the backlog, or <see langword="null"/> if there is nothing to read now.</param>
    /// <param name="onMeasuring">Called once when the measured window starts.</param>
    internal async Task RunPhasesAsync(Func<BacklogSample?> sampler, Action? onMeasuring = null)
    {
        var started = Stopwatch.GetTimestamp();
        var nextDiskCheck = 0L;
        using var timer = new PeriodicTimer(SampleInterval);
        try
        {
            while (!IsStopped)
            {
                var now = Stopwatch.GetTimestamp();
                if (Checker.Violation is not null)
                {
                    Stop("violation");
                    break;
                }

                if (now >= nextDiskCheck)
                {
                    nextDiskCheck = now + Stopwatch.Frequency;
                    if (EnvironmentInfo.GetFreeBytes(Root) is { } free && free < MinFreeBytes)
                    {
                        Stop("free-disk");
                        break;
                    }
                }

                if (!measuring)
                {
                    if (Stopwatch.GetElapsedTime(started, now) >= Spec.Warmup)
                        BeginMeasuring(onMeasuring);
                }
                else if (Stopwatch.GetElapsedTime(measureStart, now) >= Spec.Duration)
                {
                    Stop("duration");
                    break;
                }
                else if (sampler() is { } sample)
                {
                    backlog.Samples++;
                    backlog.MaxUncommitted = long.Max(backlog.MaxUncommitted, sample.Uncommitted);
                    backlog.MaxUnapplied = long.Max(backlog.MaxUnapplied, sample.Unapplied);
                    backlog.MaxFollowerLag = long.Max(backlog.MaxFollowerLag, sample.FollowerLag);
                    Diagnostics?.OnBacklog(sample);
                }

                await timer.WaitForNextTickAsync(StopToken).ConfigureAwait(false);
            }
        }
        catch (OperationCanceledException) when (IsStopped)
        {
            // stopped by a bound, a worker or the run timeout
        }
        finally
        {
            if (RunToken.IsCancellationRequested)
                Stop("max-duration");

            EndMeasuring();
        }
    }

    private void BeginMeasuring(Action? onMeasuring)
    {
        processStart = ProcessSample.Take();
        Diagnostics?.Begin(Meter);
        measureStart = Stopwatch.GetTimestamp();
        Meter.Measuring = true;
        Timeline.Measuring = true;
        measuring = true;
        onMeasuring?.Invoke();
    }

    private void EndMeasuring()
    {
        if (!measuring)
            return;

        measuring = false;
        Meter.Measuring = false;
        Timeline.Measuring = false;
        measureEnd = Stopwatch.GetTimestamp();
        Diagnostics?.End(Meter);
        processEnd = ProcessSample.Take();
    }

    /// <summary>
    /// Fills in the load and latency of the measured window, once the workload has stopped.
    /// </summary>
    internal void CompleteLoad(FsyncProbe? probe)
    {
        var report = Report;
        report.StoppedBy = Volatile.Read(in stoppedBy) ?? "duration";
        if (measureEnd is 0L)
            return;

        var seconds = Stopwatch.GetElapsedTime(measureStart, measureEnd).TotalSeconds;
        report.MeasuredSeconds = Math.Round(seconds, 3);
        report.Offered = Interlocked.Read(ref offered);
        report.Completed = Interlocked.Read(ref completed);
        report.Rejected = Interlocked.Read(ref rejected);
        report.Unknown = Interlocked.Read(ref unknown);
        report.Overloaded = Interlocked.Read(ref overloaded);
        if (seconds > 0D)
        {
            report.OfferedPerSecond = Math.Round(report.Offered / seconds, 1);
            report.CompletedPerSecond = Math.Round(report.Completed / seconds, 1);
            report.PayloadMiBPerSecond = Math.Round(report.Completed * (double)payload.Size / seconds / (1024D * 1024D), 3);
        }

        report.AcknowledgedTotal = AcknowledgedTotal;
        report.AckLatency = AckLatency.Summarize();
        report.AppendLatency = AppendLatency.Count > 0L ? AppendLatency.Summarize() : null;
        report.CommitApplyLatency = CommitApplyLatency.Count > 0L ? CommitApplyLatency.Summarize() : null;
        report.ReplicaApplyLag = ReplicaApplyLag.Count > 0L ? ReplicaApplyLag.Summarize() : null;
        report.CheckpointFlushDuration = Meter.CheckpointFlushDuration.Summarize();
        report.ApplyDuration = Meter.ApplyDuration.Summarize();
        backlog.MaxInFlight = Interlocked.Read(ref maxInFlight);
        report.Backlog = backlog;
        report.Process = ProcessSample.Delta(processStart, processEnd);
        report.Diagnostics = Diagnostics?.Complete(report.Completed, seconds);

        // A durable acknowledgment includes at least one synchronous flush. If it is much cheaper than a flush measured
        // on this device, the numbers may describe buffered writes.
        var durableLatency = report.AppendLatency ?? report.AckLatency;
        if (probe is { Distinguishable: true } && durableLatency.Count > 0L)
            report.SuspectBuffered = durableLatency.P50Us * 2L < probe.Flushed.P50Us;
    }

    /// <summary>
    /// Reconciles a node's history with the entries its log applied, and with the log it recovered after the run.
    /// </summary>
    /// <seealso cref="HistoryReconciliation"/>
    internal void Reconcile(int node, IReadOnlyList<AppliedEntry> history, long appliedIndex, IReadOnlyList<AppliedEntry> walTail,
        IReadOnlyList<AppliedEntry>? recovered)
    {
        if (!Report.Oracles.Checked.Contains(OnlineHistoryChecker.Reconciliation))
            Report.Oracles.Checked.Add(OnlineHistoryChecker.Reconciliation);

        Report.Oracles.ReconciledEntries += history.Count;
        if (HistoryReconciliation.CheckApplied(node, history, appliedIndex, walTail) is { } applied)
            Checker.Report(applied);

        if (recovered is not null && HistoryReconciliation.CheckRecovered(node, history, recovered) is { } durable)
            Checker.Report(durable);
    }

    /// <summary>
    /// Runs the durability audit over the recovered logs of all the voters.
    /// </summary>
    internal void CheckDurability(IReadOnlyList<IReadOnlyList<AppliedEntry>> durableLogs)
    {
        Report.Oracles.Checked.Add(OnlineHistoryChecker.Durability);
        Report.Oracles.DurableEntriesAudited = durableLogs.Sum(static log => (long)log.Count);
        if (DurabilityAudit.Check(Checker.Acknowledged, durableLogs) is { } violation)
            Checker.Report(violation);
    }

    internal NodeReport AddNode(int node, string role, bool durable, string? root, long lastIndex, HistoryStateMachine? stateMachine, long recovered)
    {
        var counters = Meter[node];
        var result = new NodeReport
        {
            Node = node,
            Role = role,
            Durable = durable,
            Appended = counters.Read(ref counters.Appended),
            Flushed = counters.Read(ref counters.Flushed),
            Committed = counters.Read(ref counters.Committed),
            Applied = counters.Read(ref counters.Applied),
            AppendBytes = counters.Read(ref counters.AppendBytes),
            DeletedBytes = counters.Read(ref counters.DeletedBytes),
            FileBytes = root is null ? 0L : DiskUsage.GetFileBytes(root),
            LastIndex = lastIndex,
            Snapshots = stateMachine?.Snapshots ?? 0,
            SnapshotFailures = stateMachine?.SnapshotFailures ?? 0,
            Restores = stateMachine?.Restores ?? 0,
            RecoveredEntries = recovered,
        };

        Report.Nodes.Add(result);
        return result;
    }

    /// <summary>
    /// Bytes appended and on disk against the acknowledged payload, once the nodes are reported.
    /// </summary>
    internal void CompleteWriteAmplification(int leader)
    {
        var payloadBytes = Interlocked.Read(ref acknowledgedPayload);
        var leaderBytes = Report.Nodes.FirstOrDefault(n => n.Node == leader)?.AppendBytes ?? 0L;
        var clusterBytes = Report.Nodes.Sum(static n => n.AppendBytes);
        var fileBytes = Report.Nodes.Sum(static n => n.FileBytes);
        Report.WriteAmplification = new()
        {
            PayloadBytes = payloadBytes,
            LeaderAppendBytes = leaderBytes,
            ClusterAppendBytes = clusterBytes,
            ClusterFileBytes = fileBytes,
            LeaderAppendRatio = Ratio(leaderBytes),
            ClusterAppendRatio = Ratio(clusterBytes),
            ClusterFileRatio = Ratio(fileBytes),
        };

        double Ratio(long bytes) => payloadBytes > 0L ? Math.Round((double)bytes / payloadBytes, 3) : 0D;
    }

    /// <summary>
    /// Copies the oracle outcome into the report.
    /// </summary>
    internal SafetyViolationException? CompleteOracles()
    {
        Report.Oracles.AcknowledgedChecked = Checker.Acknowledged.Count;
        var violation = Checker.Violation;
        if (violation is not null)
        {
            Report.Oracles.Violation = violation.Message;
            Report.StoppedBy = Report.StoppedBy is "duration" ? "violation" : Report.StoppedBy;
        }

        return violation;
    }

    public void Dispose()
    {
        Meter.Dispose();
        Diagnostics?.Dispose();
        stopSource.Dispose();
    }
}
