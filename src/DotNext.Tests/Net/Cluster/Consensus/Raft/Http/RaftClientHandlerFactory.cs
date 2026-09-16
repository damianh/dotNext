using System.Diagnostics.CodeAnalysis;

namespace DotNext.Net.Cluster.Consensus.Raft.Http;

[ExcludeFromCodeCoverage]
internal sealed class RaftClientHandlerFactory : IHttpMessageHandlerFactory
{
    internal TimeSpan ConnectTimeout { get; init; } = TimeSpan.FromMilliseconds(100);

    public HttpMessageHandler CreateHandler(string name) => new SocketsHttpHandler { ConnectTimeout = ConnectTimeout };
}