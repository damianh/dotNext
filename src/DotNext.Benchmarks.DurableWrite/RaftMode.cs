using System.Diagnostics;
using System.Net;
using System.Net.Sockets;
using DotNext.Benchmarks.DurableWrite.Oracles;
using DotNext.Net.Cluster.Consensus.Raft;
using DotNext.Net.Cluster.Consensus.Raft.InProcess;
using DotNext.Net.Cluster.Consensus.Raft.Membership;
using DotNext.Net.Cluster.Consensus.Raft.StateMachine;

namespace DotNext.Benchmarks.DurableWrite;

/// <summary>
/// Replicated cells: one <see cref="RaftCluster"/> per voter in this process, talking over loopback TCP, each with
/// its own <see cref="WriteAheadLog"/> and <see cref="HistoryStateMachine"/> on disk.
/// </summary>
/// <remarks>
/// The nodes share the process, the CPU and the device, so the numbers include the real transport, serialization
/// and one durable append per node per entry, but not network latency or the isolation of separate hosts.
/// </remarks>
internal static class RaftMode
{
    private const int MaxOpenLoopInFlight = 4096;
    private static readonly TimeSpan ElectionTimeout = TimeSpan.FromSeconds(30);
    private static readonly TimeSpan CatchUpTimeout = TimeSpan.FromSeconds(60);
    private static readonly TimeSpan StopTimeout = TimeSpan.FromSeconds(30);
    private static readonly TimeSpan RecoveryTimeout = TimeSpan.FromMinutes(2);
    private static readonly TimeSpan RelayDelay = TimeSpan.FromMilliseconds(5);
    private static readonly TimeSpan PauseDuration = TimeSpan.FromSeconds(5);

