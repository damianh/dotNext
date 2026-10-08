using System.Collections.Concurrent;
using System.Diagnostics;
using System.Globalization;
using System.Net;
using System.Net.Sockets;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using DotNext.Raft.FaultCampaign.Node;

namespace DotNext.Raft.FaultCampaign.Driver;

/// <summary>
/// The driver: starts the cluster, runs a closed-loop workload against the leader, injects the scheduled faults, and
/// checks the oracles after each fault is removed.
/// </summary>
internal sealed class Campaign : IDisposable
{
    private const long MinFreeBytesAtStart = 2L << 30, MinFreeBytes = 1L << 30;
    private static readonly TimeSpan StatusInterval = TimeSpan.FromMilliseconds(100), HistoryInterval = TimeSpan.FromMilliseconds(500);
    private static readonly TimeSpan TerminateGrace = TimeSpan.FromSeconds(15), WarmupTimeout = TimeSpan.FromSeconds(60);

    private static readonly JsonSerializerOptions ReportJson = new(JsonSerializerDefaults.Web)
    {
        WriteIndented = true,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
        Converters = { new JsonStringEnumConverter(JsonNamingPolicy.CamelCase) },
    };

    private readonly CampaignOptions options;
    private readonly CampaignReport report;
    private readonly NodeProcess[] nodes;
    private readonly OnlineHistoryChecker checker;
    private readonly HistoryFeed feed;
    private readonly SemaphoreSlim feedLock = new(1, 1);
    private readonly LogClassifier classifier = new();
    private readonly NodeStatus?[] statuses;
    private readonly long[] claimOffsets;
    private readonly ConcurrentQueue<(int Node, WriteKey Key)> pendingAcks = new();
    private readonly Random victims;
    private readonly Stopwatch clock = Stopwatch.StartNew();
    private volatile int leader = -1;
    private Task? monitor;
    private long acknowledged, rejected, unknown;

    private Campaign(CampaignOptions options, CampaignReport report, int[] raftPorts, int[] controlPorts)
    {
        this.options = options;
        this.report = report;
        nodes = new NodeProcess[CampaignOptions.Nodes];
        for (var i = 0; i < nodes.Length; i++)
            nodes[i] = new(options, i, raftPorts, controlPorts[i]);

        checker = new(nodes.Length);
        feed = new(checker, nodes.Length);
        statuses = new NodeStatus?[nodes.Length];
        claimOffsets = new long[nodes.Length];
        victims = new(unchecked((options.Seed * 31) + 7));
    }

    internal static async Task<int> RunAsync(ReadOnlyMemory<string> args)
    {
        var options = CampaignOptions.Parse(args.Span);
        PrepareOutput(options.OutputDirectory);
        var episodes = Schedule.Create(options.Seed, options.Episodes);
        var report = new CampaignReport
        {
            StartedUtc = DateTimeOffset.UtcNow,
            Command = string.Join(' ', Environment.GetCommandLineArgs().Skip(1)),
            Transport = options.Transport is Transport.Http ? "http" : "tcp",
            Seed = options.Seed,
            Injection = CampaignOptions.InjectionName(options.Injection),
            Schedule = episodes.Select(static e => e.Name).ToArray(),
            Bounds = new()
            {
                Nodes = CampaignOptions.Nodes,
                MaxDurationMinutes = options.MaxDuration.TotalMinutes,
                RecoveryTimeoutSeconds = options.RecoveryTimeout.TotalSeconds,
                WarmupEntries = options.WarmupEntries,
                MinAcknowledgedAfterRecovery = CampaignOptions.MinAcknowledgedAfterRecovery,
                Clients = options.Clients,
                PayloadBytes = options.PayloadSize,
                SnapshotInterval = options.SnapshotInterval,
            },
            NodeSettings = new()
            {
                Host = "DotNext.Raft.FaultCampaign node (mirrors the hosting of src/examples/RaftNode)",
                Storage = "WriteAheadLog at library defaults (default MemoryManagementStrategy)",
                LowerElectionTimeoutMs = NodeHost.LowerElectionTimeout,
                UpperElectionTimeoutMs = NodeHost.UpperElectionTimeout,
                RequestTimeoutSeconds = NodeHost.RequestTimeout.TotalSeconds,
                ReplicateTimeoutSeconds = NodeHost.ReplicateTimeout.TotalSeconds,
            },
            Environment = EnvironmentInfo.Collect(options.OutputDirectory),
        };

        foreach (var episode in episodes)
            report.Episodes.Add(new() { Number = episode.Number, Fault = episode.Name, HoldMs = (int)episode.Hold.TotalMilliseconds });

        var (raftPorts, controlPorts) = AllocatePorts(CampaignOptions.Nodes);
        using var campaign = new Campaign(options, report, raftPorts, controlPorts);
        try
        {
            await campaign.ExecuteAsync(episodes).ConfigureAwait(false);
        }
        catch (Exception e)
        {
            report.Error = e.ToString();
            Console.Error.WriteLine($"harness error: {e}");
        }

        return campaign.Finish();
    }

