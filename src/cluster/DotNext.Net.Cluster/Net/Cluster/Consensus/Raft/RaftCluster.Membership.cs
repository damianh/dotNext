using System.Diagnostics;
using System.Runtime.InteropServices;

namespace DotNext.Net.Cluster.Consensus.Raft;

using Collections.Specialized;
using ReplicationUtils;
using Membership;
using Threading;

public partial class RaftCluster<TMember>
{
    private interface IMemberList : IReadOnlyDictionary<ClusterMemberId, TMember>
    {
        TMember? LocalMember { get; }
        
        new IReadOnlyCollection<TMember> Values { get; }

        bool TryAdd(TMember member, out IMemberList list);

        TMember? TryRemove(ClusterMemberId id, out IMemberList list);

        internal static IMemberList Empty { get; } = new MemberList();
    }

    private sealed class MemberList : Dictionary<ClusterMemberId, TMember>, IMemberList
    {
        private TMember? localMember;
        
        internal MemberList()
            : base(10)
        {
        }

        private MemberList(MemberList origin)
            : base(origin)
            => localMember = origin.localMember;

        TMember? IMemberList.LocalMember => localMember;

        IReadOnlyCollection<TMember> IMemberList.Values => Values;

        bool IMemberList.TryAdd(TMember member, out IMemberList list)
        {
            MemberList tmp;

            if (!ContainsKey(member.Id) && (tmp = new(this)).TryAdd(member.Id, member))
            {
                list = tmp;
                return true;
            }

            list = this;
            return false;
        }

        private new bool TryAdd(ClusterMemberId id, TMember member)
        {
            if (!base.TryAdd(id, member))
                return false;
            
            if (!member.IsRemote)
                localMember = member;

            return true;
        }

        TMember? IMemberList.TryRemove(ClusterMemberId id, out IMemberList list)
        {
            MemberList tmp;

            if (ContainsKey(id) && (tmp = new(this)).TryRemove(id) is { } result)
            {
                list = tmp;
            }
            else
            {
                result = null;
                list = this;
            }

            return result;
        }

        private TMember? TryRemove(ClusterMemberId id)
        {
            if (!Remove(id, out var result))
                return null;

            if (ReferenceEquals(result, localMember))
                localMember = null;

            return result;
        }
    }

    /// <summary>
    /// Indicates that the caller is trying to add or remove cluster member concurrently.
    /// </summary>
    /// <remarks>
    /// The current implementation of Raft doesn't support adding or removing multiple cluster members at a time.
    /// </remarks>
    public sealed class ConcurrentMembershipModificationException : RaftProtocolException
    {
        internal ConcurrentMembershipModificationException()
            : base(ExceptionMessages.ConcurrentMembershipUpdate)
        {
        }
    }

    private readonly AsyncExclusiveLock membershipLock;
    private IMemberList members;
    private InvocationList<Action<RaftCluster<TMember>, RaftClusterMemberEventArgs<TMember>>> memberAddedHandlers, memberRemovedHandlers;

    /// <summary>
    /// Gets the member by its identifier.
    /// </summary>
    /// <param name="id">The identifier of the cluster member.</param>
    /// <returns><see langword="true"/> if member found; otherwise, <see langword="false"/>.</returns>
    protected TMember? TryGetMember(ClusterMemberId id)
        => members.GetValueOrDefault(id);

    /// <summary>
    /// An event raised when new cluster member is detected.
    /// </summary>
    public event Action<RaftCluster<TMember>, RaftClusterMemberEventArgs<TMember>> MemberAdded
    {
        add => memberAddedHandlers += value;
        remove => memberAddedHandlers -= value;
    }

    /// <inheritdoc />
    event Action<IPeerMesh, PeerEventArgs> IPeerMesh.PeerDiscovered
    {
        add => memberAddedHandlers += value;
        remove => memberAddedHandlers -= value;
    }

    private void OnMemberAdded(TMember member)
    {
        if (!memberAddedHandlers.IsEmpty)
        {
            try
            {
                memberAddedHandlers.Invoke(this, new(member));
            }
            catch (Exception e)
            {
                Logger.UnhandledException(e);
            }
        }
    }

    /// <summary>
    /// An event raised when cluster member is removed gracefully.
    /// </summary>
    public event Action<RaftCluster<TMember>, RaftClusterMemberEventArgs<TMember>> MemberRemoved
    {
        add => memberRemovedHandlers += value;
        remove => memberRemovedHandlers -= value;
    }

    /// <inheritdoc />
    event Action<IPeerMesh, PeerEventArgs> IPeerMesh.PeerGone
    {
        add => memberRemovedHandlers += value;
        remove => memberRemovedHandlers -= value;
    }

