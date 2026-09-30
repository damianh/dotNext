using System.Collections.Concurrent;
using System.Diagnostics;
using System.Net;
using System.Reflection;
using System.Text;

namespace DotNext.Net.Cluster.Consensus.Raft.InProcess;

using IO;
using StateMachine;

/// <summary>
/// One seeded run of the in-process simulation: a bounded schedule of faults and client proposals against WAL-backed
/// <see cref="InProcessCluster"/> nodes, followed by a separate liveness phase.
/// </summary>
/// <remarks>
/// What the seed controls: every scheduler decision (which pending message is delivered, dropped or has its response lost,
/// which link is cut, how far time advances, who is asked to propose, who crashes) and the per-node election timeouts.
/// What it does not: thread-pool scheduling inside a step, the order in which continuations run after a delivery or a timer,
/// the WAL's background work, and the wall-clock settle between steps. A seed is therefore not a guarantee of replay;
/// the trace is the record of what actually happened.
/// </remarks>
internal sealed class Simulation : IAsyncDisposable
{
    private const int MaxCrashes = 3;
    private const int CheckpointEvery = 10;
    private const int LivenessIterations = 400;

    // Compaction is out of scope: a compacted prefix is read back as one empty term-0 snapshot entry, which would
    // break the index-to-entry mapping used by the oracles.
    private const long NoCompaction = 1_000_000L;
    private static readonly TimeSpan Guard = TimeSpan.FromSeconds(20);
    private static readonly TimeSpan DeliveryWait = TimeSpan.FromMilliseconds(200);
    private static readonly int[] ElectionTimeoutsMs = [100, 130, 160, 190, 220];
    private static readonly int[] AdvanceMs = [5, 10, 25, 50, 100, 150];
    private static string revision;

    private readonly long seed;
    private readonly int steps;
    private readonly Random random;
    private readonly ManualTimeProvider clock = new();
    private readonly InProcessNetwork network = new();
    private readonly EndPoint[] membership;
    private readonly Slot[] slots;
    private readonly SimulationHistory history = new();
    private readonly List<string> trace = [];
    private readonly ConcurrentQueue<(int Node, long Term)> claims = new();
    private readonly List<Proposal> proposals = [];
    private readonly List<(string What, Task Task)> inFlight = [];
    private readonly List<(int First, int Second, bool Bidirectional)> partitions = [];
    private readonly CancellationTokenSource lifetime = new();
    private CancellationToken token;
    private TimeSpan elapsed;
    private int crashes;
    private int stepNumber;

    internal Simulation(long seed, int voters, int steps = 150)
    {
        this.seed = seed;
        this.steps = steps;
        Voters = voters;
        random = new(unchecked((int)seed ^ (int)(seed >> 32)));
        membership = Enumerable.Range(0, voters).Select(EndPoint (i) => new DnsEndPoint($"node-{i}", 0)).ToArray();
        var timeouts = ElectionTimeoutsMs.OrderBy(_ => random.Next()).Take(voters).ToArray();
        slots = Enumerable.Range(0, voters).Select(i => new Slot(i, Test.GetTempPath(), timeouts[i])).ToArray();
    }

    internal int Voters { get; }

    internal int AcknowledgedCount => history.AcknowledgedCount;

    internal string Summary
    {
        get
        {
            var counts = proposals.GroupBy(static p => (p.Outcome ?? "pending").Split(' ')[0]).Select(static g => $"{g.Key}={g.Count()}");
            return $"seed={seed} voters={Voters} steps={stepNumber} crashes={crashes} proposals: {string.Join(", ", counts)}";
        }
    }

    internal IReadOnlyList<string> Trace => trace;

    internal static string Revision => revision ??= ResolveRevision();

