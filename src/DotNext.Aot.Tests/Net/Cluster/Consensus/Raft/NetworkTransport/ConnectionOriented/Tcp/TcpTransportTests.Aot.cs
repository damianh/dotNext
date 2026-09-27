using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

namespace DotNext.Net.Cluster.Consensus.Raft.NetworkTransport.ConnectionOriented.Tcp;

partial class TcpTransportTests
{
    private static ILoggerFactory CreateDebugLoggerFactory(int port) => NullLoggerFactory.Instance;
}