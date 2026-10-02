using System.Diagnostics;
using System.Runtime.InteropServices;

namespace DotNext.Net.Cluster.Consensus.Raft;

using IO;
using IO.Log;
using Membership;

public partial class RaftCluster<TMember>
{
    // The latest configuration in the log is active as soon as it is appended, committed or not (Ongaro's thesis, §4.1).
    // Everything except the published active version is protected by transitionLock.
    private const int MaxStagedSnapshotConfigurations = 4;

    private readonly object stagedSnapshotConfigurationSync = new();
    private ActiveConfiguration? activeConfiguration;
    private List<StagedSnapshotConfiguration> stagedSnapshotConfigurations = [];

    /// <summary>
    /// Derives the active cluster configuration from the log.
    /// </summary>
    /// <remarks>
    /// When enabled, the latest configuration entry in the log becomes the active configuration as soon as it is appended,
    /// committed or not, so that votes, commit and lease quorums, and replication are always counted against it.
    /// The configuration reverts to the previous one in the log when an uncommitted configuration is truncated.
    /// <paramref name="storage"/> keeps the last applied configuration, which serves as the committed baseline
    /// on startup and after a snapshot is installed.
    /// The method must be called before <see cref="StartAsync(CancellationToken)"/>.
    /// </remarks>
    /// <param name="storage">The storage of the applied configuration, which also decodes configuration log entries.</param>
    /// <param name="memberFactory">The factory of cluster members.</param>
    /// <param name="addressProvider">The delegate that returns the address of the member.</param>
    /// <param name="comparer">The comparer of the addresses.</param>
    /// <typeparam name="TAddress">The type of the member address.</typeparam>
    /// <exception cref="InvalidOperationException">The cluster is already started.</exception>
    protected void UseLogConfiguration<TAddress>(IClusterConfigurationStorage<TAddress> storage,
        Func<TAddress, TMember> memberFactory,
        Func<TMember, TAddress> addressProvider,
        IEqualityComparer<TAddress>? comparer = null)
        where TAddress : notnull
    {
        ArgumentNullException.ThrowIfNull(storage);
        ArgumentNullException.ThrowIfNull(memberFactory);
        ArgumentNullException.ThrowIfNull(addressProvider);

        if (Volatile.Read(in state) is not UnstartedState)
            throw new InvalidOperationException();

        activeConfiguration = new ActiveConfiguration<TAddress>(storage, memberFactory, addressProvider,
            comparer ?? EqualityComparer<TAddress>.Default);
    }

    private void StageSnapshotConfiguration(long senderTerm, byte[] payload, long version)
    {
        lock (stagedSnapshotConfigurationSync)
        {
            if (stagedSnapshotConfigurations is [.., { Term: var newestTerm }])
            {
                if (senderTerm < newestTerm)
                    return;

                if (senderTerm > newestTerm)
                    stagedSnapshotConfigurations.Clear();
            }

            var updated = false;
            for (var i = 0; i < stagedSnapshotConfigurations.Count; i++)
            {
                if (stagedSnapshotConfigurations[i] is { Term: var term, Version: var stagedVersion } && term == senderTerm && stagedVersion == version)
                {
                    stagedSnapshotConfigurations[i] = new(senderTerm, payload, version);
                    updated = true;
                    break;
                }
            }

            if (!updated)
            {
                stagedSnapshotConfigurations.Add(new(senderTerm, payload, version));
                stagedSnapshotConfigurations.Sort(static (x, y) => x.Version.CompareTo(y.Version));
                if (stagedSnapshotConfigurations.Count > MaxStagedSnapshotConfigurations)
                    stagedSnapshotConfigurations.RemoveRange(0, stagedSnapshotConfigurations.Count - MaxStagedSnapshotConfigurations);
            }
        }
    }

    private StagedSnapshotConfiguration? GetStagedSnapshotConfiguration(long senderTerm, long snapshotIndex)
    {
        lock (stagedSnapshotConfigurationSync)
        {
            // Stale entries are harmless: the leader stages applied configurations of one term, and the latest
            // staged version at or below the snapshot index is the configuration in effect at that snapshot.
            for (var i = stagedSnapshotConfigurations.Count - 1; i >= 0; i--)
            {
                if (stagedSnapshotConfigurations[i] is { Term: var term, Version: var version } configuration
                    && term == senderTerm
                    && version <= snapshotIndex)
                {
                    return configuration;
                }
            }

            return null;
        }
    }