    /// <summary>
    /// Runs the fault phase, then the liveness phase. Failures carry the seed, revision and trace.
    /// </summary>
    /// <exception cref="SimulationFailureException">A safety oracle, the liveness check or the harness failed.</exception>
    internal async Task RunAsync(CancellationToken cancellationToken)
    {
        token = cancellationToken;
        trace.Add($"seed={seed} voters={Voters} steps={steps} revision={Revision}");
        trace.Add($"election timeouts (ms): {string.Join(", ", slots.Select(static s => $"node-{s.Index}={s.TimeoutMs}"))}");
        try
        {
            await StartAsync().WaitAsync(Guard, token);
            for (stepNumber = 1; stepNumber <= steps; stepNumber++)
            {
                await StepAsync().WaitAsync(Guard, token);
                await SettleAsync();
                await ObserveAsync();
                if (stepNumber % CheckpointEvery is 0)
                    await CheckpointAsync();
            }

            await CheckpointAsync();
        }
        catch (SafetyViolationException e)
        {
            throw Fail("SAFETY", e);
        }
        catch (Exception e) when (e is not OperationCanceledException || !token.IsCancellationRequested)
        {
            throw Fail("HARNESS", e);
        }

        try
        {
            await LivenessAsync();
            await CheckpointAsync();
            history.RequireAcknowledgedWritesPreserved();
        }
        catch (SafetyViolationException e)
        {
            throw Fail("SAFETY", e);
        }
        catch (LivenessFailureException e)
        {
            throw Fail("LIVENESS", e);
        }
        catch (Exception e) when (e is not OperationCanceledException || !token.IsCancellationRequested)
        {
            throw Fail("HARNESS", e);
        }
    }

    private SimulationFailureException Fail(string category, Exception cause)
    {
        Log($"{category} FAILURE: {cause.Message}");
        return new(category, seed, Voters, trace, cause);
    }

    private void Log(string message) => trace.Add($"[{elapsed.TotalMilliseconds,7:0}ms] {message}");

    private async Task StartAsync()
    {
        foreach (var source in membership)
        {
            foreach (var target in membership)
            {
                if (!Equals(source, target))
                    network.Hold(source, target);
            }
        }

        foreach (var slot in slots)
            await OpenAsync(slot);
    }

    private async Task OpenAsync(Slot slot)
    {
        slot.Wal = new(new()
        {
            Location = slot.Location,
            MemoryManagement = WriteAheadLog.MemoryManagementStrategy.PrivateMemory,
            FlushInterval = Timeout.InfiniteTimeSpan,
        }, IStateMachine.CreateNoOp(NoCompaction));
        var node = new InProcessCluster(
            network,
            ((DnsEndPoint)membership[slot.Index]).Host,
            membership,
            slot.Wal,
            clock,
            TimeSpan.FromMilliseconds(slot.TimeoutMs),
            startFollower: true);
        var index = slot.Index;
        node.LeaderChanged += (sender, leader) =>
        {
            if (leader is not null && leader.Id == ((InProcessCluster)sender).Id)
                claims.Enqueue((index, sender.Term));
        };

        slot.Node = node;
        slot.Up = true;
        await node.StartAsync(token);
    }

    private async Task CloseAsync(Slot slot)
    {
        slot.Up = false;
        await slot.Node.StopAsync(token);
        await slot.Node.DisposeAsync();
        slot.Wal.Dispose();
    }

