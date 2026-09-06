using Microsoft.Extensions.DependencyInjection;

namespace DotNext.Net.Cluster.Consensus.Raft.Http;

using InProcess;

public sealed class RaftHttpClusterDependencyTests : RaftTest
{
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public static async Task ResolvesTimeProviderFromServices(bool registerProvider)
    {
        var services = new ServiceCollection();
        services.AddLogging()
            .UseConsensusOnlyLog()
            .UseInMemoryConfigurationStorage()
            .ConfigureLocalNode(static config => config.PublicEndPoint = new Uri("http://localhost:3262"));

        TimeProvider expected = TimeProvider.System;
        if (registerProvider)
        {
            expected = new ManualTimeProvider();
            services.AddSingleton(expected);
        }

        await using var provider = services.BuildServiceProvider();
        var cluster = provider.GetRequiredService<IRaftCluster>();

        Same(expected, ((IRaftStateMachine)cluster).TimeProvider);
    }
}