    internal static async Task RunAsync(CellRun run, FsyncProbe? probe, (double Rate, string Cell)? reference)
    {
        var spec = run.Spec;
        var nodes = new Node[spec.Voters];
        try
        {
            await CreateNodesAsync(run, nodes).ConfigureAwait(false);
            await Task.WhenAll(nodes.Select(n => n.Cluster.StartAsync(run.RunToken))).ConfigureAwait(false);
            var leader = await WaitForLeaderAsync(run, nodes).ConfigureAwait(false);

            if (spec.Injection is FailureInjection.DropApplied or FailureInjection.ReorderApplied)
                nodes.First(n => n != leader && n.StateMachine is not null).StateMachine!.Arm(spec.Injection);

            Task? pause = null;
            var phases = run.RunPhasesAsync(() => Sample(run, nodes), onMeasuring: () =>
            {
                if (nodes.FirstOrDefault(static n => n.Relay is not null)?.Relay is { } relay)
                    pause = PauseAsync(run, relay);
            });

            Task[] workers;
            if (spec.Kind is CellKind.RaftOpen)
            {
                var rate = Math.Max(1D, spec.OpenLoopFraction * (reference?.Rate ?? 1000D));
                run.Report.OfferedRatePerSecond = Math.Round(rate, 1);
                run.Report.OfferedRateReference = reference is { Cell: var cell, Rate: var completed }
                    ? $"{spec.OpenLoopFraction:P0} of {cell} ({completed:F1}/s)"
                    : $"{spec.OpenLoopFraction:P0} of 1000/s (no reference cell in this run)";
                workers = [OpenLoopAsync(run, nodes, rate)];
            }
            else
            {
                workers = Enumerable.Range(0, spec.Concurrency).Select(client => ClosedLoopAsync(run, nodes, client)).ToArray();
            }

            try
            {
                await Task.WhenAll(workers).ConfigureAwait(false);
            }
            finally
            {
                run.Stop("worker");
                await phases.ConfigureAwait(false);
                if (pause is not null)
                    await pause.ConfigureAwait(false);
            }

            run.CompleteLoad(probe);
            run.Report.LeaderChanges = nodes.Sum(static n => n.LeaderClaims);

            // A slow follower must have fallen behind the compacted log and installed a snapshot while under load.
            var slowNode = nodes.FirstOrDefault(static n => n.Relay is not null);
            var slowRestores = slowNode?.StateMachine?.Restores;

            leader = FindLeader(nodes) ?? leader;
            await CatchUpAsync(run, nodes, leader).ConfigureAwait(false);

            run.Report.Oracles.Checked.AddRange([
                OnlineHistoryChecker.ApplyOrder, OnlineHistoryChecker.PrefixAgreement,
                OnlineHistoryChecker.AcknowledgedWrites, OnlineHistoryChecker.ElectionSafety]);
            run.Checker.CheckFinal(nodes.Select(static n => n.StateMachine?.History).ToArray());

            // What each log applied past the end of its history, read while the nodes still run.
            var applied = new (IReadOnlyList<AppliedEntry> History, long Index, IReadOnlyList<AppliedEntry> Tail)?[nodes.Length];
            foreach (var node in nodes)
            {
                if (node is { Wal: { } wal, StateMachine: { } stateMachine })
                {
                    var appliedIndex = wal.LastAppliedIndex;
                    var history = stateMachine.History;
                    applied[node.Id] = (history, appliedIndex,
                        await NodeStorage.ReadAsync(wal, history.Count + 1L, appliedIndex, run.RunToken).ConfigureAwait(false));
                }
            }

            await StopAsync(nodes).ConfigureAwait(false);

            var durableLogs = new IReadOnlyList<AppliedEntry>[nodes.Length];
            foreach (var node in nodes)
            {
                durableLogs[node.Id] = node.Durable
                    ? await NodeStorage.RecoverAsync(node.Root, spec.Memory, spec.NoBuffering, node.Id, RecoveryTimeout, run.RunToken).ConfigureAwait(false)
                    : [];
            }

            run.CheckDurability(durableLogs);
            foreach (var node in nodes)
            {
                if (applied[node.Id] is { } state)
                    run.Reconcile(node.Id, state.History, state.Index, state.Tail, node.Durable ? durableLogs[node.Id] : null);
            }

            foreach (var node in nodes)
            {
                var report = run.AddNode(node.Id, node.Role(leader), node.Durable, node.Durable ? node.Root : null, node.LastIndex,
                    node.StateMachine, durableLogs[node.Id].Count);
                report.RelayedBytes = node.RelayedBytes;
            }

            run.CompleteWriteAmplification(leader.Id);

            // Checked last, so a run that also broke a safety oracle reports the violation.
            if (slowNode is not null && slowRestores is 0)
            {
                throw new LivenessFailureException(
                    $"slow follower node {slowNode.Id} installed no snapshot during the load window; the cell did not exercise snapshot transfer");
            }
        }
        finally
        {
            await StopAsync(nodes).ConfigureAwait(false);
        }
    }

