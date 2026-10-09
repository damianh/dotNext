using DotNext.Raft.FaultCampaign;
using DotNext.Raft.FaultCampaign.Driver;
using DotNext.Raft.FaultCampaign.Node;

try
{
    return args switch
    {
        ["node", ..] => await NodeHost.RunAsync(args.AsMemory(1)).ConfigureAwait(false),
        ["run", ..] => await Campaign.RunAsync(args.AsMemory(1)).ConfigureAwait(false),
        _ => Usage(null),
    };
}
catch (UsageException e)
{
    return Usage(e.Message);
}
catch (Exception e) when (args is ["run", ..])
{
    // A failure outside the campaign's own handler (output preparation, report writing) may leave no report.json. A
    // node crash stays unhandled: the driver classifies the runtime's "Unhandled exception" line.
    Console.Error.WriteLine($"harness error: {e}");
    return 1;
}

static int Usage(string? error)
{
    if (error is not null)
        Console.Error.WriteLine($"error: {error}");

    Console.Error.WriteLine(
        """
        Real-process fault-injection smoke campaign for the Raft implementation (#118 stage 3).

        usage: DotNext.Raft.FaultCampaign run [options]
          --transport http|tcp      the Raft transport (default http)
          --out <dir>               the artifact directory: report, history, node logs, data (default ./fault-campaign)
          --seed <n>                the seed that picks victims and hold times (default 1)
          --episodes <list>         a comma-separated subset of the schedule, in schedule order (default: all)
          --inject none|drop-applied|volatile-storage
                                    a test-only failure that the oracles must catch (exit code 3)
          --snapshot-interval <n>   entries between state machine snapshots (default 50)
          --payload <bytes>         the write size (default 256)
          --clients <n>             closed-loop clients (default 4)
          --recovery-timeout <s>    the bound on recovery after each fault (default 30)
          --max-duration <min>      the bound on the whole run (default 10)
          --keep-data true|false    keep the node data directories after a passing run (default false)

        exit codes: 0 pass, 1 harness error, 2 usage, 3 safety violation, 4 liveness failure,
                    5 incomplete (deadline, disk, or a fault that did not take effect), 6 unexpected failure signal

        usage: DotNext.Raft.FaultCampaign node ...  (started by 'run'; see Node/NodeHost.cs)
        """);
    return 2;
}
