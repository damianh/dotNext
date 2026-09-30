using DotNext.Net.Cluster.Consensus.Raft.Membership;

namespace DotNext.Net.Cluster.Consensus.Raft.StateMachine;

partial class WriteAheadLog
{
    private volatile bool configurationManaged;

    /// <inheritdoc/>
    public IClusterConfigurationStorage? ConfigurationStorage { get; set; }

    /// <inheritdoc/>
    bool IManagedConfigurationAuditTrail.IsConfigurationManaged
    {
        get => configurationManaged;
        set => configurationManaged = value;
    }
}