    // Only the paths this tool writes are removed, so a stale data directory never leaks into a new run.
    private static void PrepareOutput(string output)
    {
        Directory.CreateDirectory(output);
        foreach (var dir in (ReadOnlySpan<string>)["logs", "data", "claims"])
        {
            var path = Path.Combine(output, dir);
            if (Directory.Exists(path))
                Directory.Delete(path, recursive: true);

            Directory.CreateDirectory(path);
        }

        File.Delete(Path.Combine(output, "report.json"));
        File.Delete(Path.Combine(output, "history.json"));
    }

    private static (int[] Raft, int[] Control) AllocatePorts(int count)
    {
        var listeners = new List<TcpListener>();
        try
        {
            for (var i = 0; i < count * 2; i++)
            {
                var listener = new TcpListener(IPAddress.Loopback, 0);
                listener.Start();
                listeners.Add(listener);
            }

            var ports = listeners.Select(static l => ((IPEndPoint)l.LocalEndpoint).Port).ToArray();
            return (ports[..count], ports[count..]);
        }
        finally
        {
            foreach (var listener in listeners)
                listener.Stop();
        }
    }

    private async Task ExecuteAsync(Episode[] episodes)
    {
        if (EnvironmentInfo.GetFreeBytes(options.OutputDirectory) is < MinFreeBytesAtStart and var free)
        {
            report.Incomplete = $"only {free} bytes free in {options.OutputDirectory}; the campaign needs {MinFreeBytesAtStart}";
            return;
        }

        Log($"transport {report.Transport}, seed {options.Seed}, injection {report.Injection}, schedule {string.Join(',', report.Schedule)}");
        for (var i = 0; i < nodes.Length; i++)
            nodes[i].Start(InjectionAtStart(i, first: true));

        using var stop = new CancellationTokenSource();
        var monitor = this.monitor = MonitorAsync(stop.Token);
        var clients = Enumerable.Range(0, options.Clients).Select(c => ClientAsync(c, stop.Token)).ToArray();
        try
        {
            await RunEpisodesAsync(episodes).ConfigureAwait(false);
        }
        finally
        {
            await stop.CancelAsync().ConfigureAwait(false);
            await Task.WhenAll(clients.Append(monitor)).ConfigureAwait(false);
            await Task.WhenAll(nodes.Select(static n => n.TerminateAsync(TerminateGrace))).ConfigureAwait(false);
            ReadClaims();
            ScanLogs();
        }
    }

    private NodeInjection InjectionAtStart(int node, bool first) => options.Injection switch
    {
        // Every launch of every node starts from an empty directory, so a whole-cluster kill loses acknowledged writes.
        NodeInjection.VolatileStorage => NodeInjection.VolatileStorage,

        // One node drops one applied entry, in its first incarnation only.
        NodeInjection.DropApplied when first && node is 0 => NodeInjection.DropApplied,
        _ => NodeInjection.None,
    };

