namespace DotNext.Net.Cluster.Consensus.Raft.Membership;

using IO;

/// <summary>
/// Provides storage for the applied cluster configuration baseline.
/// </summary>
public interface IClusterConfigurationStorage : IDisposable
{
    /// <summary>
    /// Saves the configuration to the storage.
    /// </summary>
    /// <param name="configuration">The configuration to store.</param>
    /// <param name="configurationVersion">The configuration version.</param>
    /// <param name="token">The token that can be used to cancel the operation.</param>
    /// <returns><see langword="true"/> if <paramref name="configurationVersion"/> is co</returns>
    ValueTask<bool> SaveConfigurationAsync<TConfiguration>(TConfiguration configuration, long configurationVersion, CancellationToken token = default)
        where TConfiguration : IDataTransferObject;

    /// <summary>
    /// Loads configuration from the storage.
    /// </summary>
    /// <param name="token">The token that can be used to cancel the operation.</param>
    /// <returns>The copy of the configuration.</returns>
    ValueTask<(IDataTransferObject Configuration, long Version)> LoadConfigurationAsync(CancellationToken token = default);
}

/// <summary>
/// Provides storage for the applied cluster configuration baseline and decodes configuration log entries.
/// </summary>
/// <typeparam name="TAddress">The type of the cluster member address.</typeparam>
public interface IClusterConfigurationStorage<TAddress> : IClusterConfigurationStorage
    where TAddress : notnull
{
    /// <summary>
    /// Loads configuration from the storage.
    /// </summary>
    /// <param name="token">The token that can be used to cancel the operation.</param>
    /// <returns>The copy of the configuration.</returns>
    new ValueTask<IClusterConfiguration<TAddress>> LoadConfigurationAsync(CancellationToken token = default);

    /// <summary>
    /// Decodes the configuration from its binary representation, without saving it.
    /// </summary>
    /// <remarks>
    /// Implementations must decode configuration log entries because the cluster makes the latest configuration
    /// in the log active as soon as it is appended. The storage itself persists the applied configuration baseline,
    /// which is used as the committed starting point on startup and after snapshot installation.
    /// </remarks>
    /// <param name="configuration">The binary representation of the configuration, such as a configuration log entry.</param>
    /// <param name="token">The token that can be used to cancel the operation.</param>
    /// <typeparam name="TConfiguration">The type of the configuration representation.</typeparam>
    /// <returns>The decoded configuration.</returns>
    ValueTask<IClusterConfiguration<TAddress>> ReadConfigurationAsync<TConfiguration>(TConfiguration configuration, CancellationToken token = default)
        where TConfiguration : IDataTransferObject;
    
    /// <summary>
    /// An event occurred when the configuration is changed.
    /// </summary>
    event Func<IClusterConfiguration<TAddress>, CancellationToken, ValueTask> ConfigurationChanged;
}