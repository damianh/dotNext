using System.Diagnostics;
using System.Net;
using DotNext.Net;
using DotNext.Net.Cluster;
using DotNext.Net.Cluster.Consensus.Raft;
using DotNext.Net.Cluster.Consensus.Raft.Http;
using DotNext.Net.Cluster.Consensus.Raft.Membership;
using DotNext.Net.Cluster.Consensus.Raft.StateMachine;
using Microsoft.AspNetCore.Connections;
using Microsoft.Extensions.Logging.Console;

namespace DotNext.Raft.FaultCampaign.Node;

/// <summary>
/// The process under test: one Raft node over HTTP or TCP, hosted like <c>src/examples/RaftNode</c>, with the
/// write-ahead log at its library defaults and the durable-write tool's <see cref="HistoryStateMachine"/>.
/// </summary>
/// <remarks>
/// The election timeout and the HTTP request timeout are those of the durable-write tool (#118 stage 2), not RaftNode's 150-300 ms:
/// every append is persisted before it completes, so a 150 ms election timeout would be shorter than the
/// persist latency of a slow runner's disk and elections would churn without any injected fault.
/// The node listens on a private port and advertises the port of its proxy in the driver as its public endpoint (the
/// <c>publicEndPoint</c> setting over HTTP, <c>PublicEndPoint</c> over TCP), so every peer reaches it through the proxy.
/// </remarks>
internal static class NodeHost
{
    internal const int LowerElectionTimeout = 1000, UpperElectionTimeout = 2000;

    // Over TCP this one timeout also bounds vote and pre-vote requests, and a vote round waits for every member (#146),
    // so it stays at the library default, the lower election timeout. Otherwise a silent (partitioned) member would
    // stall every election of the majority. HTTP votes use its separate RpcTimeout (default: upper election timeout / 2).
    internal static TimeSpan RequestTimeout(Transport transport) => transport switch
    {
        Transport.Http => TimeSpan.FromSeconds(3),
        _ => TimeSpan.FromMilliseconds(LowerElectionTimeout),
    };

    // A write that has not completed by then is reported as unknown. The driver's own timeout is longer, so it always
    // gets an outcome from a live node, and a closed-loop client never has two writes in flight.
    internal static readonly TimeSpan ReplicateTimeout = TimeSpan.FromSeconds(10);

    internal static async Task<int> RunAsync(ReadOnlyMemory<string> args)
    {
        var options = NodeOptions.Parse(args.Span);
        if (options.Injection is NodeInjection.VolatileStorage && Directory.Exists(options.DataDirectory))
            Directory.Delete(options.DataDirectory, recursive: true);

        var smDirectory = new DirectoryInfo(Path.Combine(options.DataDirectory, "sm"));
        smDirectory.Create();
        var injection = options.Injection is NodeInjection.DropApplied ? FailureInjection.DropApplied : FailureInjection.None;
        var stateMachine = new HistoryStateMachine(smDirectory, options.Id, options.SnapshotInterval, injection: injection);
        await using (stateMachine.ConfigureAwait(false))
        {
            // The write-ahead log reads the restored snapshot in its constructor, so restore first.
            await stateMachine.RestoreAsync().ConfigureAwait(false);
            var walOptions = new WriteAheadLog.Options { Location = Path.Combine(options.DataDirectory, "wal") };
            var state = new NodeState(options, stateMachine, stateMachine.Restores);

            return options.Transport switch
            {
                Transport.Http => await RunHttpAsync(options, state, walOptions).ConfigureAwait(false),
                _ => await RunTcpAsync(options, state, walOptions).ConfigureAwait(false),
            };
        }
    }

