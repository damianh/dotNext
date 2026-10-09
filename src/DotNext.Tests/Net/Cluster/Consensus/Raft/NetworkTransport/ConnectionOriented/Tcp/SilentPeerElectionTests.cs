using System.Net;
using System.Net.Sockets;

namespace DotNext.Net.Cluster.Consensus.Raft.NetworkTransport.ConnectionOriented.Tcp;

using Membership;

/// <summary>
/// A majority must elect a leader while the third member is silent: its host is behind a network split, so the TCP
/// connection is established but no response ever arrives (a blackhole, not a refused connection).
/// </summary>
[Collection(TestCollections.Raft)]
public sealed class SilentPeerElectionTests : RaftTest
{
    private const int LowerElectionTimeout = 1000, UpperElectionTimeout = 2000;

    [Theory(Timeout = TestTimeouts.Default)]
    [InlineData(500)]  // below the election timeout: elects a leader
    [InlineData(3000)] // above the upper election timeout: no leader is ever elected
    public static async Task MajorityElectsLeaderWhileThirdMemberIsSilent(int requestTimeoutMs)
    {
        // The kernel completes the handshake of a listening socket that never accepts, and buffers what the peers send.
        using var silent = new TcpListener(IPAddress.Loopback, 0);
        silent.Start(backlog: 16);
        var ports = new[] { FreePort(), FreePort(), ((IPEndPoint)silent.LocalEndpoint).Port };

        await using var host1 = new RaftCluster(CreateConfiguration(ports, 0, requestTimeoutMs)) { AuditTrail = new ConsensusOnlyState() };
        await using var host2 = new RaftCluster(CreateConfiguration(ports, 1, requestTimeoutMs)) { AuditTrail = new ConsensusOnlyState() };
        await Task.WhenAll(host1.StartAsync(TestToken), host2.StartAsync(TestToken));

        // Ten upper election timeouts: Raft only needs one round in which a candidate collects the vote of its peer.
        var leader = await host1.WaitForLeaderAsync(TimeSpan.FromMilliseconds(UpperElectionTimeout * 10), TestToken);
        NotNull(leader);

        await host1.StopAsync(TestToken);
        await host2.StopAsync(TestToken);
    }

    private static int FreePort()
    {
        using var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        return ((IPEndPoint)listener.LocalEndpoint).Port;
    }

    private static RaftCluster.TcpConfiguration CreateConfiguration(int[] ports, int id, int requestTimeoutMs)
    {
        var result = new RaftCluster.TcpConfiguration(new IPEndPoint(IPAddress.Loopback, ports[id]))
        {
            ColdStart = false,
            LowerElectionTimeout = LowerElectionTimeout,
            UpperElectionTimeout = UpperElectionTimeout,
            RequestTimeout = TimeSpan.FromMilliseconds(requestTimeoutMs),
            ConfigurationStorage = null,
        };

        var builder = IsType<InMemoryClusterConfigurationStorage<EndPoint>>(result.ConfigurationStorage, exactMatch: false)
            .CreateInitialConfigurationBuilder();
        foreach (var port in ports)
            builder.Add(new IPEndPoint(IPAddress.Loopback, port));

        builder.Build();
        return result;
    }
}