    private async Task RunEpisodesAsync(Episode[] episodes)
    {
        var deadline = clock.Elapsed + options.MaxDuration;

        Log($"warmup to index {options.WarmupEntries}");
        if (!await WaitUntilAsync(() => statuses.All(s => s?.AppliedIndex >= options.WarmupEntries), WarmupTimeout).ConfigureAwait(false))
        {
            report.LivenessFailure = $"warmup: the nodes did not all apply {options.WarmupEntries} entries within " +
                $"{WarmupTimeout.TotalSeconds} s ({DescribeStatuses()})";
            return;
        }

        if (!await CheckpointAsync("warmup", null).ConfigureAwait(false))
            return;

        foreach (var episode in episodes)
        {
            var result = report.Episodes[episode.Number - 1];
            if (clock.Elapsed > deadline)
            {
                report.Incomplete = $"the run reached its bound of {options.MaxDuration.TotalMinutes} min before episode {episode.Number}";
                return;
            }

            if (EnvironmentInfo.GetFreeBytes(options.OutputDirectory) is < MinFreeBytes and var free)
            {
                report.Incomplete = $"only {free} bytes free in {options.OutputDirectory} before episode {episode.Number}";
                return;
            }

            Log($"episode {episode.Number}: {episode.Name}, hold {episode.Hold.TotalMilliseconds:F0} ms");
            if (!await InjectAsync(episode, result).ConfigureAwait(false))
                return;

            if (!await CheckpointAsync($"episode {episode.Number} ({episode.Name})", result).ConfigureAwait(false))
                return;

            if (episode.Kind is FaultKind.LaggingSnapshot && result.SnapshotsInstalled is not > 0)
            {
                result.Outcome = "incomplete";
                report.Incomplete = $"episode {episode.Number} ({episode.Name}): node {result.Victims[0]} caught up without installing a snapshot";
                return;
            }

            result.Outcome = "pass";
        }
    }

    // Injects the fault, removes it, and waits for the cluster to recover.
    private async Task<bool> InjectAsync(Episode episode, EpisodeReport result)
    {
        if (await CurrentLeaderAsync().ConfigureAwait(false) is not { } before)
        {
            result.Outcome = "liveness failure";
            report.LivenessFailure = $"episode {episode.Number} ({episode.Name}): no leader before the fault ({DescribeStatuses()})";
            return false;
        }

        result.LeaderBefore = before.Id;
        result.TermBefore = before.Term;
        int[] restart;
        switch (episode.Kind)
        {
            case FaultKind.LeaderKill:
                restart = [before.Id];
                await nodes[before.Id].KillAsync().ConfigureAwait(false);
                break;
            case FaultKind.LeaderTerm:
                restart = [before.Id];
                await nodes[before.Id].TerminateAsync(TerminateGrace).ConfigureAwait(false);
                break;
            case FaultKind.FollowerKill:
                restart = [Schedule.PickFollower(victims, nodes.Length, before.Id)];
                await nodes[restart[0]].KillAsync().ConfigureAwait(false);
                break;
            case FaultKind.LaggingSnapshot:
                restart = [Schedule.PickFollower(victims, nodes.Length, before.Id)];
                result.Victims = restart;
                result.VictimLastEntryIndex = statuses[restart[0]]?.LastEntryIndex ?? 0L;
                await nodes[restart[0]].KillAsync().ConfigureAwait(false);
                break;
            case FaultKind.ClusterKill:
                restart = Enumerable.Range(0, nodes.Length).ToArray();
                await Task.WhenAll(nodes.Select(static n => n.KillAsync())).ConfigureAwait(false);
                break;
            default:
                throw new ArgumentOutOfRangeException(nameof(episode));
        }

        result.Victims = restart;
        await Task.Delay(episode.Hold).ConfigureAwait(false);
        if (episode.Kind is FaultKind.LaggingSnapshot && !await LagAsync(restart[0], episode, result).ConfigureAwait(false))
            return false;

        foreach (var id in restart)
            nodes[id].Start(InjectionAtStart(id, first: false));

        // Recovery: every node answers, a leader is elected, and the cluster acknowledges new writes.
        var started = clock.Elapsed;
        var acksAtRestart = Interlocked.Read(in acknowledged);
        var recovered = await WaitUntilAsync(
            () => statuses.All(static s => s is not null) && leader >= 0
                  && Interlocked.Read(in acknowledged) - acksAtRestart >= CampaignOptions.MinAcknowledgedAfterRecovery,
            options.RecoveryTimeout).ConfigureAwait(false);

        if (!recovered)
        {
            result.Outcome = "liveness failure";
            report.LivenessFailure = $"episode {episode.Number} ({episode.Name}): within {options.RecoveryTimeout.TotalSeconds} s of " +
                $"restarting node(s) {string.Join(',', restart)}, the cluster did not answer, elect a leader and acknowledge " +
                $"{CampaignOptions.MinAcknowledgedAfterRecovery} writes; {Interlocked.Read(in acknowledged) - acksAtRestart} " +
                $"acknowledged ({DescribeStatuses()})";
            return false;
        }

        result.RecoverySeconds = Math.Round((clock.Elapsed - started).TotalSeconds, 3);
        if (await CurrentLeaderAsync().ConfigureAwait(false) is { } after)
        {
            result.LeaderAfter = after.Id;
            result.TermAfter = after.Term;
        }

        return true;
    }