    private async Task StepAsync()
    {
        var pending = SortedPending();
        var up = slots.Where(static s => s.Up).ToArray();
        var down = slots.Where(static s => !s.Up).ToArray();
        var choices = new List<(Kind Kind, int Weight)>();
        if (pending.Length > 0)
        {
            choices.Add((Kind.Deliver, 40));
            choices.Add((Kind.DeliverLoseResponse, 4));
            choices.Add((Kind.Drop, 6));
        }

        choices.Add((Kind.Advance, 18));
        if (up.Length > 0)
            choices.Add((Kind.Propose, 12));

        choices.Add((Kind.Partition, 6));
        if (partitions.Count > 0)
            choices.Add((Kind.Heal, 6));

        if (crashes < MaxCrashes && up.Length > 0)
            choices.Add((Kind.Crash, 2));

        if (down.Length > 0)
            choices.Add((Kind.Recover, 5));

        var roll = random.Next(choices.Sum(static c => c.Weight));
        var kind = choices.First(c => (roll -= c.Weight) < 0).Kind;
        switch (kind)
        {
            case Kind.Deliver:
            case Kind.DeliverLoseResponse:
                await DeliverAsync(pending[random.Next(pending.Length)], kind is Kind.DeliverLoseResponse, pending.Length);
                break;
            case Kind.Drop:
                var dropped = pending[random.Next(pending.Length)];
                Log($"#{stepNumber} drop {Describe(dropped)}");
                network.TryDrop(dropped);
                break;
            case Kind.Advance:
                Advance(AdvanceMs[random.Next(AdvanceMs.Length)], $"#{stepNumber} advance");
                break;
            case Kind.Propose:
                Propose(up);
                break;
            case Kind.Partition:
                var first = random.Next(Voters);
                var second = (first + 1 + random.Next(Voters - 1)) % Voters;
                var bidirectional = random.Next(3) is not 0;
                partitions.Add((first, second, bidirectional));
                network.Partition(membership[first], membership[second], bidirectional);
                Log($"#{stepNumber} partition node-{first} {(bidirectional ? "<->" : "->")} node-{second}");
                break;
            case Kind.Heal:
                var (healFirst, healSecond, healBidirectional) = partitions[random.Next(partitions.Count)];
                partitions.Remove((healFirst, healSecond, healBidirectional));
                network.Heal(membership[healFirst], membership[healSecond], healBidirectional);
                Log($"#{stepNumber} heal node-{healFirst} {(healBidirectional ? "<->" : "->")} node-{healSecond}");
                break;
            case Kind.Crash:
                var victim = up[random.Next(up.Length)];
                await ObserveAsync();
                await CheckpointAsync();
                Log($"#{stepNumber} crash node-{victim.Index} (WAL disposed without a shutdown flush)");
                crashes++;
                await CloseAsync(victim);
                break;
            case Kind.Recover:
                var revived = down[random.Next(down.Length)];
                Log($"#{stepNumber} recover node-{revived.Index} (WAL reopened at the same location)");
                await OpenAsync(revived);
                break;
        }
    }

    private void Advance(int milliseconds, string label)
    {
        Log($"{label} {milliseconds}ms");
        elapsed += TimeSpan.FromMilliseconds(milliseconds);
        clock.Advance(TimeSpan.FromMilliseconds(milliseconds));
    }

    private void Propose(Slot[] up)
    {
        var leaders = up.Where(static s => s.BelievesLeader).ToArray();
        var target = leaders.Length > 0 && random.Next(10) < 7
            ? leaders[random.Next(leaders.Length)]
            : up[random.Next(up.Length)];
        var payload = $"s{seed}-p{proposals.Count + 1}";
        var node = target.Node;
        var term = node.Term;
        ReadOnlyMemory<byte> bytes = Encoding.UTF8.GetBytes(payload);
        var task = ((IRaftCluster)node).ReplicateAsync(bytes, token: lifetime.Token).AsTask();
        proposals.Add(new(payload, target, node, task));
        Log($"#{stepNumber} propose {payload} to node-{target.Index} (term {term}, believes leader: {target.BelievesLeader})");
    }

    private async Task DeliverAsync(PendingMessage message, bool loseResponse, int pendingCount)
    {
        Log($"#{stepNumber} {(loseResponse ? "deliver, lose response" : "deliver")} {Describe(message)} ({pendingCount} pending)");
        try
        {
            var delivery = loseResponse ? network.DeliverAndLoseResponseAsync(message) : network.TryDeliverAsync(message);
            await WaitForDeliveryAsync(delivery, Describe(message));
        }
        catch (InvalidOperationException)
        {
            // the message was removed between listing and delivery
        }
    }

