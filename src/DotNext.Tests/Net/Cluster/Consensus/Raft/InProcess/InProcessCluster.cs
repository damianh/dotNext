using System.Collections.Immutable;
using System.Diagnostics.CodeAnalysis;
using System.Net;

namespace DotNext.Net.Cluster.Consensus.Raft.InProcess;

using IO;
using IO.Log;
using NetworkTransport;

internal sealed class InProcessCluster : RaftCluster<InProcessClusterMember>, ILocalMember
{
    private static readonly IReadOnlyDictionary<string, string> EmptyMetadata =
        ImmutableDictionary<string, string>.Empty;

    private readonly InProcessNetwork network;
    private readonly ClusterMemberId id;
    private readonly EndPoint[] membership;
    private readonly bool startFollower;
    private bool registered;

    [SetsRequiredMembers]
    internal InProcessCluster(
        InProcessNetwork network,
        string name,
        IEnumerable<EndPoint> membership,
        IPersistentState auditTrail,
        TimeProvider timeProvider,
        TimeSpan electionTimeout,
        bool startFollower = true)
        : base(new Configuration(electionTimeout))
    {
        ArgumentNullException.ThrowIfNull(network);
        ArgumentException.ThrowIfNullOrEmpty(name);
        ArgumentNullException.ThrowIfNull(membership);
        ArgumentNullException.ThrowIfNull(auditTrail);
        ArgumentNullException.ThrowIfNull(timeProvider);

        this.network = network;
        TimeProvider = timeProvider;
        this.membership = membership.ToArray();
        this.startFollower = startFollower;
        EndPoint = new DnsEndPoint(name, 0);
        id = ClusterMemberId.FromEndPoint(EndPoint);
        AuditTrail = auditTrail;
    }

    internal EndPoint EndPoint { get; }

    internal ref readonly ClusterMemberId Id => ref id;

    internal InProcessClusterMember GetMember(EndPoint endPoint)
        => Members.Single(member => Equals(member.EndPoint, endPoint));

    internal void StartElectionTimer() => StartFollowing();

    public override async Task StartAsync(CancellationToken token = default)
    {
        var scope = await ChangeConfigurationAsync(token).ConfigureAwait(false);
        try
        {
            foreach (var address in membership)
                scope.MarkAsAdded(new(this, network, address));
        }
        finally
        {
            await scope.DisposeAsync().ConfigureAwait(false);
        }

        await base.StartAsync(token).ConfigureAwait(false);
        network.Register(this);
        registered = true;

        if (startFollower)
            StartFollowing();
    }

    public override async Task StopAsync(CancellationToken token = default)
    {
        var draining = ValueTask.CompletedTask;
        if (registered)
        {
            registered = false;
            draining = network.UnregisterAsync(this);
        }

        await Task.WhenAll(base.StopAsync(token), draining.AsTask()).ConfigureAwait(false);
    }

    internal async Task<InProcessCluster> RestartAsync(
        Func<IPersistentState, IPersistentState> reopenState,
        CancellationToken token = default)
    {
        ArgumentNullException.ThrowIfNull(reopenState);

        await StopAsync(token).ConfigureAwait(false);
        var replacement = new InProcessCluster(
            network,
            ((DnsEndPoint)EndPoint).Host,
            membership,
            reopenState(AuditTrail),
            TimeProvider,
            ElectionTimeout,
            startFollower);
        await replacement.StartAsync(token).ConfigureAwait(false);
        return replacement;
    }

    ref readonly ClusterMemberId ILocalMember.Id => ref id;

    int ILocalMember.Version => AuditTrail.Version;

    IReadOnlyDictionary<string, string> ILocalMember.Metadata => EmptyMetadata;

    bool ILocalMember.IsLeader(IRaftClusterMember member) => ReferenceEquals(Leader, member);

    ValueTask<Result<ReplicationStatus>> ILocalMember.AppendEntriesAsync<TEntry>(
        ClusterMemberId sender,
        long senderTerm,
        ILogEntryProducer<TEntry> entries,
        long prevLogIndex,
        long prevLogTerm,
        long commitIndex,
        int stateVersion,
        CancellationToken token)
        => AppendEntriesAsync(sender, senderTerm, entries, prevLogIndex, prevLogTerm, commitIndex, stateVersion, token);

    ValueTask<Result<bool>> ILocalMember.VoteAsync(
        ClusterMemberId sender,
        long term,
        long lastLogIndex,
        long lastLogTerm,
        int stateVersion,
        CancellationToken token)
        => VoteAsync(sender, term, lastLogIndex, lastLogTerm, stateVersion, token);

    ValueTask<Result<PreVoteResult>> ILocalMember.PreVoteAsync(
        ClusterMemberId sender,
        long term,
        long lastLogIndex,
        long lastLogTerm,
        int stateVersion,
        CancellationToken token)
        => PreVoteAsync(sender, term + 1L, lastLogIndex, lastLogTerm, stateVersion, token);

