namespace DotNext.Net.Cluster.Consensus.Raft.InProcess;

/// <summary>
/// #56 stage 1: seeded, bounded schedules of faults and client proposals against WAL-backed in-process nodes, checked by
/// independent safety oracles, plus a separate liveness check after every fault is removed.
/// See the in-process README for what is and is not controlled. A seed alone is not a guarantee of replay.
/// </summary>
public sealed class SimulationTests : RaftTest
{
    private const string SeedsVariable = "DOTNEXT_RAFT_SIM_SEEDS";
    private const string SeedVariable = "DOTNEXT_RAFT_SIM_SEED";
    private const string VotersVariable = "DOTNEXT_RAFT_SIM_VOTERS";
    private const string StepsVariable = "DOTNEXT_RAFT_SIM_STEPS";

    public static TheoryData<int, long> FixedSeeds()
    {
        var data = new TheoryData<int, long>();
        foreach (var voters in new[] { 3, 5 })
        {
            for (var seed = 1L; seed <= 8L; seed++)
                data.Add(voters, seed);
        }

        return data;
    }

    [Theory(Timeout = TestTimeouts.Default)]
    [MemberData(nameof(FixedSeeds))]
    public static async Task FixedSeedKeepsSafetyAndRecoversLiveness(int voters, long seed)
    {
        await using var simulation = new Simulation(seed, voters);
        await RunAsync(simulation);
    }

    /// <summary>
    /// Manual campaign. Set <c>DOTNEXT_RAFT_SIM_SEEDS=N</c> to run N random seeds for each cluster size, or
    /// <c>DOTNEXT_RAFT_SIM_SEED=S</c> to run one seed again. <c>DOTNEXT_RAFT_SIM_VOTERS</c> (3 or 5) limits the size and
    /// <c>DOTNEXT_RAFT_SIM_STEPS</c> changes the length of the fault phase. The first failure stops the run and reports the seed and trace.
    /// </summary>
    [Fact(Timeout = 4 * 60 * 60 * 1000)]
    public static async Task Campaign()
    {
        var seeds = Environment.GetEnvironmentVariable(SeedsVariable);
        var single = Environment.GetEnvironmentVariable(SeedVariable);
        SkipUnless(!string.IsNullOrEmpty(seeds) || !string.IsNullOrEmpty(single),
            $"Set {SeedsVariable}=<count> or {SeedVariable}=<seed> to run a simulation campaign.");

        var steps = int.TryParse(Environment.GetEnvironmentVariable(StepsVariable), out var parsedSteps) ? parsedSteps : 150;
        var sizes = int.TryParse(Environment.GetEnvironmentVariable(VotersVariable), out var parsedVoters)
            ? new[] { parsedVoters }
            : new[] { 3, 5 };
        var runs = new List<(int Voters, long Seed)>();
        if (!string.IsNullOrEmpty(single))
        {
            runs.AddRange(sizes.Select(size => (size, long.Parse(single))));
        }
        else
        {
            for (var i = 0; i < int.Parse(seeds); i++)
            {
                var seed = Random.Shared.NextInt64(1L, int.MaxValue);
                runs.AddRange(sizes.Select(size => (size, seed)));
            }
        }

        var output = TestContext.Current.TestOutputHelper;
        foreach (var (voters, seed) in runs)
        {
            output?.WriteLine($"running seed={seed} voters={voters} steps={steps}");
            await using var simulation = new Simulation(seed, voters, steps);
            await RunAsync(simulation);
        }
    }

    private static async Task RunAsync(Simulation simulation)
    {
        try
        {
            await simulation.RunAsync(TestToken);
        }
        finally
        {
            TestContext.Current.TestOutputHelper?.WriteLine(simulation.Summary);
        }
    }

    // The oracles are checked by feeding them small histories that a correct cluster cannot produce.

    private static CommittedEntry Entry(long index, long term, string payload) => new(index, term, payload);

    private static CommittedEntry[] Prefix(params (long Term, string Payload)[] entries)
        => entries.Select(static (e, i) => Entry(i + 1L, e.Term, e.Payload)).ToArray();

    [Fact]
    public static void ElectionSafetyRejectsTwoLeadersInOneTerm()
    {
        var history = new SimulationHistory();
        history.RecordLeaderClaim(node: 0, term: 1L);
        history.RecordLeaderClaim(node: 1, term: 2L);
        history.RecordLeaderClaim(node: 0, term: 1L); // the same node again is not a second leader

        var failure = Throws<SafetyViolationException>(() => history.RecordLeaderClaim(node: 2, term: 2L));
        Equal("election safety", failure.Oracle);
    }

