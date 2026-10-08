namespace DotNext.Raft.FaultCampaign.Driver;

internal enum FaultKind
{
    // SIGKILL the leader, hold it down, restart it.
    LeaderKill,

    // SIGTERM the leader (graceful stop), hold it down, restart it.
    LeaderTerm,

    // SIGKILL a follower, hold it down, restart it.
    FollowerKill,

    // SIGKILL a follower and keep it down until the leader has a snapshot past the follower's log, then restart it:
    // it must catch up by installing the snapshot.
    LaggingSnapshot,

    // SIGKILL every node at once, then restart all of them: everything must be recovered from disk.
    ClusterKill,
}

internal readonly record struct Episode(int Number, FaultKind Kind, TimeSpan Hold)
{
    internal string Name => Schedule.NameOf(Kind);
}

/// <summary>
/// The fixed fault schedule of the smoke campaign. The seed picks only the victims among the followers and the
/// hold times; the order of faults is fixed, so every run covers every fault.
/// </summary>
internal static class Schedule
{
    internal static readonly FaultKind[] Default =
    [
        FaultKind.LeaderKill,
        FaultKind.LeaderKill,
        FaultKind.LeaderTerm,
        FaultKind.FollowerKill,
        FaultKind.LeaderKill,
        FaultKind.LaggingSnapshot,
        FaultKind.ClusterKill,
    ];

    internal static string NameOf(FaultKind kind) => kind switch
    {
        FaultKind.LeaderKill => "leader-kill",
        FaultKind.LeaderTerm => "leader-term",
        FaultKind.FollowerKill => "follower-kill",
        FaultKind.LaggingSnapshot => "lagging-snapshot",
        FaultKind.ClusterKill => "cluster-kill",
        _ => throw new ArgumentOutOfRangeException(nameof(kind)),
    };

    internal static FaultKind Parse(string name) => name switch
    {
        "leader-kill" => FaultKind.LeaderKill,
        "leader-term" => FaultKind.LeaderTerm,
        "follower-kill" => FaultKind.FollowerKill,
        "lagging-snapshot" => FaultKind.LaggingSnapshot,
        "cluster-kill" => FaultKind.ClusterKill,
        _ => throw new UsageException(
            $"unknown episode '{name}'; expected leader-kill, leader-term, follower-kill, lagging-snapshot or cluster-kill"),
    };

    /// <param name="kinds">The faults, in order; <see langword="null"/> for <see cref="Default"/>.</param>
    internal static Episode[] Create(int seed, IReadOnlyList<FaultKind>? kinds)
    {
        var random = new Random(seed);
        var result = new List<Episode>();
        foreach (var kind in kinds ?? Default)
        {
            // Long enough for the remaining nodes to elect a leader (election timeout 1-2 s) before the victim returns,
            // short enough to keep the smoke run in minutes.
            var hold = TimeSpan.FromMilliseconds(random.Next(500, 4000));
            result.Add(new(result.Count + 1, kind, hold));
        }

        return result.ToArray();
    }

    /// <summary>
    /// A follower picked by the seed.
    /// </summary>
    internal static int PickFollower(Random random, int nodes, int leader)
    {
        var pick = random.Next(nodes - 1);
        return pick >= leader ? pick + 1 : pick;
    }
}