    ValueTask<bool> ILocalMember.ResignAsync(CancellationToken token) => ResignAsync(token);

    ValueTask<Result<HeartbeatResult>> ILocalMember.InstallSnapshotAsync<TSnapshot>(
        ClusterMemberId sender,
        long senderTerm,
        TSnapshot snapshot,
        long snapshotIndex,
        int stateVersion,
        CancellationToken token)
        => InstallSnapshotAsync(sender, senderTerm, snapshot, snapshotIndex, stateVersion, token);

    ValueTask<bool> ILocalMember.InstallConfigurationAsync<TConfiguration>(
        long senderTerm,
        TConfiguration configuration,
        long configurationVersion,
        CancellationToken token)
        => InstallConfigurationAsync(senderTerm, configuration, configurationVersion, token);

    ValueTask<long?> ILocalMember.SynchronizeAsync(long commitIndex, CancellationToken token)
        => SynchronizeAsync(commitIndex, token);

    private sealed class Configuration(TimeSpan electionTimeout) : IClusterMemberConfiguration
    {
        public double HeartbeatThreshold => 0.5D;

        public ElectionTimeout ElectionTimeout { get; } = new()
        {
            LowerValue = checked((int)electionTimeout.TotalMilliseconds),
            UpperValue = checked((int)electionTimeout.TotalMilliseconds),
        };

        public bool Standby => false;

        public bool IsLeaderLeaseEnabled => false;
    }
}

internal sealed class InProcessClusterMember(
    InProcessCluster source,
    InProcessNetwork network,
    EndPoint target)
    : RaftClusterMember(source, target)
{
    internal InProcessCluster Source => source;

    internal ClusterMemberId Id => ClusterMemberId.FromEndPoint(EndPoint);

    public override ValueTask CancelPendingRequestsAsync()
    {
        network.CancelPending(source.EndPoint, EndPoint);
        return ValueTask.CompletedTask;
    }

    private protected override Task<Result<bool>> VoteAsync(
        long term,
        long lastLogIndex,
        long lastLogTerm,
        CancellationToken token)
        => network.SendAsync(
            this,
            RaftMessageType.Vote,
            (target, requestToken) => target.VoteAsync(
                source.Id,
                term,
                lastLogIndex,
                lastLogTerm,
                source.AuditTrail.Version,
                requestToken),
            token);

    private protected override Task<Result<PreVoteResult>> PreVoteAsync(
        long term,
        long lastLogIndex,
        long lastLogTerm,
        CancellationToken token)
        => network.SendAsync(
            this,
            RaftMessageType.PreVote,
            (target, requestToken) => target.PreVoteAsync(
                source.Id,
                term,
                lastLogIndex,
                lastLogTerm,
                source.AuditTrail.Version,
                requestToken),
            token);

    private protected override Task<Result<ReplicationStatus>> AppendEntriesAsync<TEntry, TList>(
        long term,
        TList entries,
        long prevLogIndex,
        long prevLogTerm,
        long commitIndex,
        CancellationToken token)
        => network.SendAsync(
            this,
            RaftMessageType.AppendEntries,
            (target, requestToken) => target.AppendEntriesAsync(
                source.Id,
                term,
                new LogEntryProducer<TEntry>(entries),
                prevLogIndex,
                prevLogTerm,
                commitIndex,
                source.AuditTrail.Version,
                requestToken),
            token);

    private protected override Task<Result<HeartbeatResult>> InstallSnapshotAsync(
        long term,
        IRaftLogEntry snapshot,
        long snapshotIndex,
        IDataTransferObject configuration,
        long configurationVersion,
        CancellationToken token)
        => network.SendAsync(
            this,
            RaftMessageType.InstallSnapshot,
            async (target, requestToken) =>
            {
                await target.InstallConfigurationAsync(
                        term,
                        configuration,
                        configurationVersion,
                        requestToken)
                    .ConfigureAwait(false);

                return await target.InstallSnapshotAsync(
                        source.Id,
                        term,
                        snapshot,
                        snapshotIndex,
                        source.AuditTrail.Version,
                        requestToken)
                    .ConfigureAwait(false);
            },
            token);

    private protected override Task<bool> ResignAsync(CancellationToken token)
        => network.SendAsync(
            this,
            RaftMessageType.Resign,
            static (target, requestToken) => target.ResignAsync(requestToken),
            token);

    private protected override Task<IReadOnlyDictionary<string, string>> GetMetadataAsync(CancellationToken token)
        => network.SendAsync(
            this,
            RaftMessageType.Metadata,
            static (target, _) => ValueTask.FromResult(target.Metadata),
            token);

    private protected override Task<long?> SynchronizeAsync(long commitIndex, CancellationToken token)
        => network.SendAsync(
            this,
            RaftMessageType.Synchronize,
            (target, requestToken) => target.SynchronizeAsync(commitIndex, requestToken),
            token);
}