    private void OnMemberRemoved(TMember member)
    {
        if (!memberRemovedHandlers.IsEmpty)
        {
            try
            {
                memberRemovedHandlers.Invoke(this, new(member));
            }
            catch (Exception e)
            {
                Logger.UnhandledException(e);
            }
        }
    }

    /// <summary>
    /// Announces a new member in the cluster.
    /// </summary>
    /// <remarks>
    /// The new configuration is built from the latest configuration in the log: the method first waits until the leader
    /// has committed the latest configuration in its log and an entry of its own term (or, if the active configuration
    /// is not derived from the log, until the leader has applied its whole log), so a configuration change that is still
    /// pending cannot be overwritten. If the active configuration is derived from the log, the member appears in
    /// <see cref="Members"/> as soon as the new configuration is appended; the method returns when the configuration
    /// is committed and applied by the leader.
    /// </remarks>
    /// <typeparam name="TAddress">The type of the member address.</typeparam>
    /// <param name="member">The cluster member client used to catch up its state.</param>
    /// <param name="rounds">The number of warmup rounds.</param>
    /// <param name="configurationStorage">The configuration storage.</param>
    /// <param name="addressProvider">The delegate that allows to get the address of the member.</param>
    /// <param name="token">The token that can be used to cancel the operation.</param>
    /// <returns>
    /// <see langword="true"/> if the node has been added to the cluster successfully;
    /// <see langword="false"/> if the node rejects the replication or the address of the node cannot be committed.
    /// </returns>
    /// <exception cref="ArgumentOutOfRangeException"><paramref name="rounds"/> is less than or equal to zero.</exception>
    /// <exception cref="OperationCanceledException">The operation has been canceled or the cluster elects a new leader.</exception>
    /// <exception cref="NotLeaderException">The current node is not a leader.</exception>
    /// <exception cref="ConcurrentMembershipModificationException">The method is called concurrently.</exception>
    protected async Task<bool> AddMemberAsync<TAddress>(TMember member, int rounds, IClusterConfigurationStorage<TAddress> configurationStorage, Func<TMember, TAddress> addressProvider, CancellationToken token = default)
        where TAddress : notnull
    {
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(rounds);

        var leaderState = LeaderStateOrException;
        var tokenSource = CombineTokens(token, leaderState.Token);
        var process = new ReplicationProcess<TMember>(member, replicationLag)
        {
            Logger = Logger,
            Term = leaderState.Term,
            AuditTrail = AuditTrail,
        };
        var lockTaken = false;
        try
        {
            lockTaken = membershipLock.TryAcquire();
            if (!lockTaken)
                throw new ConcurrentMembershipModificationException();

            var config = await LoadLatestConfigurationAsync(leaderState, configurationStorage, tokenSource.Token).ConfigureAwait(false);
            if (!IClusterConfiguration<TAddress>.TryAdd(ref config, addressProvider(member)))
                return false;

            // assume that the member is up-to-date with the leader
            member.State.Initialize(AuditTrail);

            // catch up node
            if (!await process.CatchUpAsync(rounds, tokenSource.Token).ConfigureAwait(false))
                return false;

            var commitIndex = await AppendConfigurationAsync(leaderState, config, tokenSource.Token).ConfigureAwait(false);

            // ensure that the configuration is committed
            await AuditTrail.WaitForApplyAsync(commitIndex, tokenSource.Token).ConfigureAwait(false);
        }
        catch (OperationCanceledException e) when (e.CausedBy(tokenSource, leaderState.Token))
        {
            throw new NotLeaderException(e);
        }
        catch (OperationCanceledException e) when (e.CancellationToken == tokenSource.Token)
        {
            throw new OperationCanceledException(e.Message, e, tokenSource.CancellationOrigin);
        }
        finally
        {
            await tokenSource.DisposeAsync().ConfigureAwait(false);
            process.Dispose();
            
            if (lockTaken)
                membershipLock.Release();
        }

        return true;
    }