    // Keeps the follower down until both other nodes have a snapshot past the follower's log, so that whichever of
    // them leads must send the snapshot (the log reads the snapshot for any index at or below the snapshot index).
    private async Task<bool> LagAsync(int follower, Episode episode, EpisodeReport result)
    {
        var target = (result.VictimLastEntryIndex ?? 0L) + options.SnapshotInterval;
        var others = Enumerable.Range(0, nodes.Length).Where(i => i != follower).ToArray();
        if (!await WaitUntilAsync(() => others.All(i => statuses[i]?.SnapshotIndex >= target), options.RecoveryTimeout).ConfigureAwait(false))
        {
            result.Outcome = "liveness failure";
            report.LivenessFailure = $"episode {episode.Number} ({episode.Name}): the other nodes did not take a snapshot at index " +
                $"{target} or above within {options.RecoveryTimeout.TotalSeconds} s ({DescribeStatuses()})";
            return false;
        }

        result.LeaderSnapshotIndexAtRestart = others.Min(i => statuses[i]?.SnapshotIndex ?? 0L);
        return true;
    }

    /// <summary>
    /// The oracles, after the fault is removed and the cluster has recovered.
    /// </summary>
    /// <returns><see langword="false"/> if the run must stop.</returns>
    private async Task<bool> CheckpointAsync(string name, EpisodeReport? result)
    {
        var acks = new List<(int Node, WriteKey Key)>();
        while (pendingAcks.TryDequeue(out var ack))
            acks.Add(ack);

        // Every write acknowledged so far is committed at or below the commit index of the leader that acknowledges a
        // later write. Wait for a later write, then for every node to apply up to the highest commit index seen.
        var mark = Interlocked.Read(in acknowledged);
        var target = 0L;
        var caughtUp = await WaitUntilAsync(() =>
        {
            if (Interlocked.Read(in acknowledged) <= mark || statuses.Any(static s => s is null))
                return false;

            target = long.Max(target, statuses.Max(static s => s!.CommitIndex));
            return statuses.All(s => s!.AppliedIndex >= target);
        }, options.RecoveryTimeout, freshStatus: true).ConfigureAwait(false);

        if (!caughtUp)
        {
            if (result is not null)
                result.Outcome = "liveness failure";

            report.LivenessFailure = $"{name}: the nodes did not all apply up to commit index {target} within " +
                $"{options.RecoveryTimeout.TotalSeconds} s ({DescribeStatuses()})";
            return false;
        }

        await feedLock.WaitAsync().ConfigureAwait(false);
        try
        {
            // A fresh page from every node, so that every history reaches the target.
            for (var i = 0; i < nodes.Length; i++)
            {
                if (!await IngestAsync(i, CancellationToken.None).ConfigureAwait(false) || feed.Current(i).Count < target)
                {
                    if (result is not null)
                        result.Outcome = "liveness failure";

                    report.LivenessFailure = $"{name}: node {i} did not return its history up to index {target} (has {feed.Current(i).Count})";
                    return false;
                }
            }

            ReadClaims();
            foreach (var (node, key) in acks)
                checker.OnAcknowledged(node, key);

            var histories = Enumerable.Range(0, nodes.Length).Select(feed.Current).ToArray();
            checker.CheckFinal(histories);
            if (checker.Violation is null && RecoveryAudit.Check(checker.Acknowledged, histories) is { } durability)
                checker.Report(durability);

            if (result is not null)
            {
                result.CommitIndexAtCheckpoint = target;
                result.AcknowledgedTotal = checker.Acknowledged.Count;
                if (result.Victims is [var victim] && statuses[victim] is { } status)
                    result.SnapshotsInstalled = status.Restores - status.RestoresAtStartup;
            }
        }
        finally
        {
            feedLock.Release();
        }

        ScanLogs();
        foreach (var node in nodes)
            node.CheckUnexpectedExit();

        if (checker.Violation is { } violation)
        {
            if (result is not null)
                result.Outcome = "safety violation";

            report.Violation = ViolationReport.Create(name, violation);
            Log($"{name}: safety violation ({violation.Oracle})");
            return false;
        }

        if (HasUnexpectedSignals())
        {
            if (result is not null)
                result.Outcome = "unexpected signal";

            Log($"{name}: unexpected failure signal");
            return false;
        }

        Log($"{name}: oracles passed at commit index {target}, {checker.Acknowledged.Count} acknowledged writes checked");
        return true;
    }