    private static async Task CreateNodesAsync(CellRun run, Node[] nodes)
    {
        var spec = run.Spec;
        var slow = spec.Kind is CellKind.SlowFollower ? spec.Voters - 1 : -1;
        var nonDurable = spec.Injection is FailureInjection.NonDurableFollowers;

        for (var id = 0; id < nodes.Length; id++)
        {
            var host = new IPEndPoint(IPAddress.Loopback, GetFreePort());
            DelayRelay? relay = null;
            if (id == slow)
            {
                relay = new(host, RelayDelay);
                relay.Start();
            }

            nodes[id] = new(id, Path.Combine(run.Root, $"node{id}"), host, relay)
            {
                // The slow follower and the non-durable followers never start an election, so the pause cannot
                // disrupt the cluster and the durable node leads.
                Standby = id == slow || (nonDurable && id > 0),
            };
        }

        foreach (var node in nodes)
        {
            IPersistentState state;
            if (nonDurable && node.Id > 0)
            {
                // Injected failure: a voter that acknowledges entries it keeps in memory only.
                state = new ConsensusOnlyState();
            }
            else
            {
                var stateMachine = NodeStorage.CreateStateMachine(node.Root, node.Id, spec.SnapshotInterval, run.Checker, run.Timeline);
                node.StateMachine = stateMachine;
                await stateMachine.RestoreAsync(run.RunToken).ConfigureAwait(false);
                node.Wal = new(NodeStorage.CreateOptions(node.Root, spec.Memory, spec.NoBuffering, node.Id), stateMachine);
                state = node.Wal;
            }

            var configuration = new RaftCluster.TcpConfiguration(node.Host)
            {
                ColdStart = nodes.Length is 1,
                PublicEndPoint = node.Public,
                Standby = node.Standby,
                LowerElectionTimeout = 1000,
                UpperElectionTimeout = 2000,
                RequestTimeout = TimeSpan.FromSeconds(3),
                ConfigurationStorage = null,
            };

            if (nodes.Length > 1)
            {
                var builder = ((InMemoryClusterConfigurationStorage<EndPoint>)configuration.ConfigurationStorage!).CreateInitialConfigurationBuilder();
                foreach (var member in nodes)
                    builder.Add(member.Public);

                builder.Build();
            }

            var cluster = new RaftCluster(configuration) { AuditTrail = state };
            var id = node.Id;
            cluster.LeaderChanged += (sender, leader) =>
            {
                if (leader is { IsRemote: false })
                {
                    node.OnLeaderClaim();
                    run.Checker.OnLeaderClaim(id, sender.AuditTrail.Term);
                }
            };

            node.Cluster = cluster;
        }
    }

    private static int GetFreePort()
    {
        using var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        return ((IPEndPoint)listener.LocalEndpoint).Port;
    }

    private static Node? FindLeader(Node[] nodes)
    {
        foreach (var node in nodes)
        {
            try
            {
                if (!node.Cluster.LeadershipToken.IsCancellationRequested)
                    return node;
            }
            catch (Exception e) when (e is ObjectDisposedException or InvalidOperationException)
            {
                // the node is stopping
            }
        }

        return null;
    }

    private static async Task<Node> WaitForLeaderAsync(CellRun run, Node[] nodes)
    {
        var deadline = Stopwatch.GetTimestamp() + (long)(ElectionTimeout.TotalSeconds * Stopwatch.Frequency);
        while (true)
        {
            if (FindLeader(nodes) is { } leader)
                return leader;

            if (Stopwatch.GetTimestamp() > deadline)
                throw new LivenessFailureException($"no leader was elected within {ElectionTimeout.TotalSeconds:F0} s");

            await Task.Delay(10, run.RunToken).ConfigureAwait(false);
        }
    }

    private static BacklogSample? Sample(CellRun run, Node[] nodes)
    {
        if (FindLeader(nodes) is not { Wal: { } wal } leader)
            return null;

        var minApplied = long.MaxValue;
        var maxApplied = 0L;
        foreach (var node in nodes)
        {
            if (node.Wal is null)
                continue;

            var applied = run.Checker.LastApplied(node.Id);
            minApplied = long.Min(minApplied, applied);
            maxApplied = long.Max(maxApplied, applied);
        }

        var state = leader.Cluster.AuditTrail;
        return new(
            state.LastEntryIndex - state.LastCommittedEntryIndex,
            long.Max(0L, state.LastCommittedEntryIndex - wal.LastAppliedIndex),
            maxApplied - minApplied);
    }

    // Holds the traffic to the slow follower once, from 30% of the measured window, so that it falls behind the
    // snapshots of the leader and has to install one.
    private static async Task PauseAsync(CellRun run, DelayRelay relay)
    {
        try
        {
            await Task.Delay(run.Spec.Duration * 0.3, run.StopToken).ConfigureAwait(false);
            relay.Pause(PauseDuration);
        }
        catch (OperationCanceledException)
        {
            // the cell stopped first
        }
    }