    private static async Task<int> RunHttpAsync(NodeOptions options, NodeState state, WriteAheadLog.Options walOptions)
    {
        var configuration = new Dictionary<string, string?>
        {
            { "partitioning", "false" },
            { "lowerElectionTimeout", LowerElectionTimeout.ToString() },
            { "upperElectionTimeout", UpperElectionTimeout.ToString() },
            { "requestTimeout", RequestTimeout(Transport.Http).ToString() },
            { "publicEndPoint", HttpEndPoint(options.RaftPorts[options.Id]).ToString() },
            { "coldStart", "false" },
        };

        var builder = WebApplication.CreateSlimBuilder();
        builder.Configuration.AddInMemoryCollection(configuration);
        builder.WebHost.ConfigureKestrel(kestrel =>
        {
            kestrel.Listen(IPAddress.Loopback, options.ListenPort);
            kestrel.Listen(IPAddress.Loopback, options.ControlPort);
        });

        builder.Services
            .UseInMemoryConfigurationStorage(members =>
            {
                foreach (var port in options.RaftPorts)
                    members.Add(new UriEndPoint(HttpEndPoint(port)));
            })
            .AddSingleton<IStateMachine>(state.StateMachine)
            .UsePersistentLog(walOptions)
            .AddRouting();

        ConfigureLogging(builder.Logging);
        builder.JoinCluster();

        var app = builder.Build();
        await using var appScope = app.ConfigureAwait(false);
        var cluster = app.Services.GetRequiredService<IRaftCluster>();
        state.Attach(cluster);

        // This test host does not authenticate peers; it listens on loopback only.
        app.UseConsensusProtocolHandler();
        state.MapControlEndpoints(app);
        await app.RunAsync().ConfigureAwait(false);
        return 0;
    }

    private static async Task<int> RunTcpAsync(NodeOptions options, NodeState state, WriteAheadLog.Options walOptions)
    {
        var builder = WebApplication.CreateSlimBuilder();
        builder.WebHost.ConfigureKestrel(kestrel => kestrel.Listen(IPAddress.Loopback, options.ControlPort));
        builder.Services.AddRouting();
        ConfigureLogging(builder.Logging);
        var app = builder.Build();
        await using var appScope = app.ConfigureAwait(false);

        var configuration = new RaftCluster.TcpConfiguration(new IPEndPoint(IPAddress.Loopback, options.ListenPort))
        {
            PublicEndPoint = new IPEndPoint(IPAddress.Loopback, options.RaftPorts[options.Id]),
            ColdStart = false,
            LowerElectionTimeout = LowerElectionTimeout,
            UpperElectionTimeout = UpperElectionTimeout,
            RequestTimeout = RequestTimeout(Transport.Tcp),
            ConfigurationStorage = null, // in-memory static configuration, as in RaftNode
            LoggerFactory = app.Services.GetRequiredService<ILoggerFactory>(),
        };

        var members = ((InMemoryClusterConfigurationStorage<EndPoint>)configuration.ConfigurationStorage!).CreateInitialConfigurationBuilder();
        foreach (var port in options.RaftPorts)
            members.Add(new IPEndPoint(IPAddress.Loopback, port));

        members.Build();

        var wal = new WriteAheadLog(walOptions, state.StateMachine);
        await using (wal.ConfigureAwait(false))
        {
            var cluster = new RaftCluster(configuration) { AuditTrail = wal };
            await using (cluster.ConfigureAwait(false))
            {
                state.Attach(cluster);
                await cluster.StartAsync(CancellationToken.None).ConfigureAwait(false);
                state.MapControlEndpoints(app);

                // Returns on SIGTERM (or Ctrl+C), like the generic host in HTTP mode.
                await app.RunAsync().ConfigureAwait(false);
                await cluster.StopAsync(CancellationToken.None).ConfigureAwait(false);
            }
        }

        return 0;
    }

    private static Uri HttpEndPoint(int port) => new($"http://127.0.0.1:{port}/", UriKind.Absolute);

