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
/// and then it commits a configuration that contains only itself.
/// </remarks>
[Collection(TestCollections.Raft)]
public sealed class HttpBootstrapRecipeTests : RaftTest
{
    private const int Port1 = 3262;
    private const int Port2 = 3263;

    // Two empty nodes deployed with the published configuration.
    // Expected: they form one cluster with a single leader.
    [Fact(Timeout = TestTimeouts.Default)]
    public static async Task NodesDeployedWithPublishedRecipeFormOneCluster()
    {
        using var host1 = CreateHost(Port1, coldStart: true);
        using var host2 = CreateHost(Port2, coldStart: true);
        await host1.StartAsync(TestToken);
        await host2.StartAsync(TestToken);

        var leader1 = await GetLocalClusterView(host1).WaitForLeaderAsync(DefaultTimeout, TestToken);
        var leader2 = await GetLocalClusterView(host2).WaitForLeaderAsync(DefaultTimeout, TestToken);
        Equal(leader1.EndPoint, leader2.EndPoint, UriEndPoint.Comparer);

        await host2.StopAsync(TestToken);
        await host1.StopAsync(TestToken);
    }

    private static IRaftHttpCluster GetLocalClusterView(IHost host)
        => host.Services.GetRequiredService<IRaftHttpCluster>();

    private static IHost CreateHost(int port, bool coldStart, string persistentConfigPath = null)
    {
        var configuration = new Dictionary<string, string>
        {
            ["publicEndPoint"] = $"http://localhost:{port}",
            ["coldStart"] = coldStart.ToString(),
        };

        if (persistentConfigPath is not null)
            configuration[Startup.PersistentConfigurationPath] = persistentConfigPath;

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
