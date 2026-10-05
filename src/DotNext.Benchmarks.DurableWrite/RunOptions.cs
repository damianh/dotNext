using System.Globalization;
using DotNext.Net.Cluster.Consensus.Raft.StateMachine;

namespace DotNext.Benchmarks.DurableWrite;

internal enum RunMode
{
    All,
    Wal,
    Raft,
}

internal enum Profile
{
    Smoke,
    Full,
}

/// <summary>
/// Command-line options. Every run is bounded by the profile, <see cref="MaxEntries"/> and <see cref="MaxDuration"/>.
/// </summary>
internal sealed class RunOptions
{
    internal RunMode Mode { get; private set; } = RunMode.All;

    internal Profile Profile { get; private set; } = Profile.Smoke;

    internal FailureInjection Injection { get; private set; }

    internal string? OutputPath { get; private set; }

    internal string WorkDirectory { get; private set; } = Path.Combine(Path.GetTempPath(), "dotnext-durable-write");

    internal bool KeepData { get; private set; }

    // The global caps: the number of acknowledged writes per cell, and the wall time of the whole run.
    internal long MaxEntries { get; private set; } = 200_000L;

    internal TimeSpan MaxDuration { get; private set; } = TimeSpan.FromMinutes(30);

    internal long MaxPayloadBytes { get; private set; } = 2L * 1024 * 1024 * 1024;

    internal WriteAheadLog.MemoryManagementStrategy MemoryManagement { get; private set; } = WriteAheadLog.MemoryManagementStrategy.SharedMemory;

    // Optional overrides of the profile, for a custom run.
    internal int[]? Voters { get; private set; }

    internal int[]? EntrySizes { get; private set; }

    internal int[]? Concurrency { get; private set; }

    internal TimeSpan? Duration { get; private set; }

    internal TimeSpan? Warmup { get; private set; }

    internal const string Usage =
        """
        Durable-write load baselines for the .NEXT Raft write-ahead log (#118 stage 2).

        Usage: DotNext.Benchmarks.DurableWrite [options]

          --mode all|wal|raft           What to run (default: all)
          --profile smoke|full          Workload matrix and durations (default: smoke)
          --inject none|drop-applied|reorder-applied|non-durable-followers
                                        Run one short replicated cell with a known failure, to show that the
                                        oracles catch it. The run must exit with code 3.
          --out <file>                  Write the JSON report to this file
          --work-dir <dir>              Where node data is written (default: %TEMP%/dotnext-durable-write)
          --keep-data                   Do not delete node data after each cell
          --memory shared|private       WAL memory management strategy (default: shared)
          --voters 1,3,5                Override the replicated cluster sizes
          --sizes 128,16384             Override the entry sizes, in bytes
          --concurrency 1,16,64         Override the closed-loop client counts
          --duration <seconds>          Override the measured duration of each cell
          --warmup <seconds>            Override the warmup of each cell
          --max-entries <n>             Stop a cell after n acknowledged writes (default: 200000)
          --max-duration <minutes>      Abort the whole run after this time (default: 30)
          --max-payload-gib <n>         Stop a cell after n GiB of acknowledged payload (default: 2)

        Exit codes: 0 ok, 1 unexpected error, 2 usage, 3 safety oracle violation, 4 liveness failure.
        """;

    internal static RunOptions Parse(string[] args)
    {
        var result = new RunOptions();
        for (var i = 0; i < args.Length; i++)
        {
            var name = args[i];
            switch (name)
            {
                case "--mode":
                    result.Mode = Next() switch
                    {
                        "all" => RunMode.All,
                        "wal" => RunMode.Wal,
                        "raft" => RunMode.Raft,
                        var other => throw new UsageException($"Unknown mode '{other}'"),
                    };
                    break;
                case "--profile":
                    result.Profile = Next() switch
                    {
                        "smoke" => Profile.Smoke,
                        "full" => Profile.Full,
                        var other => throw new UsageException($"Unknown profile '{other}'"),
                    };
                    break;
                case "--inject":
                    result.Injection = Next() switch
                    {
                        "none" => FailureInjection.None,
                        "drop-applied" => FailureInjection.DropApplied,
                        "reorder-applied" => FailureInjection.ReorderApplied,
                        "non-durable-followers" => FailureInjection.NonDurableFollowers,
                        var other => throw new UsageException($"Unknown injection '{other}'"),
                    };
                    break;
                case "--out":
                    result.OutputPath = Next();
                    break;
                case "--work-dir":
                    result.WorkDirectory = Next();
                    break;
                case "--keep-data":
                    result.KeepData = true;
                    break;
                case "--memory":
                    result.MemoryManagement = Next() switch
                    {
                        "shared" => WriteAheadLog.MemoryManagementStrategy.SharedMemory,
                        "private" => WriteAheadLog.MemoryManagementStrategy.PrivateMemory,
                        var other => throw new UsageException($"Unknown memory strategy '{other}'"),
                    };
                    break;
                case "--voters":
                    result.Voters = IntList(1, 5);
                    break;
                case "--sizes":
                    result.EntrySizes = IntList(WriteKeyHeader, 1024 * 1024);
                    break;
                case "--concurrency":
                    result.Concurrency = IntList(1, 1024);
                    break;
                case "--duration":
                    result.Duration = TimeSpan.FromSeconds(Number(1, 3600));
                    break;
                case "--warmup":
                    result.Warmup = TimeSpan.FromSeconds(Number(0, 600));
                    break;
                case "--max-entries":
                    result.MaxEntries = Number(1, 10_000_000);
                    break;
                case "--max-duration":
                    result.MaxDuration = TimeSpan.FromMinutes(Number(1, 24 * 60));
                    break;
                case "--max-payload-gib":
                    result.MaxPayloadBytes = Number(1, 64) * 1024L * 1024 * 1024;
                    break;
                case "-h" or "--help" or "-?":
                    throw new UsageException(null);
                default:
                    throw new UsageException($"Unknown option '{name}'");
            }

            string Next()
                => ++i < args.Length ? args[i] : throw new UsageException($"Option '{name}' needs a value");

            long Number(long min, long max)
                => long.TryParse(Next(), NumberStyles.None, CultureInfo.InvariantCulture, out var value) && value >= min && value <= max
                    ? value
                    : throw new UsageException($"Option '{name}' needs a number in [{min}, {max}]");

            int[] IntList(int min, int max)
            {
                var values = Next().Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
                var result = new int[values.Length];
                for (var j = 0; j < values.Length; j++)
                {
                    if (!int.TryParse(values[j], NumberStyles.None, CultureInfo.InvariantCulture, out result[j]) || result[j] < min || result[j] > max)
                        throw new UsageException($"Option '{name}' needs numbers in [{min}, {max}]");
                }

                return result.Length > 0 ? result : throw new UsageException($"Option '{name}' needs at least one number");
            }
        }

        return result;
    }

    private const int WriteKeyHeader = Oracles.WriteKey.HeaderSize;
}

internal sealed class UsageException(string? message) : Exception(message ?? string.Empty);