    /// <summary>
    /// Removes the member from the cluster.
    /// </summary>
    /// <remarks>
    /// The new configuration is built from the latest configuration in the log: the method first waits until the leader
    /// has committed the latest configuration in its log and an entry of its own term (or, if the active configuration
    /// is not derived from the log, until the leader has applied its whole log), so a configuration change that is still
    /// pending cannot be overwritten. Removing the last configured member is rejected and returns
    /// <see langword="false"/>. If the active configuration is derived from the log, the member disappears from
    /// <see cref="Members"/> as soon as the new configuration is appended; the method returns when the configuration
    /// is committed and applied by the leader. A leader that removes itself steps down before the method returns.
    /// </remarks>
    /// <typeparam name="TAddress">The type of the member address.</typeparam>
    /// <param name="id">The cluster member to remove.</param>
    /// <param name="configurationStorage">The configuration storage.</param>
    /// <param name="addressProvider">The delegate that allows to get the address of the member.</param>
    /// <param name="token">The token that can be used to cancel the operation.</param>
    /// <returns>
    /// <see langword="true"/> if the node has been removed from the cluster successfully;
    /// <see langword="false"/> if the node rejects the replication, the address of the node cannot be committed,
    /// or removing the member would leave the configuration empty.
    /// </returns>
    /// <exception cref="NotLeaderException">The current node is not a leader.</exception>
    /// <exception cref="OperationCanceledException">The operation has been canceled or the cluster elects a new leader.</exception>
    /// <exception cref="ConcurrentMembershipModificationException">The method is called concurrently.</exception>
    protected async Task<bool> RemoveMemberAsync<TAddress>(ClusterMemberId id, IClusterConfigurationStorage<TAddress> configurationStorage, Func<TMember, TAddress> addressProvider, CancellationToken token = default)
        where TAddress : notnull
    {
        var leaderState = LeaderStateOrException;
        var lockTaken = false;
        var tokenSource = CombineTokens(token, leaderState.Token);

        try
        {
            lockTaken = membershipLock.TryAcquire();
            if (!lockTaken)
                throw new ConcurrentMembershipModificationException();

            if (members.TryGetValue(id, out var member))
            {
                var config = await LoadLatestConfigurationAsync(leaderState, configurationStorage, tokenSource.Token).ConfigureAwait(false);
                if (TryBuildNonEmptyRemoval(ref config, addressProvider(member)))
                {
                    var removingSelf = !member.IsRemote;
                    var commitIndex = await AppendConfigurationAsync(leaderState, config, tokenSource.Token).ConfigureAwait(false);
                    await AuditTrail.WaitForApplyAsync(commitIndex, tokenSource.Token).ConfigureAwait(false);

                    // the leader removing itself steps down once the configuration is committed
                    if (removingSelf && activeConfiguration is not null)
                        await StepDownIfRemovedAsync(leaderState).ConfigureAwait(false);

                    return true;
                }
            }
        }
        catch (OperationCanceledException e) when (e.CausedBy(tokenSource, leaderState.Token))
        {
            throw new NotLeaderException(e);
        }
        catch (OperationCanceledException e) when (e.CancellationToken == tokenSource.Token)
        {
            throw new OperationCanceledException(e.Message, e, tokenSource.CancellationOrigin);
        }
        finally
        {
            await tokenSource.DisposeAsync().ConfigureAwait(false);
            
            if (lockTaken)
                membershipLock.Release();
        }

        return false;
    }

    private async ValueTask ProcessMembershipChangesAsync(IReadOnlySet<TMember> added, IReadOnlySet<TMember> removed)
    {
        try
        {
            await ApplyMembershipChangesAsync(added, removed, configurationIndex: null).ConfigureAwait(false);
        }
        finally
        {
            transitionLock.Release();
        }
        
        // stop clients
        foreach (var member in removed)
        {
            try
            {
                await member.CancelPendingRequestsAsync().ConfigureAwait(false);
            }
            finally
            {
                member.Dispose();
            }
        }
    }

    // configurationIndex is the index of the active configuration derived from the log, or null if the changes are
    // reported by ChangeConfigurationAsync
    private async ValueTask ApplyMembershipChangesAsync(IReadOnlySet<TMember> added, IReadOnlySet<TMember> removed, long? configurationIndex)
    {
        Debug.Assert(transitionLock.IsLockHeld);

        var membersCopy = members;

        // remove nodes
        foreach (var member in removed)
        {
            if (ReferenceEquals(member, membersCopy.TryRemove(member.Id, out membersCopy)))
            {
                OnMemberRemoved(member);
            }
        }

        // add nodes
        foreach (var member in added)
        {
            if (membersCopy.TryAdd(member, out membersCopy))
            {
                OnMemberAdded(member);
            }
        }

        var stepDownOnCommit = default(LeaderState<TMember>);
        switch (membersCopy.LocalMember)
        {
            case null when members.LocalMember is null:
                break;
            case null when configurationIndex is null:
                // local member is removed, but can be added later, so the state is resumable
                await FreezeAsync().ConfigureAwait(false);
                break;
            case null when state is UnstartedState:
                // the initial state is chosen on startup
                break;
            case null when state is LeaderState<TMember> leaderState:
                // the leader manages the cluster without itself until the configuration is committed
                stepDownOnCommit = leaderState;
                break;
            case null:
                await FreezeAsync().ConfigureAwait(false);
                break;
            case not null when state is not UnstartedState && members.LocalMember is null:
                // local member is added
                await UnfreezeAsync().ConfigureAwait(false);
                break;
        }

        // rewrite the list of members
        members = membersCopy;
        Interlocked.MemoryBarrierProcessWide();

        if (stepDownOnCommit is not null)
            _ = StepDownOnCommitAsync(stepDownOnCommit, configurationIndex.GetValueOrDefault());
    }
    