    // One JSON object per line, so the driver can classify the failure signals by event id (RAFT-REVIEW, #26).
    private static void ConfigureLogging(ILoggingBuilder logging)
    {
        logging.ClearProviders();
        logging.AddJsonConsole(static json =>
        {
            json.UseUtcTimestamp = true;
            json.TimestampFormat = "yyyy-MM-ddTHH:mm:ss.fffZ";
            json.IncludeScopes = false;
        });
        logging.SetMinimumLevel(LogLevel.Information);
        logging.AddFilter("Microsoft", LogLevel.Warning);
        logging.AddFilter("System.Net.Http", LogLevel.Warning);
        logging.AddFilter<ConsoleLoggerProvider>("Microsoft.Hosting.Lifetime", LogLevel.Information);
    }
}

/// <summary>
/// The control endpoints and the leader-claim journal of one node.
/// </summary>
internal sealed class NodeState(NodeOptions options, HistoryStateMachine stateMachine, int restoresAtStartup)
{
    private readonly Guid incarnation = Guid.NewGuid();
    private readonly Lock claimsSync = new();
    private IRaftCluster? cluster;

    internal HistoryStateMachine StateMachine => stateMachine;

    private IRaftCluster Cluster => cluster ?? throw new InvalidOperationException("the cluster is not attached");

    internal void Attach(IRaftCluster cluster)
    {
        this.cluster = cluster;
        cluster.LeaderChanged += OnLeaderChanged;
    }

    // A leader claim is appended to a file outside the data directory and flushed to the page cache, which survives
    // SIGKILL. The driver feeds the claims of every incarnation to the election-safety oracle.
    private void OnLeaderChanged(ICluster sender, IClusterMember? leader)
    {
        if (leader is not { IsRemote: false })
            return;

        var term = Cluster.AuditTrail.Term;
        lock (claimsSync)
            File.AppendAllText(options.ClaimsFile, $"{term}\n");
    }

    internal void MapControlEndpoints(WebApplication app)
    {
        app.MapPost(ControlApi.Write, WriteAsync);
        app.MapGet(ControlApi.Status, GetStatus);
        app.MapGet(ControlApi.History, GetHistory);
    }

    private async Task WriteAsync(HttpContext context)
    {
        var payload = new byte[(int)(context.Request.ContentLength ?? 0L)];
        await context.Request.Body.ReadExactlyAsync(payload, context.RequestAborted).ConfigureAwait(false);

        var cluster = Cluster;
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(context.RequestAborted);
        timeout.CancelAfter(NodeHost.ReplicateTimeout);
        int status;
        try
        {
            var entry = new BinaryLogEntry { Term = cluster.AuditTrail.Term, Content = payload };
            await cluster.ReplicateAsync(entry, timeout.Token).ConfigureAwait(false);
            status = ControlApi.Acknowledged;
        }
        catch (NotLeaderException e) when (e.InnerException is null)
        {
            status = ControlApi.Rejected;
        }
        catch (NotLeaderException)
        {
            status = ControlApi.Unknown;
        }
        catch (OperationCanceledException)
        {
            status = ControlApi.Unknown;
        }
        catch (Exception e)
        {
            // Once SIGTERM stops the host, the cluster and the log are stopped and disposed under the write, for example
            // with ObjectDisposedException: the outcome is unknown and the Warning is reported as unclassified. From a live
            // node the exception is not expected, and the driver fails the run on a Critical line (exit 6).
            var stopping = context.RequestServices.GetRequiredService<IHostApplicationLifetime>().ApplicationStopping.IsCancellationRequested;
            context.RequestServices.GetRequiredService<ILoggerFactory>().CreateLogger("FaultCampaign.Node")
                .Log(stopping ? LogLevel.Warning : LogLevel.Critical, e, "The write failed with an unexpected exception");
            status = ControlApi.Unknown;
        }

        context.Response.StatusCode = status;
    }