    private void RemoveStagedSnapshotConfigurations(long senderTerm, long snapshotIndex)
    {
        lock (stagedSnapshotConfigurationSync)
            stagedSnapshotConfigurations.RemoveAll(configuration => configuration.Term == senderTerm && configuration.Version <= snapshotIndex);
    }

    private async ValueTask<bool> PromoteStagedSnapshotConfigurationAsync(long senderTerm, long snapshotIndex,
        CancellationToken token)
    {
        Debug.Assert(transitionLock.IsLockHeld);

        if (GetStagedSnapshotConfiguration(senderTerm, snapshotIndex) is not { } configuration || AuditTrail.ConfigurationStorage is not { } configurationStorage)
            return false;

        await configurationStorage.SaveConfigurationAsync(new BinaryTransferObject(configuration.Payload), configuration.Version, token).ConfigureAwait(false);
        return true;
    }

    private void InvalidateSnapshotConfiguration(long snapshotIndex, long committedIndex)
    {
        Debug.Assert(transitionLock.IsLockHeld);

        if (activeConfiguration is { } configuration)
            configuration.Invalidate(long.Min(snapshotIndex, committedIndex));
    }

    // Rebuilds the active configuration from the storage and the whole log
    private async ValueTask LoadConfigurationAsync(CancellationToken token)
    {
        Debug.Assert(transitionLock.IsLockHeld);

        if (activeConfiguration is { } configuration)
        {
            await configuration.LoadAsync(AuditTrail, token).ConfigureAwait(false);
            await ActivateConfigurationAsync(configuration).ConfigureAwait(false);
        }
    }

    // Rescans the log from the specified index, or from an earlier index if the active configuration is stale
    private async ValueTask UpdateConfigurationAsync(long startIndex, CancellationToken token)
    {
        Debug.Assert(transitionLock.IsLockHeld);

        if (activeConfiguration is { } configuration)
        {
            configuration.Invalidate(startIndex);
            await configuration.RefreshAsync(AuditTrail, token).ConfigureAwait(false);
            await ActivateConfigurationAsync(configuration).ConfigureAwait(false);
        }
    }

    // Refreshes the active configuration, if it is stale
    private ValueTask RefreshConfigurationAsync(CancellationToken token)
        => activeConfiguration is { IsDirty: true } ? UpdateConfigurationAsync(long.MaxValue, token) : ValueTask.CompletedTask;

    private async ValueTask RefreshConfigurationAfterFailureAsync(long startIndex)
    {
        Debug.Assert(transitionLock.IsLockHeld);

        if (activeConfiguration is { } configuration)
        {
            configuration.Invalidate(startIndex);
            try
            {
                await UpdateConfigurationAsync(startIndex, LifecycleToken).ConfigureAwait(false);
            }
            catch (Exception e)
            {
                configuration.Invalidate(startIndex);
                Logger.UnhandledException(e);
            }
        }
    }

    private async ValueTask InstallSnapshotConfigurationAsync(long snapshotIndex, long committedIndex, CancellationToken token)
    {
        Debug.Assert(transitionLock.IsLockHeld);

        if (activeConfiguration is { } configuration)
        {
            await configuration.InstallSnapshotAsync(AuditTrail, snapshotIndex, committedIndex, token).ConfigureAwait(false);
            await ActivateConfigurationAsync(configuration).ConfigureAwait(false);
        }
    }

    private async ValueTask ActivateConfigurationAsync(ActiveConfiguration configuration)
    {
        Debug.Assert(transitionLock.IsLockHeld);

        configuration.GetChanges(members, out var added, out var removed);
        if (added.Count > 0 || removed.Count > 0)
        {
            await ApplyMembershipChangesAsync(added, removed, configuration.Index).ConfigureAwait(false);
            _ = DisposeMembersAsync(removed, state as LeaderState<TMember>);
        }
    }

    private async Task DisposeMembersAsync(IReadOnlySet<TMember> removed, LeaderState<TMember>? leaderState)
    {
        // The replication round in progress still counts the removed members over the previous configuration,
        // which is safe because the majorities of two configurations that differ by a single server overlap.
        // Disconnecting them in the middle of the round could cost the quorum, so they are disconnected
        // once the next round, which excludes them, completes.
        if (leaderState is not null)
        {
            try
            {
                await leaderState.ForceReplicationAsync(LifecycleToken).ConfigureAwait(false);
            }
            catch (Exception e) when (e is OperationCanceledException or NotLeaderException or ObjectDisposedException)
            {
                // leadership is lost, or the node is stopped
            }
        }

        foreach (var member in removed)
        {
            try
            {
                await member.CancelPendingRequestsAsync().ConfigureAwait(false);
            }
            catch (Exception e)
            {
                Logger.FailedToCancelPendingRequests(e);
            }
            finally
            {
                member.Dispose();
            }
        }
    }

