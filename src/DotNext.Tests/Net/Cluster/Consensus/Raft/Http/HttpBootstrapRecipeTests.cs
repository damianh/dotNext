using Microsoft.AspNetCore.Connections;
using Microsoft.AspNetCore.Hosting;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace DotNext.Net.Cluster.Consensus.Raft.Http;

/// <summary>
/// Contract checks for issue #23: bootstrap of an HTTP cluster.
/// </summary>
/// <remarks>
/// The published guide (https://dotnet.github.io/dotNext/features/cluster/raft.html) shows a node configuration with
/// <c>"coldStart" : true</c>, which is also the default. A node bootstraps only when its stored configuration is empty,
/// and then it commits a configuration that contains only itself. Bootstrap ownership therefore belongs to exactly one node.
/// </remarks>
[Collection(TestCollections.Raft)]
public sealed class HttpBootstrapRecipeTests : RaftTest
{
    private const int Port1 = 3262;
    private const int Port2 = 3263;

    // Characterization of the published configuration: two empty nodes, both with "coldStart" : true.
    // Each node commits a configuration that contains only itself and elects itself. The result is two clusters.
    [Fact(Timeout = TestTimeouts.Default)]
    public static async Task EveryNodeColdStartedFormsSeparateClusters()
    {
        using var host1 = CreateHost(Port1, coldStart: true);
        using var host2 = CreateHost(Port2, coldStart: true);
        await host1.StartAsync(TestToken);
        await host2.StartAsync(TestToken);

        var leader1 = await GetLocalClusterView(host1).WaitForLeaderAsync(DefaultTimeout, TestToken);
        var leader2 = await GetLocalClusterView(host2).WaitForLeaderAsync(DefaultTimeout, TestToken);
        NotEqual(leader1.EndPoint, leader2.EndPoint, UriEndPoint.Comparer);
        Equal(1, GetMemberCount(host1));
        Equal(1, GetMemberCount(host2));

        await host2.StopAsync(TestToken);
        await host1.StopAsync(TestToken);
    }

    // Corrected recipe: exactly one node owns the cold start, the other node starts with "coldStart" : false
    // and becomes a voting member only after the leader commits it with AddMemberAsync.
    [Fact(Timeout = TestTimeouts.Default)]
    public static async Task SingleColdStartOwnerAndJoinerFormOneCluster()
    {
        using var host1 = CreateHost(Port1, coldStart: true);
        using var host2 = CreateHost(Port2, coldStart: false);
        await host1.StartAsync(TestToken);
        await host2.StartAsync(TestToken);

        True(await GetLocalClusterView(host1).AddMemberAsync(GetLocalClusterView(host2).LocalMemberAddress, TestToken));
        await GetLocalClusterView(host2).Readiness.WaitAsync(TestToken);

        await AssertLeadershipAsync(UriEndPoint.Comparer, GetLocalClusterView(host1), GetLocalClusterView(host2));
        Equal(2, GetMemberCount(host1));
        Equal(2, GetMemberCount(host2));

        await host2.StopAsync(TestToken);
        await host1.StopAsync(TestToken);
    }

    // With persistent configuration storage, "coldStart" is consulted only while the stored configuration is empty.
    // Restarting the cold-start owner with "coldStart" : true neither bootstraps a new cluster nor drops the joined member.
    [Fact(Timeout = TestTimeouts.Default)]
    public static async Task PersistedConfigurationPreventsBootstrapOnRestart()
    {
        var node1State = GetTempPath();
        var node2State = GetTempPath();
        Directory.CreateDirectory(node1State);
        Directory.CreateDirectory(node2State);

        using (var host1 = CreateHost(Port1, coldStart: true, node1State))
        using (var host2 = CreateHost(Port2, coldStart: false, node2State))
        {
            await host1.StartAsync(TestToken);
            await host2.StartAsync(TestToken);

            True(await GetLocalClusterView(host1).AddMemberAsync(GetLocalClusterView(host2).LocalMemberAddress, TestToken));
            await GetLocalClusterView(host2).Readiness.WaitAsync(TestToken);
            await AssertLeadershipAsync(UriEndPoint.Comparer, GetLocalClusterView(host1), GetLocalClusterView(host2));

            await host2.StopAsync(TestToken);
            await host1.StopAsync(TestToken);
        }

        using (var host1 = CreateHost(Port1, coldStart: true, node1State))
        using (var host2 = CreateHost(Port2, coldStart: false, node2State))
        {
            await host1.StartAsync(TestToken);
            Equal(2, GetMemberCount(host1));

            await host2.StartAsync(TestToken);
            await AssertLeadershipAsync(UriEndPoint.Comparer, GetLocalClusterView(host1), GetLocalClusterView(host2));

            await host2.StopAsync(TestToken);
            await host1.StopAsync(TestToken);
        }
    }

    private static IRaftHttpCluster GetLocalClusterView(IHost host)
        => host.Services.GetRequiredService<IRaftHttpCluster>();

    private static int GetMemberCount(IHost host)
        => ((IRaftCluster)GetLocalClusterView(host)).Members.Count;

    private static IHost CreateHost(int port, bool coldStart, string stateRoot = null)
    {
        var configuration = new Dictionary<string, string>
        {
            ["publicEndPoint"] = $"http://localhost:{port}",
            ["coldStart"] = coldStart.ToString(),
        };

        // Persistent WAL and configuration storage that survive a restart, as in a real deployment.
        if (stateRoot is not null)
        {
            configuration[Startup.PersistentConfigurationPath] = Path.Combine(stateRoot, "config");
            configuration[Startup.LogLocationKey] = Path.Combine(stateRoot, "log");
        }

        return new HostBuilder()
            .ConfigureWebHost(webHost => webHost
                .UseKestrel(options => options.ListenLocalhost(port))
                .UseStartup<Startup>())
            .ConfigureHostOptions(static options => options.ShutdownTimeout = DefaultTimeout)
            .ConfigureAppConfiguration(builder => builder.AddInMemoryCollection(configuration))
            .ConfigureLogging(static builder => builder.SetMinimumLevel(LogLevel.Warning))
            .JoinCluster()
            .Build();
    }
}