    private static async Task ClosedLoopAsync(CellRun run, Node[] nodes, int client)
    {
        await Task.Yield();
        var buffer = run.Payload.CreateBuffer();
        for (var seq = 1L; !run.IsStopped;)
        {
            if (FindLeader(nodes) is not { } leader)
            {
                await Delay(run).ConfigureAwait(false);
                continue;
            }

            var key = new WriteKey(WriteKey.ClosedLoop, client, seq);
            run.OnOffered();
            var start = Stopwatch.GetTimestamp();
            switch (await ReplicateAsync(run, leader, key, buffer).ConfigureAwait(false))
            {
                case Outcome.Acknowledged:
                    run.OnAcknowledged(1, start, Stopwatch.GetTimestamp());
                    seq++;
                    break;
                case Outcome.Unknown:
                    // The write may still be committed: the next write gets a new sequence number.
                    seq++;
                    await Delay(run).ConfigureAwait(false);
                    break;
                case Outcome.Rejected:
                    await Delay(run).ConfigureAwait(false);
                    break;
                default:
                    return;
            }
        }
    }

    // Writes are offered at a fixed rate, whatever the cluster does. The latency is measured from the time a write
    // was due, so a stall is charged to every write it delayed (no coordinated omission).
    private static async Task OpenLoopAsync(CellRun run, Node[] nodes, double rate)
    {
        await Task.Yield();
        var pending = new List<Task>();
        var started = Stopwatch.GetTimestamp();
        var ticksPerWrite = Stopwatch.Frequency / rate;
        var inFlight = 0;
        var seq = 0L;
        for (var k = 0L; !run.IsStopped; k++)
        {
            var due = started + (long)(k * ticksPerWrite);
            var wait = due - Stopwatch.GetTimestamp();
            if (wait > 0L)
            {
                try
                {
                    await Task.Delay(TimeSpan.FromSeconds((double)wait / Stopwatch.Frequency), run.StopToken).ConfigureAwait(false);
                }
                catch (OperationCanceledException)
                {
                    break;
                }
            }

            run.OnOffered();
            if (Volatile.Read(in inFlight) >= MaxOpenLoopInFlight)
            {
                run.OnOverloaded();
                continue;
            }

            Interlocked.Increment(ref inFlight);
            var key = new WriteKey(WriteKey.OpenLoop, 0, ++seq);
            pending.Add(SendAsync(key, due));
            if (pending.Count >= 1024)
            {
                // Await the completed sends before dropping them, so a faulted one fails the cell.
                foreach (var task in pending)
                {
                    if (task.IsCompleted)
                        await task.ConfigureAwait(false);
                }

                pending.RemoveAll(static t => t.IsCompleted);
            }
        }

        await Task.WhenAll(pending).ConfigureAwait(false);

        async Task SendAsync(WriteKey key, long due)
        {
            var buffer = run.Payload.Rent();
            try
            {
                for (var attempt = 0; !run.IsStopped; attempt++)
                {
                    if (FindLeader(nodes) is not { } leader)
                    {
                        await Delay(run).ConfigureAwait(false);
                        continue;
                    }

                    switch (await ReplicateAsync(run, leader, key, buffer).ConfigureAwait(false))
                    {
                        case Outcome.Acknowledged:
                            run.OnAcknowledged(1, due, Stopwatch.GetTimestamp());
                            return;
                        case Outcome.Rejected:
                            // Not appended anywhere, so the same write can be sent again.
                            await Delay(run).ConfigureAwait(false);
                            continue;
                        default:
                            return;
                    }
                }
            }
            finally
            {
                System.Buffers.ArrayPool<byte>.Shared.Return(buffer);
                Interlocked.Decrement(ref inFlight);
            }
        }
    }

    private enum Outcome
    {
        Acknowledged,
        Rejected,
        Unknown,
        Stopped,
    }

