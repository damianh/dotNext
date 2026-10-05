using static DotNext.Net.Cluster.Consensus.Raft.StateMachine.WriteAheadLog;

namespace DotNext.Benchmarks.DurableWrite;

internal enum CellKind
{
    /// <summary>Raw write-ahead log: concurrent writers, each <c>AppendAsync</c>, <c>CommitAsync</c>, wait for apply.</summary>
    WalAppend,

    /// <summary>Raw write-ahead log: one writer appends a batch at an explicit index, the path a follower takes.</summary>
    WalBatch,

    /// <summary>Raft over loopback TCP, closed-loop clients.</summary>
    RaftClosed,

    /// <summary>Raft over loopback TCP, writes offered at a fixed rate.</summary>
    RaftOpen,

    /// <summary>Raft with one follower behind a delaying relay that also pauses mid-run, and frequent snapshots.</summary>
    SlowFollower,

    /// <summary>A short replicated cell with a known failure; the run must report a violation.</summary>
    Inject,
}

/// <summary>
/// One cell of the workload matrix.
/// </summary>
internal sealed record CellSpec(string Name, CellKind Kind)
{
    internal int Voters { get; init; } = 1;

    internal int EntrySize { get; init; } = 128;

    internal int Concurrency { get; init; } = 1;

    internal int BatchSize { get; init; } = 1;

    /// <summary>
    /// For <see cref="CellKind.RaftOpen"/>: the offered rate, as a fraction of the throughput of the busiest closed-loop
    /// cell with the same voters and entry size that ran before it.
    /// </summary>
    internal double OpenLoopFraction { get; init; }

    internal TimeSpan Warmup { get; init; }

    internal TimeSpan Duration { get; init; }

    internal long SnapshotInterval { get; init; } = long.MaxValue;

    internal MemoryManagementStrategy Memory { get; init; }

    // Unbuffered (direct) I/O; the write-ahead log applies it to private memory on Windows and Linux only.
    internal bool NoBuffering { get; init; }

    internal FailureInjection Injection { get; init; }

    // With --repeat: the round, from 1.
    internal int? Repeat { get; init; }

    // The name without the round suffix.
    internal string BaseName { get; init; } = Name;

    internal bool IsReplicated => Kind is not (CellKind.WalAppend or CellKind.WalBatch);
}

/// <summary>
/// The smoke and full workload matrices. Each is bounded: a fixed number of cells, each with a fixed duration,
/// and every cell also stops at <see cref="RunOptions.MaxEntries"/> or <see cref="RunOptions.MaxPayloadBytes"/>.
/// </summary>
internal static class Profiles
{
    private const int Small = 128, Large = 16 * 1024;

    internal static IReadOnlyList<CellSpec> Build(RunOptions options)
    {
        if (options.Injection is not FailureInjection.None)
            return [Inject(options)];

        IReadOnlyList<CellSpec> cells = BuildMatrix(options);
        if (options.Cells is { } filter)
            cells = cells.Where(c => filter.Any(f => MatchesSegments(c.Name, f))).ToList();

        if (options.Repeat <= 1)
            return cells;

        // Round after round, so that a drift of the host spreads over every cell instead of biasing one.
        var rounds = new List<CellSpec>(cells.Count * options.Repeat);
        for (var round = 1; round <= options.Repeat; round++)
        {
            foreach (var cell in cells)
                rounds.Add(cell with { Name = $"{cell.Name}-r{round}", BaseName = cell.Name, Repeat = round });
        }

        return rounds;
    }

    // The filter must cover whole dash-separated segments of the name, so that "c1" does not select "c16".
    internal static bool MatchesSegments(string name, string filter)
    {
        for (var start = name.IndexOf(filter, StringComparison.Ordinal);
             start >= 0;
             start = name.IndexOf(filter, start + 1, StringComparison.Ordinal))
        {
            var end = start + filter.Length;
            if ((start is 0 || name[start - 1] is '-') && (end == name.Length || name[end] is '-'))
                return true;
        }

        return false;
    }