    // A leader that is not in its own configuration keeps leading until the configuration is committed (Ongaro's thesis, §4.2.2)
    private async Task StepDownOnCommitAsync(LeaderState<TMember> leaderState, long configurationIndex)
    {
        try
        {
            await AuditTrail.WaitForApplyAsync(configurationIndex, leaderState.Token).ConfigureAwait(false);
            await StepDownIfRemovedAsync(leaderState).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            // leadership is lost, or the node is stopped
        }
        catch (ObjectDisposedException)
        {
            // the node is disposed
        }
        catch (Exception e)
        {
            Logger.UnhandledException(e);
        }
    }

    private async ValueTask StepDownIfRemovedAsync(LeaderState<TMember> leaderState)
    {
        await transitionLock.AcquireAsync(LifecycleToken).ConfigureAwait(false);
        try
        {
            if (ReferenceEquals(state, leaderState) && members.LocalMember is null)
                await FreezeAsync().ConfigureAwait(false);
        }
        finally
        {
            transitionLock.Release();
        }
    }

    private sealed record StagedSnapshotConfiguration(long Term, byte[] Payload, long Version);

    private abstract class ActiveConfiguration
    {
        private long rescanFrom = long.MaxValue;

        internal bool IsDirty => rescanFrom is not long.MaxValue;

        internal void Invalidate(long index) => rescanFrom = long.Min(rescanFrom, index);

        // The index of the active configuration, or -1 if it is not loaded yet
        internal abstract long Index { get; }

        internal abstract bool HasEntriesAfter(long index);

        internal async ValueTask RefreshAsync(IPersistentState auditTrail, CancellationToken token)
        {
            if (rescanFrom is var startIndex and not long.MaxValue)
            {
                await RescanAsync(auditTrail, startIndex, token).ConfigureAwait(false);
                rescanFrom = long.MaxValue;
            }
        }

        internal async ValueTask LoadAsync(IPersistentState auditTrail, CancellationToken token)
        {
            Reset();
            await ReloadBaselineAsync(token).ConfigureAwait(false);
            Invalidate(1L);
            await RefreshAsync(auditTrail, token).ConfigureAwait(false);
        }

        internal async ValueTask InstallSnapshotAsync(IPersistentState auditTrail, long snapshotIndex, long committedIndex, CancellationToken token)
        {
            // Committed configurations stay valid. The snapshot replaces the rest of the prefix, whose
            // configurations are covered by the leader's configuration installed in the storage.
            Fold(long.Min(snapshotIndex, committedIndex));
            Truncate(snapshotIndex);
            await ReloadBaselineAsync(token).ConfigureAwait(false);
            Invalidate(snapshotIndex + 1L);
            await RefreshAsync(auditTrail, token).ConfigureAwait(false);
        }

        private protected abstract void Reset();

        // Removes configurations at or below the specified index
        private protected abstract void Truncate(long index);

        // Moves the latest configuration at or below the specified index to the baseline
        private protected abstract void Fold(long index);

        // Takes the configuration from the storage as the baseline if it is newer
        private protected abstract ValueTask ReloadBaselineAsync(CancellationToken token);

        private protected abstract ValueTask RescanAsync(IPersistentState auditTrail, long startIndex, CancellationToken token);

        // Decodes the configuration payload received from a peer without applying it
        internal abstract ValueTask ValidateAsync(byte[] payload, CancellationToken token);

        internal abstract void GetChanges(IMemberList members, out IReadOnlySet<TMember> added, out IReadOnlySet<TMember> removed);
    }

