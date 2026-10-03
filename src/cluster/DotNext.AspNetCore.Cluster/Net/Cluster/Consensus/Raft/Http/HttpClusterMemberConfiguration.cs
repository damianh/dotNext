using System.Diagnostics.CodeAnalysis;
using System.Net;
using Microsoft.AspNetCore.Connections;

namespace DotNext.Net.Cluster.Consensus.Raft.Http;

using HttpProtocolVersion = Net.Http.HttpProtocolVersion;

/// <summary>
/// Represents configuration of Raft HTTP cluster member.
/// </summary>
public class HttpClusterMemberConfiguration : ClusterMemberConfiguration, IClusterMemberConfiguration
{
    private const string DefaultClientHandlerName = "raftClient";

    private TimeSpan? requestTimeout;

    /// <summary>
    /// Gets or sets the address of the local node visible to the entire cluster.
    /// </summary>
    [DisallowNull]
    public Uri? PublicEndPoint
    {
        get;
        set
        {
            if (value is { IsAbsoluteUri: false })
                throw new ArgumentException(ExceptionMessages.AbsoluteUriExpected(value), nameof(value));

            field = value;
        }
    }

    /// <summary>
    /// Gets configuration of the bounded, process-local journal used to suppress repeated delivery
    /// of one-way messages.
    /// </summary>
    /// <remarks>
    /// The journal does not provide durable exactly-once delivery. See
    /// <see cref="RequestJournalConfiguration"/> for the retry, expiration, eviction, and restart behavior.
    /// </remarks>
    public RequestJournalConfiguration RequestJournal { get; } = new();

    /// <summary>
    /// Specifies that each request should create individual TCP connection (no KeepAlive).
    /// </summary>
    public bool OpenConnectionForEachRequest { get; set; }

    /// <summary>
    /// Gets or sets HTTP version supported by Raft implementation.
    /// </summary>
    public HttpProtocolVersion ProtocolVersion { get; set; }

    /// <summary>
    /// Gets or sets HTTP version policy.
    /// </summary>
    public HttpVersionPolicy ProtocolVersionPolicy { get; set; } = HttpVersionPolicy.RequestVersionOrLower;

    /// <summary>
    /// Gets or sets request timeout used to communicate with cluster members.
    /// </summary>
    /// <remarks>
    /// The timeout also bounds the processing of incoming AppendEntries, InstallSnapshot and Synchronize requests,
    /// so that a peer that stalls in the middle of the request body cannot hold the node for longer than that.
    /// The connection of a request that exceeds the timeout is aborted. The leader gives up on the request after its own
    /// request timeout, so all members of the cluster should use the same value.
    /// </remarks>
    /// <value>HTTP request timeout; default is <see cref="ClusterMemberConfiguration.UpperElectionTimeout"/>.</value>
    public TimeSpan RequestTimeout
    {
        get => requestTimeout ?? TimeSpan.FromMilliseconds(UpperElectionTimeout);
        set => requestTimeout = value > TimeSpan.Zero ? value : throw new ArgumentOutOfRangeException(nameof(value));
    }

    /// <summary>
    /// Gets or sets HTTP handler name used by Raft node client.
    /// </summary>
    public string ClientHandlerName
    {
        get => field is { Length: > 0 } ? field : DefaultClientHandlerName;
        set;
    }

    /// <inheritdoc />
    IEqualityComparer<EndPoint> IClusterMemberConfiguration.EndPointComparer => UriEndPoint.Comparer;
}