    private static async Task<Outcome> ReplicateAsync(CellRun run, Node leader, WriteKey key, byte[] buffer)
    {
        var cluster = leader.Cluster;
        var entry = new BinaryLogEntry { Term = cluster.AuditTrail.Term, Content = run.Payload.Write(buffer, key) };
        run.BeginRequest();
        try
        {
            // In-flight writes complete with the run token, not the stop token, so their outcome is known.
            await cluster.ReplicateAsync(entry, run.RunToken).ConfigureAwait(false);
        }
        catch (NotLeaderException e)
        {
            if (e.InnerException is null)
            {
                run.OnRejected();
                return Outcome.Rejected;
            }

            run.OnUnknown();
            return Outcome.Unknown;
        }
        catch (OperationCanceledException) when (run.RunToken.IsCancellationRequested)
        {
            return Outcome.Stopped;
        }
        catch (ObjectDisposedException)
        {
            return Outcome.Stopped;
        }
        finally
        {
            run.EndRequest();
        }

        run.Checker.OnAcknowledged(leader.Id, key);
        return Outcome.Acknowledged;
    }

    private static async Task Delay(CellRun run)
    {
        try
        {
            await Task.Delay(10, run.StopToken).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            // the cell stopped
        }
    }

    // Every node with a state machine must apply what the leader committed, before the histories are compared.
    private static async Task CatchUpAsync(CellRun run, Node[] nodes, Node leader)
    {
        var target = leader.Cluster.AuditTrail.LastCommittedEntryIndex;
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(run.RunToken);
        timeout.CancelAfter(CatchUpTimeout);
        foreach (var node in nodes)
        {
            if (node.Wal is not { } wal)
                continue;

            try
            {
                await wal.WaitForApplyAsync(target, timeout.Token).ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (!run.RunToken.IsCancellationRequested)
            {
                throw new LivenessFailureException(
                    $"node {node.Id} applied {wal.LastAppliedIndex} of {target} committed entries {CatchUpTimeout.TotalSeconds:F0} s after the workload");
            }
        }
    }

    private static async Task StopAsync(Node[] nodes)
    {
        foreach (var node in nodes)
        {
            if (node is not null)
                await node.DisposeAsync().ConfigureAwait(false);
        }
    }

    private sealed class Node(int id, string root, IPEndPoint host, DelayRelay? relay) : IAsyncDisposable
    {
        private int leaderClaims;
        private bool disposed;

        internal int Id => id;

        internal string Root => root;

        internal IPEndPoint Host => host;

        // The address the other nodes use: the relay of a slow follower, otherwise the node itself.
        internal IPEndPoint Public => relay?.EndPoint ?? host;

        internal DelayRelay? Relay => relay;

        internal bool Standby { get; init; }

        internal HistoryStateMachine? StateMachine { get; set; }

        internal WriteAheadLog? Wal { get; set; }

        internal bool Durable => Wal is not null;

        internal RaftCluster Cluster { get; set; } = null!;

        internal long LastIndex { get; private set; }

        internal long? RelayedBytes { get; private set; }

        internal int LeaderClaims => Volatile.Read(in leaderClaims);

        internal void OnLeaderClaim() => Interlocked.Increment(ref leaderClaims);

        internal string Role(Node leader) => (this == leader ? "leader" : "follower")
            + (relay is not null ? " (slow)" : string.Empty)
            + (Durable ? string.Empty : " (non-durable)");

        public async ValueTask DisposeAsync()
        {
            if (disposed)
                return;

            disposed = true;
            if (Cluster is { } cluster)
            {
                using (var timeout = new CancellationTokenSource(StopTimeout))
                {
                    try
                    {
                        await cluster.StopAsync(timeout.Token).ConfigureAwait(false);
                    }
                    catch (Exception e) when (e is OperationCanceledException or ObjectDisposedException or InvalidOperationException)
                    {
                        // already stopped, or stopping took too long; disposal releases the rest
                    }
                }

                await cluster.DisposeAsync().ConfigureAwait(false);
            }

            if (Wal is { } wal)
            {
                LastIndex = wal.LastEntryIndex;
                await wal.DisposeAsync().ConfigureAwait(false);
            }

            if (StateMachine is { } stateMachine)
                await stateMachine.DisposeAsync().ConfigureAwait(false);

            if (relay is not null)
            {
                RelayedBytes = relay.Bytes;
                await relay.DisposeAsync().ConfigureAwait(false);
            }
        }
    }
}