    private sealed class ActiveConfiguration<TAddress>(
        IClusterConfigurationStorage<TAddress> storage,
        Func<TAddress, TMember> memberFactory,
        Func<TMember, TAddress> addressProvider,
        IEqualityComparer<TAddress> comparer) : ActiveConfiguration
        where TAddress : notnull
    {
        // uncommitted configurations (as of the last scan), ordered by index
        private readonly List<ConfigurationVersion> tail = new();
        private ConfigurationVersion? baseline;
        private volatile ConfigurationVersion? active;

        internal ConfigurationVersion? Active => active;

        internal override long Index => active?.Index ?? -1L;

        internal override bool HasEntriesAfter(long index) => tail is [.., { Index: var last }] && last > index;

        private void Publish() => active = tail is [.., var last] ? last : baseline;

        private protected override void Reset()
        {
            tail.Clear();
            baseline = null;
            active = null;
        }

        private protected override void Truncate(long index)
        {
            tail.RemoveAll(version => version.Index <= index);
            Publish();
        }

        private protected override void Fold(long index)
        {
            var i = tail.FindLastIndex(version => version.Index <= index);
            if (i >= 0)
            {
                if (tail[i].Index > (baseline?.Index ?? -1L))
                    baseline = tail[i];

                tail.RemoveRange(0, i + 1);
            }
        }

        private protected override async ValueTask ReloadBaselineAsync(CancellationToken token)
        {
            var (configuration, version) = await ((IClusterConfigurationStorage)storage).LoadConfigurationAsync(token).ConfigureAwait(false);
            if (baseline is null || version > baseline.Index)
            {
                baseline = new(version, configuration as IClusterConfiguration<TAddress>
                                        ?? await storage.ReadConfigurationAsync(configuration, token).ConfigureAwait(false));
                tail.RemoveAll(entry => entry.Index <= version);
            }

            Publish();
        }

        private protected override async ValueTask RescanAsync(IPersistentState auditTrail, long startIndex, CancellationToken token)
        {
            tail.RemoveAll(version => version.Index >= startIndex);
            if (baseline is null)
                await ReloadBaselineAsync(token).ConfigureAwait(false);

            startIndex = long.Max(startIndex, baseline!.Index + 1L);

            var endIndex = auditTrail.LastEntryIndex;
            if (startIndex <= endIndex)
            {
                var (snapshotIndex, configurations) = await auditTrail
                    .ReadAsync(new Reader(storage, startIndex), startIndex, endIndex, token)
                    .ConfigureAwait(false);

                if (snapshotIndex.HasValue)
                {
                    // configurations in the snapshot are committed and applied, so the storage covers them
                    Fold(snapshotIndex.GetValueOrDefault());
                    await ReloadBaselineAsync(token).ConfigureAwait(false);
                }

                var baselineIndex = baseline?.Index ?? -1L;
                foreach (var version in configurations)
                {
                    if (version.Index > baselineIndex)
                        tail.Add(version);
                }
            }

            Fold(auditTrail.LastCommittedEntryIndex);
            Publish();
        }

        internal override async ValueTask ValidateAsync(byte[] payload, CancellationToken token)
            => await storage.ReadConfigurationAsync(new BinaryTransferObject(payload), token).ConfigureAwait(false);

        internal override void GetChanges(IMemberList members, out IReadOnlySet<TMember> added, out IReadOnlySet<TMember> removed)
        {
            var addedMembers = new HashSet<TMember>(ReferenceEqualityComparer.Instance);
            var removedMembers = new HashSet<TMember>(ReferenceEqualityComparer.Instance);
            var addresses = active is { } version
                ? new HashSet<TAddress>(version.Configuration.Members, comparer)
                : new HashSet<TAddress>(comparer);

            var existing = new HashSet<TAddress>(comparer);
            foreach (var member in members.Values)
            {
                var address = addressProvider(member);
                if (!addresses.Contains(address) || !existing.Add(address))
                    removedMembers.Add(member);
            }

            foreach (var address in addresses)
            {
                if (!existing.Contains(address))
                    addedMembers.Add(memberFactory(address));
            }

            added = addedMembers;
            removed = removedMembers;
        }

        internal sealed record ConfigurationVersion(long Index, IClusterConfiguration<TAddress> Configuration);

        [StructLayout(LayoutKind.Auto)]
        private readonly struct Reader(IClusterConfigurationStorage<TAddress> storage, long startIndex)
            : ILogEntryConsumer<IRaftLogEntry, (long? SnapshotIndex, List<ConfigurationVersion> Configurations)>
        {
            public async ValueTask<(long? SnapshotIndex, List<ConfigurationVersion> Configurations)> ReadAsync<TEntry, TList>(TList entries,
                long? snapshotIndex, CancellationToken token)
                where TEntry : IRaftLogEntry
                where TList : IReadOnlyList<TEntry>
            {
                // the snapshot, if present, is the first entry of the list
                var firstIndex = snapshotIndex ?? startIndex;
                var configurations = new List<ConfigurationVersion>();
                for (var i = 0; i < entries.Count; i++)
                {
                    var entry = entries[i];
                    if (entry is { IsSnapshot: false, IsConfiguration: true })
                        configurations.Add(new(firstIndex + i, await storage.ReadConfigurationAsync(entry, token).ConfigureAwait(false)));
                }

                return (snapshotIndex, configurations);
            }
        }
    }
}