    /// <summary>
    /// Notifies that the member is unavailable.
    /// </summary>
    /// <remarks>
    /// It's an infrastructure method that can be used to remove unavailable member from the cluster configuration
    /// at the leader side.
    /// </remarks>
    /// <param name="member">The member that is considered as unavailable.</param>
    /// <param name="term">The cluster term at the point in time when the member was detected as unavailable.</param>
    /// <param name="token">The token associated with <see cref="LeadershipToken"/> that identifies the leader state at the time of detection.</param>
    /// <returns>The task representing asynchronous result.</returns>
    protected virtual ValueTask UnavailableMemberDetected(TMember member, long term, CancellationToken token)
        => token.IsCancellationRequested ? ValueTask.FromCanceled(token) : ValueTask.CompletedTask;

    /// <summary>
    /// Provides the helper for implementing <see cref="UnavailableMemberDetected(TMember, long, CancellationToken)"/> method.
    /// </summary>
    /// <remarks>
    /// The removal is built from the latest configuration in the log: the helper waits until the leader
    /// has applied its log, then appends the new configuration without waiting for it to be applied.
    /// Call it only from <see cref="UnavailableMemberDetected(TMember, long, CancellationToken)"/>, which runs
    /// under the same membership lock as <see cref="AddMemberAsync{TAddress}(TMember, int, IClusterConfigurationStorage{TAddress}, Func{TMember, TAddress}, CancellationToken)"/>
    /// and <see cref="RemoveMemberAsync{TAddress}(ClusterMemberId, IClusterConfigurationStorage{TAddress}, Func{TMember, TAddress}, CancellationToken)"/>.
    /// </remarks>
    /// <param name="configurationStorage">The configuration storage.</param>
    /// <param name="address">The address of the cluster member.</param>
    /// <param name="term">The cluster term at the point in time when the member was detected as unavailable.</param>
    /// <param name="token">The token that can be used to cancel the operation.</param>
    /// <typeparam name="TAddress">The type of the address.</typeparam>
    /// <exception cref="NotLeaderException">The current node is not a leader, or it is no longer the leader of <paramref name="term"/>.</exception>
    protected async ValueTask UnavailableMemberDetected<TAddress>(IClusterConfigurationStorage<TAddress> configurationStorage,
        TAddress address,
        long term,
        CancellationToken token)
        where TAddress : notnull
    {
        var leaderState = LeaderStateOrException;
        if (leaderState.Term != term)
            throw new NotLeaderException();

        var tokenSource = CombineTokens(token, leaderState.Token);
        try
        {
            var config = await LoadLatestConfigurationAsync(leaderState, configurationStorage, tokenSource.Token).ConfigureAwait(false);
            if (TryBuildNonEmptyRemoval(ref config, address))
            {
                await AppendConfigurationAsync(leaderState, config, tokenSource.Token).ConfigureAwait(false);
            }
        }
        catch (OperationCanceledException e) when (e.CausedBy(tokenSource, leaderState.Token))
        {
            throw new NotLeaderException(e);
        }
        catch (OperationCanceledException e) when (e.CancellationToken == tokenSource.Token)
        {
            throw new OperationCanceledException(e.Message, e, tokenSource.CancellationOrigin);
        }
        finally
        {
            await tokenSource.DisposeAsync().ConfigureAwait(false);
        }
    }

    private static bool TryBuildNonEmptyRemoval<TAddress>(ref IClusterConfiguration<TAddress> configuration, TAddress address)
        where TAddress : notnull
        => IClusterConfiguration<TAddress>.TryRemove(ref configuration, address) && configuration.Members.Count > 0;

