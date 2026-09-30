namespace DotNext.Net.Cluster.Consensus.Raft;

/// <summary>
/// Represents a log whose configuration entries can be owned by a running cluster.
/// </summary>
internal interface IManagedConfigurationAuditTrail
{
    /// <summary>
    /// Gets or sets a value indicating that a running cluster derives its active configuration from this log.
    /// </summary>
    /// <remarks>
    /// While it is set, a configuration entry must be appended by the cluster's membership API,
    /// which activates it on the leader and allows one change at a time.
    /// </remarks>
    bool IsConfigurationManaged { get; set; }
}