    private IResult GetStatus()
    {
        var cluster = Cluster;
        var wal = (WriteAheadLog)cluster.AuditTrail;
        bool isLeader;
        try
        {
            isLeader = !cluster.LeadershipToken.IsCancellationRequested;
        }
        catch (ObjectDisposedException)
        {
            isLeader = false;
        }

        return Results.Json(new NodeStatus
        {
            Id = options.Id,
            Pid = Environment.ProcessId,
            Incarnation = incarnation,
            Term = cluster.Term,
            IsLeader = isLeader,
            Leader = cluster.Leader?.EndPoint.ToString(),
            LastEntryIndex = wal.LastEntryIndex,
            CommitIndex = wal.LastCommittedEntryIndex,
            AppliedIndex = wal.LastAppliedIndex,
            SnapshotIndex = ((ISnapshotManager)stateMachine).Snapshot?.Index ?? 0L,
            Restores = stateMachine.Restores,
            RestoresAtStartup = restoresAtStartup,
            Snapshots = stateMachine.Snapshots,
            SnapshotFailures = stateMachine.SnapshotFailures,
        }, ControlApi.Json);
    }

    // ?from=n&incarnation=g&epoch=e: the entries from position n if the incarnation and epoch still match, otherwise
    // the whole history. The history and its epoch are read atomically, so a page never mixes two epochs.
    private IResult GetHistory(int? from, Guid? incarnation, int? epoch)
    {
        var history = stateMachine.GetHistory(out var current);
        var start = incarnation == this.incarnation && epoch == current ? int.Clamp(from ?? 0, 0, history.Count) : 0;
        var entries = new long[history.Count - start][];
        for (var i = 0; i < entries.Length; i++)
            entries[i] = ControlApi.Encode(history[start + i]);

        return Results.Json(new HistoryPage
        {
            Incarnation = this.incarnation,
            Epoch = current,
            From = start,
            Total = history.Count,
            Entries = entries,
        }, ControlApi.Json);
    }
}

internal sealed class NodeOptions
{
    internal required Transport Transport { get; init; }
    internal required int Id { get; init; }
    // The member endpoints, the same on every node. The driver's proxy of each node listens on its port.
    internal required int[] RaftPorts { get; init; }

    // The port this node listens on for Raft: the upstream of its proxy.
    internal required int ListenPort { get; init; }
    internal required int ControlPort { get; init; }
    internal required string DataDirectory { get; init; }
    internal required string ClaimsFile { get; init; }
    internal required long SnapshotInterval { get; init; }
    internal required NodeInjection Injection { get; init; }

    internal static NodeOptions Parse(ReadOnlySpan<string> args)
    {
        var line = new CommandLine(args);
        var ports = line.Require("peers").Split(',').Select(static p => int.Parse(p, System.Globalization.CultureInfo.InvariantCulture)).ToArray();
        var result = new NodeOptions
        {
            Transport = line.GetChoice("transport", Transport.Http, ("http", Transport.Http), ("tcp", Transport.Tcp)),
            RaftPorts = ports,
            Id = line.GetInt32("id", -1, 0, ports.Length - 1),
            ListenPort = line.GetInt32("listen-port", 0, 1, ushort.MaxValue),
            ControlPort = line.GetInt32("control-port", 0, 1, ushort.MaxValue),
            DataDirectory = Path.GetFullPath(line.Require("data")),
            ClaimsFile = Path.GetFullPath(line.Require("claims")),
            SnapshotInterval = line.GetInt32("snapshot-interval", 50, 1),
            Injection = line.GetChoice("inject", NodeInjection.None,
                ("none", NodeInjection.None), ("drop-applied", NodeInjection.DropApplied), ("volatile-storage", NodeInjection.VolatileStorage)),
        };

        line.RequireAllRead();
        if (result.Id < 0 || result.ControlPort is 0 || result.ListenPort is 0)
            throw new UsageException("options --id, --listen-port and --control-port are required");

        Debug.Assert(result.RaftPorts.Length > 0);
        return result;
    }
}
