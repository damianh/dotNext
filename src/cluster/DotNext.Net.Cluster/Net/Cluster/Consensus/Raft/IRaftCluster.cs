namespace DotNext.Net.Cluster.Consensus.Raft;

using IO.Log;
using Membership;
using Replication;

/// <summary>
/// Represents cluster of nodes coordinated using Raft consensus protocol.
/// </summary>
public interface IRaftCluster : IReplicationCluster<IRaftLogEntry>, IPeerMesh<IRaftClusterMember>
{
    /// <summary>
    /// Represents metrics attribute containing the address of the local node.
    /// </summary>
    protected const string LocalAddressMeterAttributeName = "dotnext.raft.server.address";

    /// <summary>
    /// Gets election timeout used by local cluster member.
    /// </summary>
    TimeSpan ElectionTimeout { get; }

    /// <summary>
    /// Gets a set of visible cluster members.
    /// </summary>
    IReadOnlyCollection<IRaftClusterMember> Members { get; }

    /// <summary>
    /// Defines persistent state for the Raft-based cluster.
    /// </summary>
    new IPersistentState AuditTrail { get; }

    /// <summary>
    /// Tries to get the lease that can be used to perform the read with linearizability guarantees.
    /// </summary>
    /// <remarks>
    /// The lease is valid while the returned token is not canceled. Each call checks the lease deadline against
    /// the monotonic clock and cancels an expired lease, even if its timer has not fired yet.
    /// A token obtained earlier is canceled only when its timer fires or a later call observes the expiration,
    /// so call this method again after the read and discard the result if the new token is canceled.
    /// See <see cref="IClusterMemberConfiguration.IsLeaderLeaseEnabled"/> for the supported clock model.
    /// </remarks>
    /// <param name="token">The token representing lease.</param>
    /// <returns><see langword="true"/> if the current node is leader; otherwise, <see langword="false"/>.</returns>
    bool TryGetLeaseToken(out CancellationToken token);

    /// <summary>
    /// Gets the token that can be used to track leader state.
    /// </summary>
    /// <remarks>
    /// The token moves to canceled state if the current node downgrades to the follower state.
    /// </remarks>
    CancellationToken LeadershipToken { get; }

    /// <summary>
    /// Gets a token that remains non-canceled while the local node is a part of the majority of the cluster and
    /// has communication with the leader.
    /// </summary>
    /// <remarks>
    /// The token moves to canceled state if the current node upgrades to the candidate state or loses connection with the leader.
    /// </remarks>
    CancellationToken ConsensusToken { get; }

    /// <summary>
    /// Represents a task indicating that the current node is ready to serve requests.
    /// </summary>
    Task Readiness { get; }

    /// <inheritdoc/>
    IAuditTrail<IRaftLogEntry> IReplicationCluster<IRaftLogEntry>.AuditTrail => AuditTrail;

    /// <summary>
    /// Ensures linearizable read from underlying state machine.
    /// </summary>
    /// <param name="barrierType">Read barrier type. It has effect only when this instance is a leader.</param>
    /// <param name="token">The token that can be used to cancel the operation.</param>
    /// <returns>The task representing asynchronous result.</returns>
    /// <exception cref="ArgumentOutOfRangeException"><paramref name="barrierType"/> is invalid.</exception>
    /// <exception cref="OperationCanceledException">The operation has been canceled.</exception>
    /// <exception cref="QuorumUnreachableException">The quorum is not visible to the local node.</exception>
    ValueTask ApplyReadBarrierAsync(ReadBarrierType barrierType, CancellationToken token = default);

    /// <summary>
    /// Waits until the local node is elected as the leader.
    /// </summary>
    /// <param name="token">The token that can be used to cancel the operation.</param>
    /// <returns>The leadership token.</returns>
    /// <exception cref="OperationCanceledException">The operation has been canceled.</exception>
    /// <exception cref="ObjectDisposedException">The local node is disposed.</exception>
    /// <exception cref="QuorumUnreachableException">The local node is stopped and never can be elected as leader.</exception>
    /// <seealso cref="LeadershipToken"/>
    Task<CancellationToken> WaitForLeadershipAsync(CancellationToken token = default);

    /// <summary>
    /// Associates the configuration storage with the WAL.
    /// </summary>
    /// <param name="auditTrail">The WAL implementation.</param>
    /// <param name="configurationStorage">The configuration storage.</param>
    protected static void SetConfigurationStorage(IPersistentState auditTrail, IClusterConfigurationStorage configurationStorage)
        => auditTrail.ConfigurationStorage = configurationStorage;
}