    // Without a log-derived configuration, a configuration takes effect on apply (the storage holds the last applied
    // configuration), so a change must not be built before every configuration entry in the log is applied: otherwise
    // a pending change (e.g. an automatic removal, or an entry inherited from the previous leader) is overwritten.
    // With a log-derived configuration, the change is built from the latest configuration in the log, which must be
    // committed first (Ongaro's thesis, §4.1), together with the no-op of the current term (§4.1, the fix for
    // single-server changes across leader changes).
    // The caller must hold membershipLock, so no other configuration can be appended concurrently.
    private async ValueTask<IClusterConfiguration<TAddress>> LoadLatestConfigurationAsync<TAddress>(LeaderState<TMember> leaderState,
        IClusterConfigurationStorage<TAddress> configurationStorage,
        CancellationToken token)
        where TAddress : notnull
    {
        leaderState.ForceReplication();
        if (activeConfiguration is ActiveConfiguration<TAddress> configuration)
        {
            if (configuration.IsDirty)
            {
                await transitionLock.AcquireAsync(token).ConfigureAwait(false);
                try
                {
                    await RefreshConfigurationAsync(token).ConfigureAwait(false);
                }
                finally
                {
                    transitionLock.Release();
                }
            }

            while (true)
            {
                var version = configuration.Active;
                Debug.Assert(version is not null);

                await AuditTrail.WaitForApplyAsync(long.Max(version.Index, leaderState.WriteBarrier), token).ConfigureAwait(false);
                if (ReferenceEquals(version, configuration.Active))
                    return version.Configuration;
            }
        }

        await AuditTrail.WaitForApplyAsync(AuditTrail.LastEntryIndex, token).ConfigureAwait(false);
        return await configurationStorage.LoadConfigurationAsync(token).ConfigureAwait(false);
    }

    // Rejects terms already stale under the append lock. Term updates are not serialized here; if
    // the term advances after the check, the entry is still ordered before any newer-term entry.
    private async ValueTask<long> AppendConfigurationAsync<TAddress>(LeaderState<TMember> leaderState,
        IClusterConfiguration<TAddress> configuration,
        CancellationToken token)
        where TAddress : notnull
    {
        long index;
        if (activeConfiguration is null)
        {
            index = await AuditTrail.AppendInCurrentTermAsync(configuration, leaderState.Term, token).ConfigureAwait(false);
        }
        else
        {
            // The configuration is active once appended (Ongaro's thesis, §4.1). A replication round that started
            // before the activation counts its quorum over the previous configuration, which is safe because
            // the majorities of two configurations that differ by a single server always overlap.
            var startIndex = AuditTrail.LastEntryIndex + 1L;
            try
            {
                index = await AuditTrail.AppendInCurrentTermAsync(configuration, leaderState.Term, token).ConfigureAwait(false);
            }
            finally
            {
                await transitionLock.AcquireAsync(LifecycleToken).ConfigureAwait(false);
                try
                {
                    await UpdateConfigurationAsync(startIndex, LifecycleToken).ConfigureAwait(false);
                }
                finally
                {
                    transitionLock.Release();
                }
            }
        }

        leaderState.ForceReplication();
        return index;
    }

    /// <summary>
    /// Initiates configuration change.
    /// </summary>
    /// <param name="token">The token that can be used to cancel the operation.</param>
    /// <returns>The scope that can be used to report the configuration changes.</returns>
    protected async ValueTask<ConfigurationChangeScope> ChangeConfigurationAsync(CancellationToken token)
    {
        await transitionLock.AcquireAsync(token).ConfigureAwait(false);
        return new(this);
    }

    /// <summary>
    /// Represents configuration change scope.
    /// </summary>
    [StructLayout(LayoutKind.Auto)]
    protected readonly struct ConfigurationChangeScope : IAsyncDisposable
    {
        private readonly RaftCluster<TMember> cluster;
        private readonly HashSet<TMember> added, removed;

        internal ConfigurationChangeScope(RaftCluster<TMember> cluster)
        {
            this.cluster = cluster;
            added = new();
            removed = new();
        }

        /// <summary>
        /// Marks the member as removed from the configuration.
        /// </summary>
        /// <param name="member">The cluster member marked as removed.</param>
        public void MarkAsRemoved(TMember member)
            => removed.Add(member);

        /// <summary>
        /// Marks the member as added to the configuration.
        /// </summary>
        /// <param name="member">The cluster member marked as added.</param>
        public void MarkAsAdded(TMember member)
            => added.Add(member);

        /// <summary>
        /// Gets a collection of existing members.
        /// </summary>
        public IReadOnlyDictionary<ClusterMemberId, TMember> Members => cluster.members;

        /// <summary>
        /// Closes the scope.
        /// </summary>
        public ValueTask DisposeAsync() => cluster.ProcessMembershipChangesAsync(added, removed);
    }
}