    private static List<CellSpec> BuildMatrix(RunOptions options)
    {
        var full = options.Profile is Profile.Full;
        var sizes = options.EntrySizes ?? [Small, Large];
        var voters = options.Voters ?? (full ? [1, 3, 5] : [1, 3]);
        var concurrency = options.Concurrency ?? (full ? [1, 16, 64] : [8]);
        var warmup = options.Warmup ?? (full ? TimeSpan.FromSeconds(5) : TimeSpan.FromSeconds(1));
        var duration = options.Duration ?? (full ? TimeSpan.FromSeconds(20) : TimeSpan.FromSeconds(4));
        var walWarmup = options.Warmup ?? (full ? TimeSpan.FromSeconds(2) : TimeSpan.FromMilliseconds(500));
        var walDuration = options.Duration ?? (full ? TimeSpan.FromSeconds(10) : TimeSpan.FromSeconds(2));
        var snapshotInterval = full ? 5000L : 1000L;
        var memory = options.MemoryManagement;
        var other = memory is MemoryManagementStrategy.SharedMemory
            ? MemoryManagementStrategy.PrivateMemory
            : MemoryManagementStrategy.SharedMemory;

        var cells = new List<CellSpec>();
        if (options.Mode is RunMode.All or RunMode.Wal)
        {
            foreach (var size in sizes)
            {
                foreach (var c in concurrency)
                {
                    cells.Add(new($"wal-append-{SizeName(size)}-c{c}", CellKind.WalAppend)
                    {
                        EntrySize = size, Concurrency = c, Warmup = walWarmup, Duration = walDuration, Memory = memory,
                    });
                }

                foreach (var batch in full ? [16, 256] : new[] { 64 })
                {
                    cells.Add(new($"wal-batch-{SizeName(size)}-b{batch}", CellKind.WalBatch)
                    {
                        EntrySize = size, BatchSize = batch, Warmup = walWarmup, Duration = walDuration, Memory = memory,
                    });
                }

                if (full)
                {
                    cells.Add(new($"wal-append-{SizeName(size)}-c16-{MemoryName(other)}", CellKind.WalAppend)
                    {
                        EntrySize = size, Concurrency = 16, Warmup = walWarmup, Duration = walDuration, Memory = other,
                    });

                    cells.Add(new($"wal-append-{SizeName(size)}-c16-private-nobuffering", CellKind.WalAppend)
                    {
                        EntrySize = size, Concurrency = 16, Warmup = walWarmup, Duration = walDuration,
                        Memory = MemoryManagementStrategy.PrivateMemory, NoBuffering = true,
                    });
                }
            }
        }

        if (options.Mode is RunMode.All or RunMode.Raft)
        {
            foreach (var v in voters)
            {
                foreach (var size in sizes)
                {
                    // In the smoke profile, a large entry runs on three voters only, with fewer clients.
                    if (!full && size > Small && v != 3)
                        continue;

                    foreach (var c in concurrency)
                    {
                        var clients = !full && size > Small ? int.Max(1, c / 2) : c;
                        cells.Add(new(RaftName(v, size, clients), CellKind.RaftClosed)
                        {
                            Voters = v, EntrySize = size, Concurrency = clients, Warmup = warmup, Duration = duration,
                            SnapshotInterval = snapshotInterval, Memory = memory,
                        });
                    }
                }
            }

            // Open loop: offered as a fraction of the closed-loop throughput of the busiest small-entry cell.
            var openVoters = voters.Contains(3) ? 3 : voters.Max();
            foreach (var fraction in full ? [0.5, 0.9, 1.2] : new[] { 0.5 })
            {
                cells.Add(new($"raft-open-{openVoters}v-{SizeName(sizes.Min())}-{fraction * 100:F0}pct", CellKind.RaftOpen)
                {
                    Voters = openVoters, EntrySize = sizes.Min(), OpenLoopFraction = fraction,
                    Warmup = warmup, Duration = options.Duration ?? (full ? TimeSpan.FromSeconds(20) : TimeSpan.FromSeconds(3)),
                    SnapshotInterval = snapshotInterval, Memory = memory,
                });
            }

            // A slow follower needs a leader and another follower, so it runs on three voters or more. The voters, the
            // smallest entry size and the highest client count follow the options; the snapshot interval and the relay do not.
            var slowVoters = options.Voters is null ? (full ? [3, 5] : [3]) : voters.Where(static v => v >= 3).Distinct().Order().ToArray();
            var slowSize = sizes.Min();
            var slowClients = options.Concurrency?.Max() ?? (full ? 16 : 8);
            foreach (var v in slowVoters)
            {
                cells.Add(new($"slow-follower-{v}v-{SizeName(slowSize)}-c{slowClients}", CellKind.SlowFollower)
                {
                    Voters = v, EntrySize = slowSize, Concurrency = slowClients, Warmup = TimeSpan.Zero,
                    Duration = options.Duration ?? (full ? TimeSpan.FromSeconds(30) : TimeSpan.FromSeconds(10)),
                    SnapshotInterval = full ? 500L : 200L, Memory = memory,
                });
            }
        }

        return cells;
    }

    private static CellSpec Inject(RunOptions options) => new($"inject-{InjectionName(options.Injection)}", CellKind.Inject)
    {
        Voters = 3,
        EntrySize = Small,
        Concurrency = 8,
        Warmup = TimeSpan.Zero,
        Duration = options.Duration ?? TimeSpan.FromSeconds(5),
        SnapshotInterval = 1000L,
        Memory = options.MemoryManagement,
        Injection = options.Injection,
    };

    internal static string RaftName(int voters, int size, int concurrency) => $"raft-closed-{voters}v-{SizeName(size)}-c{concurrency}";

    internal static string SizeName(int size) => size % 1024 is 0 ? $"{size / 1024}KiB" : $"{size}B";

    internal static string MemoryName(MemoryManagementStrategy memory) => memory switch
    {
        MemoryManagementStrategy.PrivateMemory => "private",
        _ => "shared",
    };

    internal static string InjectionName(FailureInjection injection) => injection switch
    {
        FailureInjection.DropApplied => "drop-applied",
        FailureInjection.ReorderApplied => "reorder-applied",
        FailureInjection.NonDurableFollowers => "non-durable-followers",
        _ => "none",
    };
}