    // A handler may legitimately wait for something the scheduler holds, so a delivery is not awaited indefinitely.
    private async Task WaitForDeliveryAsync(Task delivery, string what)
    {
        using var timeout = new CancellationTokenSource();
        var winner = await Task.WhenAny(delivery, Task.Delay(DeliveryWait, timeout.Token));
        await timeout.CancelAsync();
        if (ReferenceEquals(winner, delivery))
        {
            await delivery;
        }
        else
        {
            Log($"    handler still running after {DeliveryWait.TotalMilliseconds}ms: {what}");
            inFlight.Add((what, delivery));
        }
    }

    private PendingMessage[] SortedPending()
        => network.PendingMessages
            .OrderBy(m => IndexOf(m.SourceId))
            .ThenBy(m => IndexOf(m.TargetId))
            .ThenBy(static m => m.MessageType)
            .ThenBy(static m => m.LastEntryIndex)
            .ThenBy(static m => m.Id)
            .ToArray();

    private int IndexOf(ClusterMemberId id) => Array.FindIndex(slots, s => s.Node.Id == id);

    private string Describe(PendingMessage m)
        => $"{m.MessageType} node-{IndexOf(m.SourceId)}->node-{IndexOf(m.TargetId)}"
           + (m.LastEntryIndex >= 0L ? $" lastEntry={m.LastEntryIndex}" : string.Empty);

    // A wall-clock heuristic: steps are not atomic, so wait until observable progress stops.
    private async Task SettleAsync()
    {
        var watch = Stopwatch.StartNew();
        string previous = null;
        var stable = 0;
        while (stable < 2 && watch.ElapsedMilliseconds < 50L)
        {
            var until = watch.Elapsed + TimeSpan.FromMilliseconds(1);
            while (watch.Elapsed < until)
                await Task.Yield();

            var signature = Signature();
            if (signature == previous)
            {
                stable++;
            }
            else
            {
                stable = 0;
                previous = signature;
            }
        }
    }

    private string Signature()
    {
        var builder = new StringBuilder();
        foreach (var message in network.PendingMessages)
            builder.Append(message.Id).Append(',');

        foreach (var slot in slots.Where(static s => s.Up))
        {
            var node = slot.Node;
            builder.Append('|').Append(node.Term).Append(':').Append(node.AuditTrail.LastEntryIndex)
                .Append(':').Append(node.AuditTrail.LastCommittedEntryIndex).Append(':').Append(slot.BelievesLeader);
        }

        builder.Append('|').Append(proposals.Count(static p => p.Task.IsCompleted)).Append(claims.Count);
        return builder.ToString();
    }

    // Records leadership claims and the outcome of every client operation that has completed.
    private async Task ObserveAsync()
    {
        while (claims.TryDequeue(out var claim))
        {
            Log($"node-{claim.Node} claims leadership of term {claim.Term}");
            history.RecordLeaderClaim(claim.Node, claim.Term);
        }

        inFlight.RemoveAll(static f => f.Task.IsCompleted);
        foreach (var proposal in proposals.Where(static p => p.Outcome is null && p.Task.IsCompleted).ToArray())
        {
            switch (proposal.Task)
            {
                case { IsCompletedSuccessfully: true }:
                    proposal.Outcome = "acknowledged";
                    await RecordAcknowledgedAsync(proposal);
                    break;
                case { Exception.InnerException: NotLeaderException { InnerException: null } }:
                    proposal.Outcome = "rejected (NotLeaderException before the append)";
                    break;
                case { IsCanceled: true }:
                    proposal.Outcome = "unknown (canceled)";
                    break;
                default:
                    proposal.Outcome = $"unknown ({proposal.Task.Exception?.InnerException?.GetType().Name})";
                    break;
            }

            Log($"{proposal.Payload} (node-{proposal.Slot.Index}): {proposal.Outcome}");
        }
    }

