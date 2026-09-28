using System.Net;

namespace DotNext.Net.Cluster.Consensus.Raft;

/// <summary>
/// Represents configuration of cluster member.
/// </summary>
public interface IClusterMemberConfiguration
{
    /// <summary>
    /// Gets or sets threshold of the heartbeat timeout.
    /// </summary>
    /// <remarks>
    /// The threshold should be in range (0, 1). The heartbeat timeout is computed as
    /// node election timeout X threshold. The default is 0.5.
    /// </remarks>
    double HeartbeatThreshold { get; }

    /// <summary>
    /// Gets leader election timeout settings.
    /// </summary>
    ElectionTimeout ElectionTimeout { get; }

    /// <summary>
    /// A bound on clock drift across servers.
    /// </summary>
    /// <remarks>
    /// Over a given time period, no server’s clock increases more than this bound times any other.
    /// The bound applies to the monotonic clock of <see cref="TimeProvider.GetTimestamp"/>, not to wall-clock time.
    /// The value must be finite and at least 1. The leader lease lasts <see cref="ElectionTimeout.LowerValue"/>
    /// divided by this bound, so a value below 1 makes the lease longer than the election timeout and is not supported.
    /// Configurations that do not expose this property use 1.
    /// </remarks>
    /// <seealso cref="IsLeaderLeaseEnabled"/>
    double ClockDriftBound => 1D;

    /// <summary>
    /// Gets a value indicating that the cluster member
    /// represents standby node which will never become a leader.
    /// </summary>
    bool Standby { get; }

    /// <summary>
    /// Gets a value indicating that the follower node should not try to upgrade
    /// to the candidate state if the leader is reachable via the network.
    /// </summary>
    bool AggressiveLeaderStickiness => false;

    /// <summary>
    /// Gets a value representing the maximum number of replication steps allowed for the follower to be behind the leader.
    /// </summary>
    int MaxReplicationLag => 16;
    
    /// <summary>
    /// Gets a value indicating that the lease-based linearizable read is enabled on the leader node.
    /// </summary>
    /// <remarks>
    /// After a majority acknowledges a heartbeat round, the leader holds a lease for
    /// <see cref="ElectionTimeout.LowerValue"/> divided by <see cref="ClockDriftBound"/>, measured from the start of the round.
    /// Followers refuse to vote while they have heard from the leader within their election timeout,
    /// so no other leader can be elected while the lease is valid. This holds under the following assumptions:
    /// <list type="bullet">
    /// <item><description>The monotonic clocks of the members drift apart by no more than <see cref="ClockDriftBound"/>.
    /// A clock that stops while the process or host is suspended violates this assumption.</description></item>
    /// <item><description>All members use the same lease setting, <see cref="ElectionTimeout.LowerValue"/> and <see cref="ClockDriftBound"/>.</description></item>
    /// <item><description>Members keep their persistent state across restarts. With leases enabled, a started member refuses
    /// to vote for one election timeout, because it cannot know whether it acknowledged a lease before a crash.
    /// This can delay the first election after the cluster starts by up to one election timeout.</description></item>
    /// </list>
    /// Timer callbacks may run late: the lease validity is checked against the monotonic clock, not against the timer.
    /// </remarks>
    /// <seealso cref="IRaftCluster.TryGetLeaseToken(out CancellationToken)"/>
    bool IsLeaderLeaseEnabled { get; }

    /// <summary>
    /// Gets comparer for endpoint address.
    /// </summary>
    IEqualityComparer<EndPoint> EndPointComparer => EqualityComparer<EndPoint>.Default;
}