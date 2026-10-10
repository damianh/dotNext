using DotNext.Benchmarks.DurableWrite.Oracles;

namespace DotNext.Raft.FaultCampaign.Driver;

internal sealed class CampaignOptions
{
    internal const int Nodes = 3;

    // The cluster must commit this many new writes after a fault is removed before it counts as recovered.
    internal const int MinAcknowledgedAfterRecovery = 20;

    internal required Transport Transport { get; init; }
    internal required string OutputDirectory { get; init; }
    internal required int Seed { get; init; }
    internal required IReadOnlyList<FaultKind>? Episodes { get; init; }
    internal required NodeInjection Injection { get; init; }
    internal required int SnapshotInterval { get; init; }
    internal required int PayloadSize { get; init; }
    internal required int Clients { get; init; }
    internal required TimeSpan RecoveryTimeout { get; init; }
    internal required TimeSpan MaxDuration { get; init; }
    internal required bool KeepData { get; init; }
    internal int Cycles { get; init; } = 1;
    internal int MaxWrites { get; init; } = 200_000;

    // Past index 100, where the drop-applied injection starts, and past a few snapshots.
    internal long WarmupEntries => long.Max(150L, 3L * SnapshotInterval);

    internal static CampaignOptions Parse(ReadOnlySpan<string> args)
    {
        var line = new CommandLine(args);
        var episodes = line.Get("episodes") is { } list
            ? list.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries).Select(Schedule.Parse).ToArray()
            : null;

        var result = new CampaignOptions
        {
            Transport = line.GetChoice("transport", Transport.Http, ("http", Transport.Http), ("tcp", Transport.Tcp)),
            OutputDirectory = Path.GetFullPath(line.Get("out") ?? "fault-campaign"),
            Seed = line.GetInt32("seed", 1),
            Episodes = episodes,
            Injection = line.GetChoice("inject", NodeInjection.None,
                ("none", NodeInjection.None), ("drop-applied", NodeInjection.DropApplied), ("volatile-storage", NodeInjection.VolatileStorage),
                ("partition-leak", NodeInjection.PartitionLeak)),
            SnapshotInterval = line.GetInt32("snapshot-interval", 50, 10, 10_000),
            PayloadSize = line.GetInt32("payload", 256, WriteKey.HeaderSize, 64 * 1024),
            Clients = line.GetInt32("clients", 4, 1, 64),
            RecoveryTimeout = TimeSpan.FromSeconds(line.GetInt32("recovery-timeout", 30, 5, 600)),
            MaxDuration = TimeSpan.FromMinutes(line.GetDouble("max-duration", 10D, 0.5D, 240D)),
            KeepData = line.GetChoice("keep-data", false, ("true", true), ("false", false)),
            Cycles = line.GetInt32("cycles", 1, 1, 1000),
            MaxWrites = line.GetInt32("max-writes", 200_000, 1, 1_000_000),
        };

        line.RequireAllRead();
        if (episodes is { Length: 0 })
            throw new UsageException("option --episodes needs at least one episode");

        return result;
    }

    internal static string InjectionName(NodeInjection injection) => injection switch
    {
        NodeInjection.DropApplied => "drop-applied",
        NodeInjection.VolatileStorage => "volatile-storage",
        NodeInjection.PartitionLeak => "partition-leak",
        _ => "none",
    };
}
