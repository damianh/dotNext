using System.Diagnostics;
using System.Globalization;
using System.Net;
using System.Net.Sockets;
using System.Runtime.InteropServices;
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

    // The client number of the probe that writes to the isolated node of a partition episode, plus the episode number.
    private const int ProbeClient = 1000;
    private static readonly TimeSpan StatusInterval = TimeSpan.FromMilliseconds(100), HistoryInterval = TimeSpan.FromMilliseconds(500);
    private static readonly TimeSpan TerminateGrace = TimeSpan.FromSeconds(15), WarmupTimeout = TimeSpan.FromSeconds(60), DrainGrace = TimeSpan.FromSeconds(5);

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
    private readonly PartitionNetwork network;
    private readonly PartitionOracle partitions = new();

    // Per node, the writes it acknowledged: the majority side of a partition must keep acknowledging writes.
    private readonly long[] acknowledgedBy;

    // Acknowledgments not yet audited. An acknowledgment is added and counted atomically, under ackSync, so that a
    // checkpoint can take all the acknowledgments up to a count.
    private readonly Lock ackSync = new();
    private readonly List<(int Node, WriteKey Key)> pendingAcks = [];

    // The highest count of acknowledged writes seen by a client when it submitted a write that was acknowledged later.
    private long ackBarrier = -1L;

    // Per node, the snapshot failures reported by each incarnation (the counter restarts with the process).
    private readonly Lock failuresSync = new();
    private readonly Dictionary<Guid, int>[] snapshotFailures;
    private readonly Random victims;
    private readonly Stopwatch clock;

    // stop aborts the clients and the monitor; drain stops the clients after their requests in flight; bound is the
    // --max-duration of the run, from the start of the driver.
    private readonly CancellationTokenSource stop = new(), drain = new(), bound;
    private readonly List<Task> clients = [];
    private volatile int leader = -1;
    private Task? monitor;
    private long acknowledged, rejected, unknown, submittedWrites;

    private Campaign(CampaignOptions options, CampaignReport report, int[] raftPorts, int[] listenPorts, int[] controlPorts, Stopwatch clock)
    {
        this.options = options;
        this.report = report;
        this.clock = clock;
        bound = new(TimeSpan.Max(TimeSpan.Zero, options.MaxDuration - clock.Elapsed));
        snapshotFailures = Enumerable.Range(0, CampaignOptions.Nodes).Select(static _ => new Dictionary<Guid, int>()).ToArray();
        nodes = new NodeProcess[CampaignOptions.Nodes];
        for (var i = 0; i < nodes.Length; i++)
            nodes[i] = new(options, i, raftPorts, listenPorts[i], controlPorts[i]);

        network = new(raftPorts, listenPorts, i => nodes[i].Pid);
        acknowledgedBy = new long[nodes.Length];

        checker = new(nodes.Length);
        feed = new(checker, nodes.Length);
        statuses = new NodeStatus?[nodes.Length];
        claimOffsets = new long[nodes.Length];
        victims = new(unchecked((options.Seed * 31) + 7));
    }

    internal static async Task<int> RunAsync(ReadOnlyMemory<string> args)
    {
        var clock = Stopwatch.StartNew();
        var options = CampaignOptions.Parse(args.Span);
        PrepareOutput(options.OutputDirectory);
        var episodes = Schedule.Create(options.Seed, options.Episodes, options.Cycles);
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
                Cycles = options.Cycles,
                MaxWrites = options.MaxWrites,
            },
            NodeSettings = new()
            {
                Host = "DotNext.Raft.FaultCampaign node (mirrors the hosting of src/examples/RaftNode)",
                Storage = "WriteAheadLog at library defaults (default MemoryManagementStrategy)",
                LowerElectionTimeoutMs = NodeHost.LowerElectionTimeout,
                UpperElectionTimeoutMs = NodeHost.UpperElectionTimeout,
                RequestTimeoutSeconds = NodeHost.RequestTimeout(options.Transport).TotalSeconds,
                ReplicateTimeoutSeconds = NodeHost.ReplicateTimeout.TotalSeconds,
            },
            Environment = EnvironmentInfo.Collect(options.OutputDirectory),
        };

        var episodesPerCycle = episodes.Length / options.Cycles;
        foreach (var episode in episodes)
            report.Episodes.Add(new()
            {
                Number = episode.Number,
                Cycle = ((episode.Number - 1) / episodesPerCycle) + 1,
                Fault = episode.Name,
                HoldMs = (int)episode.Hold.TotalMilliseconds,
            });

        var (raftPorts, listenPorts, controlPorts) = AllocatePorts(CampaignOptions.Nodes);
        using var campaign = new Campaign(options, report, raftPorts, listenPorts, controlPorts, clock);
        using var terminate = PosixSignalRegistration.Create(PosixSignal.SIGTERM, context =>
        {
            context.Cancel = true;
            campaign.Cancel("SIGTERM");
        });
        ConsoleCancelEventHandler interrupt = (_, e) =>
        {
            e.Cancel = true;
            campaign.Cancel("Ctrl+C");
        };
        Console.CancelKeyPress += interrupt;
        try
        {
            await campaign.ExecuteAsync(episodes).ConfigureAwait(false);
        }
        catch (Exception e)
        {
            report.Error = e.ToString();
            Console.Error.WriteLine($"harness error: {e}");
        }
        finally
        {
            Console.CancelKeyPress -= interrupt;
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
        File.Delete(Path.Combine(output, "resources.jsonl"));
    }

    // Raft: the member endpoints, where the proxies listen. Listen: where the nodes listen for Raft, behind the proxies.
    private static (int[] Raft, int[] Listen, int[] Control) AllocatePorts(int count)
    {
        var listeners = new List<TcpListener>();
        try
        {
            for (var i = 0; i < count * 3; i++)
            {
                var listener = new TcpListener(IPAddress.Loopback, 0);
                listener.Start();
                listeners.Add(listener);
            }

            var ports = listeners.Select(static l => ((IPEndPoint)l.LocalEndpoint).Port).ToArray();
            return (ports[..count], ports[count..(count * 2)], ports[(count * 2)..]);
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
        network.Start();
        for (var i = 0; i < nodes.Length; i++)
            nodes[i].Start(InjectionAtStart(i, first: true));

        var monitor = this.monitor = MonitorAsync(stop.Token);
        clients.AddRange(Enumerable.Range(0, options.Clients).Select(c => ClientAsync(c, drain.Token)));
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
        var phase = "warmup";
        EpisodeReport? current = null;
        try
        {
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
                if (bound.IsCancellationRequested)
                {
                    report.Incomplete ??= $"the run reached its bound of {options.MaxDuration.TotalMinutes} min before episode {episode.Number}";
                    return;
                }

                if (EnvironmentInfo.GetFreeBytes(options.OutputDirectory) is < MinFreeBytes and var free)
                {
                    report.Incomplete = $"only {free} bytes free in {options.OutputDirectory} before episode {episode.Number}";
                    return;
                }

                phase = $"episode {episode.Number} ({episode.Name})";
                current = result;
                Log($"episode {episode.Number}: {episode.Name}, hold {episode.Hold.TotalMilliseconds:F0} ms");
                if (!await InjectAsync(episode, result).ConfigureAwait(false))
                    return;

                if (!await CheckpointAsync(phase, result).ConfigureAwait(false))
                    return;

                if (episode.Kind is FaultKind.LaggingSnapshot && result.SnapshotsInstalled is not > 0)
                {
                    result.Outcome = "incomplete";
                    report.Incomplete = $"episode {episode.Number} ({episode.Name}): node {result.Victims[0]} caught up without installing a snapshot";
                    return;
                }

                result.Outcome = "pass";
                current = null;
            }

            phase = "the final checkpoint";
            if (await FinalCheckpointAsync().ConfigureAwait(false) && bound.IsCancellationRequested)
                report.Incomplete ??= $"the run passed the final checkpoint after its bound of {options.MaxDuration.TotalMinutes} min";
        }
        catch (OperationCanceledException) when (bound.IsCancellationRequested)
        {
            // Every wait and hold observes the bound. Reaching it is not a liveness failure: the run is incomplete.
            if (current is not null)
                current.Outcome = "incomplete";

            report.Incomplete ??= $"the run reached its bound of {options.MaxDuration.TotalMinutes} min during {phase}";
        }
    }

    // The clients are stopped after their requests in flight, so that no write is acknowledged after the cutoff of the
    // final checkpoint and every acknowledgment is audited. The barrier of that checkpoint is a write submitted after all
    // of those acknowledgments: one client writes until it gets one acknowledgment, then stops too.
    private async Task<bool> FinalCheckpointAsync()
    {
        await drain.CancelAsync().ConfigureAwait(false);
        await Task.WhenAll(clients).WaitAsync(bound.Token).ConfigureAwait(false);

        var mark = Interlocked.Read(in acknowledged);
        using var last = new CancellationTokenSource();
        var client = ClientAsync(options.Clients, last.Token);
        clients.Add(client);
        var acked = await WaitUntilAsync(() => Interlocked.Read(in acknowledged) > mark, options.RecoveryTimeout).ConfigureAwait(false);
        await last.CancelAsync().ConfigureAwait(false);
        await client.WaitAsync(bound.Token).ConfigureAwait(false);
        if (!acked)
        {
            report.LivenessFailure = $"final checkpoint: no write was acknowledged within {options.RecoveryTimeout.TotalSeconds} s " +
                $"after the clients stopped ({DescribeStatuses()})";
            return false;
        }

        return await CheckpointAsync("final checkpoint", null, final: true).ConfigureAwait(false);
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
        if (episode.IsPartition)
            return await PartitionAsync(episode, before, result).ConfigureAwait(false);

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
        await Task.Delay(episode.Hold, bound.Token).ConfigureAwait(false);
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

    // Cuts every link of one node, heals, and waits for the cluster to recover. A leader or follower partition first
    // waits for the majority side to have a leader (in a new term, if the leader was isolated) that acknowledges writes,
    // then holds; a mid-election partition heals after its hold, whatever the majority side has done. While the node is
    // cut, a probe client writes to it, and the partition oracle records every write sent to it.
    private async Task<bool> PartitionAsync(Episode episode, NodeStatus before, EpisodeReport result)
    {
        var isolated = episode.Kind is FaultKind.FollowerPartition
            ? Schedule.PickFollower(victims, nodes.Length, before.Id)
            : before.Id;

        int? leak = options.Injection is NodeInjection.PartitionLeak
            ? Enumerable.Range(0, nodes.Length).First(i => i != isolated)
            : null;

        var strict = episode.Kind is not FaultKind.PartitionMidElection;
        var majority = Enumerable.Range(0, nodes.Length).Where(i => i != isolated).ToArray();
        result.Victims = [isolated];
        var cutAt = clock.Elapsed;
        result.CutPeers = partitions.Begin(episode.Number, isolated, strict, () => network.Isolate(isolated, leak));
        Log($"episode {episode.Number}: node {isolated} cut from node(s) {string.Join(',', result.CutPeers)}");

        var acksAtCut = MajorityAcknowledged();
        using var stopProbe = new CancellationTokenSource();
        clients.Add(ClientAsync(ProbeClient + episode.Number, stopProbe.Token, () => isolated));
        try
        {
            if (episode.Kind is FaultKind.PartitionMidElection)
            {
                await WaitUntilAsync(TrackStepDown, episode.Hold).ConfigureAwait(false);
            }
            else
            {
                var majorityReady = await WaitUntilAsync(
                    () => TrackStepDown() || (MajorityLeader() is not null && MajorityAcknowledged() - acksAtCut >= CampaignOptions.MinAcknowledgedAfterRecovery),
                    options.RecoveryTimeout).ConfigureAwait(false);

                if (HasPartitionViolation(episode, result))
                    return false;

                if (!majorityReady)
                {
                    result.Outcome = "liveness failure";
                    report.LivenessFailure = $"episode {episode.Number} ({episode.Name}): within {options.RecoveryTimeout.TotalSeconds} s of " +
                        $"cutting node {isolated}, the majority side did not elect a leader{(episode.Kind is FaultKind.LeaderPartition ? " in a new term" : "")} " +
                        $"and acknowledge {CampaignOptions.MinAcknowledgedAfterRecovery} writes; {MajorityAcknowledged() - acksAtCut} acknowledged " +
                        $"({DescribeStatuses()})";
                    return false;
                }

                if (MajorityLeader() is { } majorityLeader)
                {
                    result.MajorityLeader = majorityLeader.Id;
                    result.MajorityTerm = majorityLeader.Term;
                }

                await WaitUntilAsync(TrackStepDown, episode.Hold).ConfigureAwait(false);
            }

            if (HasPartitionViolation(episode, result))
                return false;

            result.AcknowledgedDuringPartition = MajorityAcknowledged() - acksAtCut;
            result.NewLeaderAtHeal = leader is var current and >= 0 && current != isolated && statuses[current]?.Term > before.Term;
        }
        finally
        {
            // Heal only once every write recorded for the isolated node has its outcome: one completed after the heal
            // could be acknowledged legitimately by the healed node.
            await stopProbe.CancelAsync().ConfigureAwait(false);
            var late = await partitions.EndAsync(network.Heal, NodeProcess.ControlTimeout + DrainGrace, bound.Token).ConfigureAwait(false);
            if (late > 0)
                Log($"episode {episode.Number}: {late} write(s) to node {isolated} had no outcome at the heal and are only counted");

            result.PartitionSeconds = Math.Round((clock.Elapsed - cutAt).TotalSeconds, 3);
        }

        Log($"episode {episode.Number}: healed after {result.PartitionSeconds:F1} s");

        // Recovery: every node answers, a leader is elected, the isolated leader no longer leads in its old term, and
        // the cluster acknowledges new writes.
        var started = clock.Elapsed;
        var acksAtHeal = Interlocked.Read(in acknowledged);
        var recovered = await WaitUntilAsync(
            () => partitions.Violation is not null
                  || (statuses.All(static s => s is not null) && leader >= 0
                      && (episode.Kind is not FaultKind.LeaderPartition || statuses[isolated] is not { IsLeader: true } s || s.Term > before.Term)
                      && Interlocked.Read(in acknowledged) - acksAtHeal >= CampaignOptions.MinAcknowledgedAfterRecovery),
            options.RecoveryTimeout).ConfigureAwait(false);

        if (HasPartitionViolation(episode, result))
            return false;

        if (!recovered)
        {
            result.Outcome = "liveness failure";
            report.LivenessFailure = $"episode {episode.Number} ({episode.Name}): within {options.RecoveryTimeout.TotalSeconds} s of " +
                $"healing the partition of node {isolated}, the cluster did not answer, elect a leader" +
                $"{(episode.Kind is FaultKind.LeaderPartition ? ", step the old leader down" : "")} and acknowledge " +
                $"{CampaignOptions.MinAcknowledgedAfterRecovery} writes; {Interlocked.Read(in acknowledged) - acksAtHeal} acknowledged " +
                $"({DescribeStatuses()})";
            return false;
        }

        result.RecoverySeconds = Math.Round((clock.Elapsed - started).TotalSeconds, 3);
        if (await CurrentLeaderAsync().ConfigureAwait(false) is { } after)
        {
            result.LeaderAfter = after.Id;
            result.TermAfter = after.Term;
        }

        return true;

        long MajorityAcknowledged() => majority.Sum(i => Interlocked.Read(in acknowledgedBy[i]));

        // A leader on the majority side; in a new term if the leader was isolated.
        NodeStatus? MajorityLeader()
            => majority.Select(i => Volatile.Read(in statuses[i]))
                .Where(s => s is { IsLeader: true } && (episode.Kind is FaultKind.FollowerPartition || s.Term > before.Term))
                .MaxBy(static s => s!.Term);

        // Records when the isolated leader stops reporting that it leads; true stops the wait on a violation.
        bool TrackStepDown()
        {
            if (result.StepDownSeconds is null && isolated == before.Id && statuses[isolated] is { IsLeader: false })
                result.StepDownSeconds = Math.Round((clock.Elapsed - cutAt).TotalSeconds, 3);

            return partitions.Violation is not null;
        }
    }

    private bool HasPartitionViolation(Episode episode, EpisodeReport result)
    {
        if (partitions.Violation is not { } violation)
            return false;

        var name = $"episode {episode.Number} ({episode.Name})";
        result.Outcome = "safety violation";
        report.Violation = ViolationReport.Create(name, violation);
        Log($"{name}: safety violation ({violation.Oracle})");
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
    /// <param name="final"><see langword="true"/> if the clients are stopped and the last acknowledged write is the barrier.</param>
    /// <returns><see langword="false"/> if the run must stop.</returns>
    private async Task<bool> CheckpointAsync(string name, EpisodeReport? result, bool final = false)
    {
        // The cutoff: every acknowledgment counted so far is checked here, the later ones at the next checkpoint. The
        // batch stays pending until it reaches the checker, so that history.json of a failed checkpoint still holds it.
        int batch;
        long mark;
        lock (ackSync)
        {
            batch = pendingAcks.Count;
            mark = Interlocked.Read(in acknowledged);
        }

        // Every write acknowledged before the cutoff is committed at or below the commit index of the leader that
        // acknowledges a write submitted after it. Wait for such a write, then for every node to apply up to the
        // highest commit index that a status polled after it reports. In the final checkpoint no write is acknowledged
        // after the cutoff, and the last acknowledged write is that barrier: it is at or below the commit index of the
        // leader that acknowledged it.
        var barrier = final ? mark - 1L : mark;
        var barrierSeen = false;
        var target = 0L;
        var caughtUp = await WaitUntilAsync(() =>
        {
            if (statuses.Any(static s => s is null))
                return false;

            if (!barrierSeen)
            {
                // The statuses of the next poll are fresh.
                barrierSeen = Volatile.Read(in ackBarrier) >= barrier;
                return false;
            }

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
                if (!await IngestAsync(i, bound.Token).ConfigureAwait(false) || feed.Current(i).Count < target)
                {
                    if (result is not null)
                        result.Outcome = "liveness failure";

                    report.LivenessFailure = $"{name}: node {i} did not return its history up to index {target} (has {feed.Current(i).Count})";
                    return false;
                }
            }

            ReadClaims();
            List<(int Node, WriteKey Key)> acks;
            lock (ackSync)
            {
                acks = pendingAcks[..batch];
                pendingAcks.RemoveRange(0, batch);
            }

            foreach (var (node, key) in acks)
                checker.OnAcknowledged(node, key);

            var histories = Enumerable.Range(0, nodes.Length).Select(feed.Current).ToArray();
            checker.CheckFinal(histories);
            if (checker.Violation is null && RecoveryAudit.Check(checker.Acknowledged, histories) is { } durability)
                checker.Report(durability);

            partitions.Check(histories);
            if (checker.Violation is null && partitions.Violation is { } minority)
                checker.Report(minority);

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

        if (!await SampleResourcesAsync(name).ConfigureAwait(false))
        {
            if (result is not null)
                result.Outcome = "liveness failure";

            return false;
        }

        Log($"{name}: oracles passed at commit index {target}, {checker.Acknowledged.Count} acknowledged writes checked");
        return true;
    }

    private async Task<bool> SampleResourcesAsync(string checkpoint)
    {
        var samples = new NodeResourceSample[nodes.Length];
        for (var i = 0; i < nodes.Length; i++)
        {
            var status = await nodes[i].GetStatusAsync(bound.Token).ConfigureAwait(false);
            var usage = await nodes[i].GetResourcesAsync(bound.Token).ConfigureAwait(false);
            if (status is null || usage is null || status.Pid != usage.Pid)
            {
                report.LivenessFailure = $"{checkpoint}: node {i} did not return a resource sample for its current process";
                return false;
            }

            samples[i] = new()
            {
                Id = i,
                Incarnation = nodes[i].Incarnations,
                LastEntryIndex = status.LastEntryIndex,
                CommitIndex = status.CommitIndex,
                AppliedIndex = status.AppliedIndex,
                SnapshotIndex = status.SnapshotIndex,
                Usage = usage,
            };
        }

        var sample = new ResourceSample
        {
            Checkpoint = checkpoint,
            ElapsedSeconds = clock.Elapsed.TotalSeconds,
            Acknowledged = Interlocked.Read(in acknowledged),
            FreeDiskBytes = EnvironmentInfo.GetFreeBytes(options.OutputDirectory)
                ?? throw new IOException("the output volume's free space could not be measured"),
            Driver = ResourceUsage.Capture(),
            Nodes = samples,
            Proxy = network.Statistics,
            RunningClientTasks = clients.Count(static t => !t.IsCompleted),
        };
        report.Resources.Add(sample);
        File.AppendAllText(Path.Combine(options.OutputDirectory, "resources.jsonl"),
            JsonSerializer.Serialize(sample, ControlApi.Json) + "\n");
        return true;
    }

    private void Cancel(string reason)
    {
        report.Incomplete ??= $"the run was stopped: {reason}";
        bound.Cancel();
    }

    // A failed background snapshot is dropped and logged by HistoryStateMachine only through Trace, which the
    // classifier does not see, so the driver counts the failures that the nodes report in their status. No injected
    // fault explains one: a snapshot canceled by the disposal of the state machine (SIGTERM) is not reported.
    private bool HasUnexpectedSignals()
        => classifier.Unexpected.Any() || nodes.Any(static n => n.UnexpectedExits.Count > 0) || SnapshotFailures() > 0;

    private void RecordSnapshotFailures(int node, NodeStatus status)
    {
        lock (failuresSync)
        {
            var failures = snapshotFailures[node];
            failures[status.Incarnation] = int.Max(failures.GetValueOrDefault(status.Incarnation), status.SnapshotFailures);
        }
    }

    private int SnapshotFailures(int node)
    {
        lock (failuresSync)
            return snapshotFailures[node].Values.Sum();
    }

    private int SnapshotFailures() => Enumerable.Range(0, nodes.Length).Sum(SnapshotFailures);

    private async Task MonitorAsync(CancellationToken token)
    {
        var lastIngest = TimeSpan.Zero;
        while (!token.IsCancellationRequested)
        {
            try
            {
                await PollStatusAsync(token).ConfigureAwait(false);
                if (EnvironmentInfo.GetFreeBytes(options.OutputDirectory) < MinFreeBytes)
                    Cancel("less than 1 GiB free in the output directory");
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
            if (polled[i] is { } status)
                RecordSnapshotFailures(i, status);

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

    // drain stops the client after its request in flight; the campaign's stop token aborts it. The client writes to the
    // leader, or to the node that pick returns.
    private async Task ClientAsync(int client, CancellationToken drain, Func<int>? pick = null)
    {
        var token = stop.Token;
        var payload = new Payload(options.PayloadSize);
        var buffer = payload.CreateBuffer();
        var seq = 0L;
        while (!drain.IsCancellationRequested && !token.IsCancellationRequested)
        {
            try
            {
                var target = pick?.Invoke() ?? leader;
                if (target < 0)
                {
                    await Task.Delay(20, token).ConfigureAwait(false);
                    continue;
                }

                // A new sequence number for every attempt: the oracle requires a closed-loop client to send its next
                // write only after the outcome of the previous one is known or can no longer change.
                var key = new WriteKey(WriteKey.ClosedLoop, client, ++seq);
                if (Interlocked.Increment(ref submittedWrites) > options.MaxWrites)
                {
                    Cancel($"the submitted-write bound of {options.MaxWrites} was reached");
                    break;
                }

                var submitted = Interlocked.Read(in acknowledged);
                await partitions.OnSubmittingAsync(target, key, token).ConfigureAwait(false);
                var outcome = await nodes[target].WriteAsync(payload.Write(buffer, key), token).ConfigureAwait(false);
                partitions.OnOutcome(key, outcome);
                switch (outcome)
                {
                    case WriteOutcome.Acknowledged:
                        lock (ackSync)
                        {
                            pendingAcks.Add((target, key));
                            Interlocked.Increment(ref acknowledged);
                            Interlocked.Increment(ref acknowledgedBy[target]);
                            if (submitted > ackBarrier)
                                Volatile.Write(ref ackBarrier, submitted);
                        }

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
    // timeout expires. Throws OperationCanceledException at the bound of the run.
    private async Task<bool> WaitUntilAsync(Func<bool> condition, TimeSpan timeout, bool freshStatus = false)
    {
        var deadline = clock.Elapsed + timeout;
        while (true)
        {
            if (freshStatus)
                await PollStatusAsync(bound.Token).ConfigureAwait(false);

            // A failed monitor leaves the statuses stale: report the failure rather than a liveness failure.
            if (monitor is { IsFaulted: true } failed)
                await failed.ConfigureAwait(false);

            if (condition())
                return true;

            if (clock.Elapsed > deadline)
                return false;

            await Task.Delay(StatusInterval, bound.Token).ConfigureAwait(false);
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
        report.Workload.Submitted = long.Min(Interlocked.Read(in submittedWrites), options.MaxWrites);
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
                SnapshotFailures = SnapshotFailures(node.Id),
            });
        }

        report.Signals.LogLines = classifier.Lines;
        report.Signals.SnapshotFailures = SnapshotFailures();
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

        report.Proxy = network.Statistics;
        foreach (var episode in report.Episodes.Where(static e => e.CutPeers is not null))
            episode.MinorityWrites = partitions.Count(episode.Number);

        if (report.Violation is null && (checker.Violation ?? partitions.Violation) is { } violation)
            report.Violation = ViolationReport.Create("shutdown", violation);

        // A run passes only if the final checkpoint audited every acknowledged write.
        (int Node, WriteKey Key)[] pending;
        lock (ackSync)
            pending = [.. pendingAcks];

        var unaudited = pending.Length;

        if (unaudited > 0 && report is { Error: null, Violation: null, LivenessFailure: null, Incomplete: null } && !HasUnexpectedSignals())
            report.Error = $"{unaudited} acknowledged writes were not audited";

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
            // An acknowledgment that no checkpoint audited (the run stopped first) has no index field.
            acknowledged = checker.Acknowledged.Select(static a => new { key = a.Key.ToString(), node = a.Node, index = (long?)a.Index })
                .Concat(pending.Select(static a => new { key = a.Key.ToString(), node = a.Node, index = (long?)null })),
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

        foreach (var node in report.Nodes.Where(static n => n.SnapshotFailures > 0))
            Log($"snapshot failures: node {node.Id} dropped {node.SnapshotFailures} failed background snapshots");

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

        network.Dispose();

        feedLock.Dispose();
        stop.Dispose();
        drain.Dispose();
        bound.Dispose();
    }
}