    private async Task RecordAcknowledgedAsync(Proposal proposal)
    {
        var index = -1L;
        if (ReferenceEquals(proposal.Slot.Node, proposal.Node) && proposal.Slot.Up)
        {
            var prefix = await ReadCommittedAsync(proposal.Slot);
            foreach (var entry in prefix)
            {
                if (entry.Payload == proposal.Payload)
                    index = entry.Index;
            }

            if (index < 0L)
            {
                throw new SafetyViolationException(
                    "acknowledged writes",
                    $"'{proposal.Payload}' was acknowledged by node-{proposal.Slot.Index}, which has committed {prefix.Count} entries and none is it");
            }
        }

        history.RecordAcknowledged(proposal.Payload, proposal.Slot.Index, index);
    }

    private async Task CheckpointAsync()
    {
        var summary = new List<string>();
        foreach (var slot in slots)
        {
            if (!slot.Up)
            {
                summary.Add($"node-{slot.Index}=down");
                continue;
            }

            var prefix = await ReadCommittedAsync(slot);
            history.ObserveCommittedPrefix(slot.Index, prefix);
            summary.Add($"node-{slot.Index}={prefix.Count}");
        }

        Log($"checkpoint: committed entries {string.Join(", ", summary)}; {history.AcknowledgedCount} acknowledged writes checked");
    }

    private async Task<IReadOnlyList<CommittedEntry>> ReadCommittedAsync(Slot slot)
    {
        var wal = slot.Wal;
        var committed = wal.LastCommittedEntryIndex;
        var result = new List<CommittedEntry>();
        if (committed < 1L)
            return result;

        using var reader = await wal.ReadAsync(1L, committed, token);
        var index = 1L;
        foreach (var entry in reader)
            result.Add(new(index++, entry.Term, await entry.ToStringAsync(Encoding.UTF8, token: token)));

        return result;
    }

    /// <summary>
    /// Every fault is removed and every node is up, time advances, and a leader must commit one new proposal
    /// within a bounded number of steps.
    /// </summary>
    private async Task LivenessAsync()
    {
        Log("liveness: heal every link, recover every node, deliver everything, advance time");
        foreach (var first in membership)
        {
            foreach (var second in membership)
            {
                if (!Equals(first, second))
                    network.Heal(first, second, bidirectional: false);
            }
        }

        partitions.Clear();
        foreach (var slot in slots.Where(static s => !s.Up))
            await OpenAsync(slot);

        Proposal target = null;
        var committed = false;
        for (var iteration = 1; iteration <= LivenessIterations; iteration++)
        {
            for (var round = 0; round < 20; round++)
            {
                var pending = SortedPending();
                if (pending.Length is 0)
                    break;

                foreach (var message in pending.OrderBy(_ => random.Next()))
                {
                    Log($"liveness {iteration}: deliver {Describe(message)}");
                    await WaitForDeliveryAsync(network.TryDeliverAsync(message), Describe(message));
                }

                await SettleAsync();
            }

            await ObserveAsync();
            if (target is { Task.IsCompletedSuccessfully: true })
                await ObserveAsync(); // the proposal may have completed after the first observation: record its outcome

            if (target is { Task.IsCompletedSuccessfully: true })
            {
                Log($"liveness: {target.Payload} committed after {iteration} iterations ({elapsed.TotalMilliseconds:0}ms of virtual time)");
                committed = true;
                break;
            }

            if ((target is null || target.Task.IsCompleted) && slots.FirstOrDefault(static s => s.Up && s.BelievesLeader) is { } leader)
            {
                var payload = $"s{seed}-live{iteration}";
                var node = leader.Node;
                Log($"liveness {iteration}: propose {payload} to node-{leader.Index} (term {node.Term})");
                ReadOnlyMemory<byte> bytes = Encoding.UTF8.GetBytes(payload);
                var task = ((IRaftCluster)node).ReplicateAsync(bytes, token: lifetime.Token).AsTask();
                proposals.Add(target = new(payload, leader, node, task));
            }

            Advance(25, $"liveness {iteration}: advance");
            await SettleAsync();
        }

        if (!committed)
            throw new LivenessFailureException($"no new proposal committed in {LivenessIterations} iterations; {Diagnostics()}");

        if (inFlight.Count > 0)
        {
            try
            {
                await Task.WhenAll(inFlight.Select(static f => f.Task)).WaitAsync(Guard, token);
            }
            catch (TimeoutException)
            {
                throw new LivenessFailureException($"an RPC handler never completed: {string.Join("; ", inFlight.Select(static f => f.What))}");
            }
        }

        string Diagnostics()
            => string.Join(", ", slots.Select(static s => $"node-{s.Index}(term {s.Node.Term}, last {s.Node.AuditTrail.LastEntryIndex}, " +
                $"commit {s.Node.AuditTrail.LastCommittedEntryIndex}, leader {s.BelievesLeader})"))
                + $"; {network.PendingMessages.Count} messages pending";
    }