    private bool HasUnexpectedSignals()
        => classifier.Unexpected.Any() || nodes.Any(static n => n.UnexpectedExits.Count > 0);

    private async Task MonitorAsync(CancellationToken token)
    {
        var lastIngest = TimeSpan.Zero;
        while (!token.IsCancellationRequested)
        {
            try
            {
                await PollStatusAsync(token).ConfigureAwait(false);
                if (clock.Elapsed - lastIngest >= HistoryInterval && await feedLock.WaitAsync(0, token).ConfigureAwait(false))
                {
                    lastIngest = clock.Elapsed;
                    try
                    {
                        for (var i = 0; i < nodes.Length; i++)
                            await IngestAsync(i, token).ConfigureAwait(false);
                    }
                    finally
                    {
                        feedLock.Release();
                    }
                }

                await Task.Delay(StatusInterval, token).ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (token.IsCancellationRequested)
            {
                break;
            }
        }
    }

    private async Task PollStatusAsync(CancellationToken token)
    {
        var polled = await Task.WhenAll(nodes.Select(n => n.GetStatusAsync(token))).ConfigureAwait(false);
        var current = -1;
        for (var i = 0; i < polled.Length; i++)
        {
            Volatile.Write(ref statuses[i], polled[i]);
            if (polled[i] is { IsLeader: true } s && (current < 0 || s.Term > polled[current]!.Term))
                current = i;
        }

        leader = current;
    }

    // Must be called under feedLock.
    private async Task<bool> IngestAsync(int node, CancellationToken token)
    {
        var (from, incarnation, epoch) = feed.NextRequest(node);
        if (await nodes[node].GetHistoryAsync(from, incarnation, epoch, token).ConfigureAwait(false) is not { } page)
            return false;

        var entries = Array.ConvertAll(page.Entries, ControlApi.Decode);
        if (!feed.Ingest(node, page.Incarnation, page.Epoch, page.From, entries))
            throw new InvalidOperationException($"node {node} returned a history page from {page.From} that does not continue position {from}");

        return true;
    }

    private void ReadClaims()
    {
        for (var i = 0; i < nodes.Length; i++)
        {
            var path = nodes[i].ClaimsFile;
            if (!File.Exists(path))
                continue;

            var bytes = File.ReadAllBytes(path);
            if (claimOffsets[i] >= bytes.Length)
                continue;

            // A claim the node was writing when it was killed has no newline yet: it is read next time, or never.
            var end = Array.LastIndexOf(bytes, (byte)'\n');
            if (end < claimOffsets[i])
                continue;

            var text = Encoding.UTF8.GetString(bytes, (int)claimOffsets[i], end + 1 - (int)claimOffsets[i]);
            claimOffsets[i] = end + 1;
            foreach (var line in text.Split('\n', StringSplitOptions.RemoveEmptyEntries))
                checker.OnLeaderClaim(i, long.Parse(line, CultureInfo.InvariantCulture));
        }
    }

    private void ScanLogs()
    {
        foreach (var node in nodes)
        {
            foreach (var log in node.LogFiles)
                classifier.Scan(log);
        }
    }

    private async Task ClientAsync(int client, CancellationToken token)
    {
        var payload = new Payload(options.PayloadSize);
        var buffer = payload.CreateBuffer();
        var seq = 0L;
        while (!token.IsCancellationRequested)
        {
            try
            {
                var target = leader;
                if (target < 0)
                {
                    await Task.Delay(20, token).ConfigureAwait(false);
                    continue;
                }

                // A new sequence number for every attempt: the oracle requires a closed-loop client to send its next
                // write only after the outcome of the previous one is known or can no longer change.
                var key = new WriteKey(WriteKey.ClosedLoop, client, ++seq);
                switch (await nodes[target].WriteAsync(payload.Write(buffer, key), token).ConfigureAwait(false))
                {
                    case WriteOutcome.Acknowledged:
                        pendingAcks.Enqueue((target, key));
                        Interlocked.Increment(ref acknowledged);
                        break;
                    case WriteOutcome.Rejected:
                        Interlocked.Increment(ref rejected);
                        await Task.Delay(20, token).ConfigureAwait(false);
                        break;
                    default:
                        Interlocked.Increment(ref unknown);
                        await Task.Delay(50, token).ConfigureAwait(false);
                        break;
                }
            }
            catch (OperationCanceledException) when (token.IsCancellationRequested)
            {
                break;
            }
        }
    }

    private async Task<NodeStatus?> CurrentLeaderAsync()
    {
        await WaitUntilAsync(() => leader >= 0, options.RecoveryTimeout).ConfigureAwait(false);
        return leader is var id and >= 0 ? Volatile.Read(in statuses[id]) : null;
    }

    // Checks the condition after each status poll of the monitor (or after a fresh poll) until it holds or the
    // timeout expires.
    private async Task<bool> WaitUntilAsync(Func<bool> condition, TimeSpan timeout, bool freshStatus = false)
    {
        var deadline = clock.Elapsed + timeout;
        while (true)
        {
            if (freshStatus)
                await PollStatusAsync(CancellationToken.None).ConfigureAwait(false);

            // A failed monitor leaves the statuses stale: report the failure rather than a liveness failure.
            if (monitor is { IsFaulted: true } failed)
                await failed.ConfigureAwait(false);

            if (condition())
                return true;

            if (clock.Elapsed > deadline)
                return false;

            await Task.Delay(StatusInterval).ConfigureAwait(false);
        }
    }

    private string DescribeStatuses()
        => string.Join("; ", statuses.Select(static (s, i) => s is null
            ? $"node {i} down or not answering"
            : $"node {i} term {s.Term}{(s.IsLeader ? " leader" : "")} last {s.LastEntryIndex} commit {s.CommitIndex} applied {s.AppliedIndex} snapshot {s.SnapshotIndex}"));

    private void Log(string message)
        => Console.WriteLine(string.Create(CultureInfo.InvariantCulture, $"[{clock.Elapsed.TotalSeconds,7:F1}s] {message}"));

    private int Finish()
    {
        report.FinishedUtc = DateTimeOffset.UtcNow;
        report.Workload.Acknowledged = Interlocked.Read(in acknowledged);
        report.Workload.Rejected = Interlocked.Read(in rejected);
        report.Workload.Unknown = Interlocked.Read(in unknown);
        foreach (var node in nodes)
        {
            report.Nodes.Add(new()
            {
                Id = node.Id,
                Incarnations = node.Incarnations,
                HistoryReplacements = feed.Replacements(node.Id),
                AppliedEntries = feed.Current(node.Id).Count,
                Logs = node.LogFiles.Select(l => Path.GetRelativePath(options.OutputDirectory, l)).ToArray(),
                UnexpectedExits = node.UnexpectedExits.ToArray(),
            });
        }

        report.Signals.LogLines = classifier.Lines;
        foreach (var group in classifier.Signals.GroupBy(static s => (s.Class, s.Rule)))
        {
            var counts = group.Key.Class switch
            {
                SignalClass.Expected => report.Signals.Expected,
                SignalClass.Unexpected => report.Signals.Unexpected,
                _ => report.Signals.Unclassified,
            };

            counts[group.Key.Rule] = group.Count();
            if (group.Key.Class is not SignalClass.Expected)
            {
                foreach (var signal in group.Take(3))
                    report.Signals.Examples.Add($"{signal.File}:{signal.Line}: {signal.Text}");
            }
        }

        if (report.Violation is null && checker.Violation is { } violation)
            report.Violation = ViolationReport.Create("shutdown", violation);

        (report.Verdict, report.ExitCode) = report switch
        {
            { Error: not null } => ("harness error", 1),
            { Violation: not null } => ("safety violation", 3),
            { LivenessFailure: not null } => ("liveness failure", 4),
            _ when HasUnexpectedSignals() => ("unexpected signal", 6),
            { Incomplete: not null } => ("incomplete", 5),
            _ => ("pass", 0),
        };

        WriteJson("report.json", report);
        WriteJson("history.json", new
        {
            acknowledged = checker.Acknowledged.Select(static a => new { key = a.Key.ToString(), node = a.Node, index = a.Index }),
            nodes = Enumerable.Range(0, nodes.Length).Select(i => new
            {
                id = i,
                entries = feed.Current(i).Select(static e => new { index = e.Index, term = e.Term, key = WriteKey.ToPayloadString(e.Key) }),
            }),
        });

        if (report.ExitCode is 0 && !options.KeepData)
            Directory.Delete(Path.Combine(options.OutputDirectory, "data"), recursive: true);

        Log($"{report.Verdict} (exit code {report.ExitCode}): {report.Workload.Acknowledged} acknowledged, " +
            $"{report.Workload.Rejected} rejected, {report.Workload.Unknown} unknown; {Path.Combine(options.OutputDirectory, "report.json")}");

        if (report.Violation is { } v)
            Log($"violation: {v.Oracle}: {v.Message}");

        if (report.LivenessFailure is { } l)
            Log($"liveness: {l}");

        if (report.Incomplete is { } inc)
            Log($"incomplete: {inc}");

        if (report.Error is not null)
            Log("harness error: see report.json");

        foreach (var exit in report.Nodes.SelectMany(static n => n.UnexpectedExits))
            Log($"unexpected exit: {exit}");

        foreach (var example in report.Signals.Examples)
            Log($"signal: {example}");

        return report.ExitCode;
    }

    private void WriteJson<T>(string file, T value)
        => File.WriteAllText(Path.Combine(options.OutputDirectory, file), JsonSerializer.Serialize(value, ReportJson));

    public void Dispose()
    {
        foreach (var node in nodes)
            node.Dispose();

        feedLock.Dispose();
    }
}