    [Fact]
    public static void CommittedPrefixAgreementRejectsDifferentTermAtTheSameIndex()
    {
        var history = new SimulationHistory();
        history.ObserveCommittedPrefix(0, Prefix((1L, string.Empty), (1L, "a")));

        var failure = Throws<SafetyViolationException>(
            () => history.ObserveCommittedPrefix(1, Prefix((1L, string.Empty), (2L, "a"))));
        Equal("committed-prefix agreement", failure.Oracle);
    }

    [Fact]
    public static void CommittedPrefixAgreementRejectsDifferentPayloadAtTheSameIndex()
    {
        var history = new SimulationHistory();
        history.ObserveCommittedPrefix(0, Prefix((1L, string.Empty), (1L, "a")));

        var failure = Throws<SafetyViolationException>(
            () => history.ObserveCommittedPrefix(1, Prefix((1L, string.Empty), (1L, "b"))));
        Equal("committed-prefix agreement", failure.Oracle);
    }

    [Fact]
    public static void CommittedPrefixAgreementRejectsAnEntryThatChangedOnTheSameNode()
    {
        var history = new SimulationHistory();
        history.ObserveCommittedPrefix(0, Prefix((1L, string.Empty), (1L, "a")));

        var failure = Throws<SafetyViolationException>(
            () => history.ObserveCommittedPrefix(0, Prefix((1L, string.Empty), (2L, "b"))));
        Equal("committed-prefix agreement", failure.Oracle);
    }

    [Fact]
    public static void AcknowledgedWriteMustKeepItsPayloadAtItsIndex()
    {
        var history = new SimulationHistory();
        history.RecordAcknowledged("a", node: 0, index: 2L);

        // the entry at index 2 is committed on another node, but it is not the acknowledged write
        var failure = Throws<SafetyViolationException>(
            () => history.ObserveCommittedPrefix(1, Prefix((1L, string.Empty), (2L, "other"))));
        Equal("acknowledged writes", failure.Oracle);
    }

    [Fact]
    public static void AcknowledgedWriteWithoutKnownIndexIsFoundByPayload()
    {
        var history = new SimulationHistory();
        history.RecordAcknowledged("a", node: 0, index: -1L);

        // the write is only ever seen at index 2 on another node, and it is there
        history.ObserveCommittedPrefix(1, Prefix((1L, string.Empty), (2L, "a")));
        history.RequireAcknowledgedWritesPreserved();
    }
    [Fact]
    public static void AcknowledgedWriteMissingFromEveryCommittedLogIsLost()
    {
        var history = new SimulationHistory();
        history.RecordAcknowledged("a", node: 0, index: 2L);
        history.ObserveCommittedPrefix(1, Prefix((1L, string.Empty)));
        history.ObserveCommittedPrefix(2, Prefix((1L, string.Empty)));

        var failure = Throws<SafetyViolationException>(history.RequireAcknowledgedWritesPreserved);
        Equal("acknowledged writes", failure.Oracle);
    }

    [Fact]
    public static void AcknowledgedWriteWithoutKnownIndexThatNeverCommittedIsLost()
    {
        var history = new SimulationHistory();
        history.RecordAcknowledged("a", node: 0, index: -1L);
        history.ObserveCommittedPrefix(1, Prefix((1L, string.Empty), (1L, "b")));

        var failure = Throws<SafetyViolationException>(history.RequireAcknowledgedWritesPreserved);
        Equal("acknowledged writes", failure.Oracle);
    }

    [Fact]
    public static void ConsistentHistoryPasses()
    {
        var history = new SimulationHistory();
        history.RecordLeaderClaim(0, 1L);
        history.RecordLeaderClaim(1, 2L);
        history.RecordAcknowledged("a", node: 0, index: 2L);
        history.RecordAcknowledged("b", node: 1, index: -1L);
        history.ObserveCommittedPrefix(0, Prefix((1L, string.Empty), (1L, "a")));
        history.ObserveCommittedPrefix(1, Prefix((1L, string.Empty), (1L, "a"), (2L, "b")));
        history.ObserveCommittedPrefix(2, Prefix((1L, string.Empty)));
        history.RequireAcknowledgedWritesPreserved();
    }
}