    public async ValueTask DisposeAsync()
    {
        await lifetime.CancelAsync();
        try
        {
            foreach (var slot in slots.Where(static s => s.Up))
            {
                slot.Up = false;
                await slot.Node.DisposeAsync().AsTask().WaitAsync(Guard);
            }
        }
        finally
        {
            foreach (var slot in slots)
            {
                slot.Wal?.Dispose();
                try
                {
                    Directory.Delete(slot.Location, recursive: true);
                }
                catch (Exception e) when (e is IOException or UnauthorizedAccessException)
                {
                    // best effort
                }
            }

            lifetime.Dispose();
        }
    }

    private static string ResolveRevision()
    {
        if (Environment.GetEnvironmentVariable("GITHUB_SHA") is { Length: > 0 } sha)
            return sha;

        if (typeof(Simulation).Assembly.GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion is { } version
            && version.IndexOf('+') is var plus and >= 0)
        {
            return version[(plus + 1)..];
        }

        try
        {
            using var git = Process.Start(new ProcessStartInfo("git", "rev-parse HEAD")
            {
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                WorkingDirectory = AppContext.BaseDirectory,
            });
            if (git is not null && git.WaitForExit(2000) && git.ExitCode is 0)
                return git.StandardOutput.ReadToEnd().Trim();
        }
        catch (Exception e) when (e is System.ComponentModel.Win32Exception or InvalidOperationException)
        {
            // git is not available
        }

        return "unknown";
    }

    private enum Kind
    {
        Deliver,
        DeliverLoseResponse,
        Drop,
        Advance,
        Propose,
        Partition,
        Heal,
        Crash,
        Recover,
    }

    private sealed class Slot(int index, string location, int timeoutMs)
    {
        internal int Index => index;

        internal string Location => location;

        internal int TimeoutMs => timeoutMs;

        internal WriteAheadLog Wal { get; set; }

        internal InProcessCluster Node { get; set; }

        internal bool Up { get; set; }

        internal bool BelievesLeader => Up && Node.Leader is { } leader && leader.Id == Node.Id;
    }

    private sealed class Proposal(string payload, Slot slot, InProcessCluster node, Task task)
    {
        internal string Payload => payload;

        internal Slot Slot => slot;

        internal InProcessCluster Node => node;

        internal Task Task => task;

        internal string Outcome { get; set; }
    }
}

/// <summary>
/// A simulation run failed. The message carries the category, the seed and the complete trace.
/// </summary>
internal sealed class SimulationFailureException : Exception
{
    internal SimulationFailureException(string category, long seed, int voters, IReadOnlyList<string> trace, Exception cause)
        : base($"{category} failure (seed={seed}, voters={voters}): {cause.Message}\n"
               + "A seed alone does not guarantee replay; the trace below is what actually happened.\n"
               + string.Join('\n', trace), cause)
    {
        Category = category;
        Seed = seed;
    }

    internal string Category { get; }

    internal long Seed { get; }
}
