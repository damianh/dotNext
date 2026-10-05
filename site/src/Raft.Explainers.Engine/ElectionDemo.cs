using System.Collections.Concurrent;
using System.Diagnostics.CodeAnalysis;
using System.Net;
using DotNext.IO;
using DotNext.IO.Log;
using DotNext.Net.Cluster;
using DotNext.Net.Cluster.Consensus.Raft;

namespace Raft.Explainers.Engine;

public sealed record RaftNodeSnapshot(string Name, string Role, long Term, string? Leader);

public sealed record RaftEvent(int Sequence, string Source, string Target, string Message);

public sealed record ElectionResult(
    string Leader,
    IReadOnlyList<RaftNodeSnapshot> Nodes,
    IReadOnlyList<RaftEvent> Events);

public static class ElectionDemo
{
    public static async Task<ElectionResult> RunAsync(CancellationToken token = default)
    {
        var network = new SimulatedNetwork();
        DnsEndPoint[] membership =
        [
            new("Ada", 0),
            new("Grace", 0),
            new("Linus", 0),
        ];

        using var stateA = new ConsensusOnlyState();
        using var stateB = new ConsensusOnlyState();
        using var stateC = new ConsensusOnlyState();
        await using var nodeA = new SimulatedNode(network, membership[0], membership, stateA, 250);
        await using var nodeB = new SimulatedNode(network, membership[1], membership, stateB, 1_500);
        await using var nodeC = new SimulatedNode(network, membership[2], membership, stateC, 2_500);

        await nodeA.StartAsync(token);
        await nodeB.StartAsync(token);
        await nodeC.StartAsync(token);

        nodeA.BeginElection();
        var elected = await nodeA.WaitForLeaderAsync(TimeSpan.FromSeconds(5), token);
        await nodeA.ForceReplicationAsync(token);
        await nodeA.WaitForLeadershipAsync(token);

        SimulatedNode[] nodes = [nodeA, nodeB, nodeC];
        var snapshots = nodes
            .Select(node => new RaftNodeSnapshot(
                node.Name,
                node.LeadershipToken.IsCancellationRequested ? "Follower" : "Leader",
                node.AuditTrail.Term,
                node.Leader is { EndPoint: DnsEndPoint leader } ? leader.Host : null))
            .ToArray();

        return new(((DnsEndPoint)elected.EndPoint).Host, snapshots, network.Events);
    }
}

internal sealed class SimulatedNetwork
{
    private readonly ConcurrentDictionary<EndPoint, SimulatedNode> nodes = new();
    private readonly ConcurrentQueue<RaftEvent> events = new();
    private int sequence;

    internal IReadOnlyList<RaftEvent> Events => events.ToArray();

    internal void Register(SimulatedNode node) => nodes[node.EndPoint] = node;

    internal void Unregister(SimulatedNode node) => nodes.TryRemove(node.EndPoint, out _);

    internal async Task<TResult> SendAsync<TResult>(
        SimulatedMember member,
        string message,
        Func<SimulatedNode, CancellationToken, ValueTask<TResult>> handler,
        CancellationToken token)
    {
        token.ThrowIfCancellationRequested();
        if (!nodes.TryGetValue(member.EndPoint, out var target))
            throw new MemberUnavailableException(member);

        events.Enqueue(new(
            Interlocked.Increment(ref sequence),
            member.Source.Name,
            target.Name,
            message));

        return await handler(target, token);
    }
}

internal sealed class SimulatedNode : RaftCluster<SimulatedMember>
{
    private readonly SimulatedNetwork network;
    private readonly IReadOnlyList<EndPoint> membership;

    [SetsRequiredMembers]
    internal SimulatedNode(
        SimulatedNetwork network,
        DnsEndPoint endPoint,
        IReadOnlyList<EndPoint> membership,
        IPersistentState auditTrail,
        int electionTimeout)
        : base(new Configuration(electionTimeout))
    {
        this.network = network;
        this.membership = membership;
        EndPoint = endPoint;
        AuditTrail = auditTrail;
    }

    internal DnsEndPoint EndPoint { get; }

    internal string Name => EndPoint.Host;

    internal ClusterMemberId Id => ClusterMemberId.FromEndPoint(EndPoint);

    internal void BeginElection() => StartFollowing();

    public override async Task StartAsync(CancellationToken token = default)
    {
        await using (var scope = await ChangeConfigurationAsync(token))
        {
            foreach (var address in membership)
                scope.MarkAsAdded(new(this, network, address));
        }

        await base.StartAsync(token);
        network.Register(this);
    }

    public override async Task StopAsync(CancellationToken token = default)
    {
        network.Unregister(this);
        await base.StopAsync(token);
    }

    internal ValueTask<Result<bool>> ReceiveVoteAsync(
        ClusterMemberId sender,
        long term,
        long lastLogIndex,
        long lastLogTerm,
        int version,
        CancellationToken token)
        => VoteAsync(sender, term, lastLogIndex, lastLogTerm, version, token);

    internal ValueTask<Result<PreVoteResult>> ReceivePreVoteAsync(
        ClusterMemberId sender,
        long nextTerm,
        long lastLogIndex,
        long lastLogTerm,
        int version,
        CancellationToken token)
        => PreVoteAsync(sender, nextTerm, lastLogIndex, lastLogTerm, version, token);

    internal async ValueTask<Result<ReplicationStatus>> ReceiveEntriesAsync<TEntry, TList>(
        SimulatedNode sender,
        long term,
        TList entries,
        long previousIndex,
        long previousTerm,
        long commitIndex,
        CancellationToken token)
        where TEntry : IRaftLogEntry
        where TList : IReadOnlyList<TEntry>
    {
        await using var producer = new LogEntryProducer<TEntry>(entries);
        return await AppendEntriesAsync(
            sender.Id,
            term,
            producer,
            previousIndex,
            previousTerm,
            commitIndex,
            sender.AuditTrail.Version,
            token);
    }

    internal ValueTask<Result<HeartbeatResult>> ReceiveSnapshotAsync(
        ClusterMemberId sender,
        long term,
        IRaftLogEntry snapshot,
        long snapshotIndex,
        int version,
        CancellationToken token)
        => InstallSnapshotAsync(sender, term, snapshot, snapshotIndex, version, token);

    internal ValueTask<bool> ReceiveConfigurationAsync(
        long term,
        IDataTransferObject configuration,
        long configurationVersion,
        CancellationToken token)
        => InstallConfigurationAsync(term, configuration, configurationVersion, token);

    internal ValueTask<long?> ReceiveReadIndexAsync(long commitIndex, CancellationToken token)
        => SynchronizeAsync(commitIndex, token);

    internal ValueTask<bool> ReceiveResignAsync(CancellationToken token) => ResignAsync(token);

    private sealed class Configuration(int electionTimeout) : IClusterMemberConfiguration
    {
        public double HeartbeatThreshold => 0.5D;

        public ElectionTimeout ElectionTimeout { get; } = new()
        {
            LowerValue = electionTimeout,
            UpperValue = electionTimeout,
        };

        public bool Standby => false;

        public bool IsLeaderLeaseEnabled => false;
    }
}

internal sealed class SimulatedMember(
    SimulatedNode source,
    SimulatedNetwork network,
    EndPoint target)
    : IRaftClusterMember, IDisposable
{
    private static readonly IReadOnlyDictionary<string, string> EmptyMetadata =
        new Dictionary<string, string>();
    private IRaftClusterMember.ReplicationState state;

    internal SimulatedNode Source => source;

    public ClusterMemberId Id => ClusterMemberId.FromEndPoint(EndPoint);

    public EndPoint EndPoint { get; } = target;

    public bool IsLeader => source.Leader?.Id == Id;

    public bool IsRemote => !Equals(source.EndPoint, EndPoint);

    public ClusterMemberStatus Status => ClusterMemberStatus.Available;

    public event Action<ClusterMemberStatusChangedEventArgs>? MemberStatusChanged
    {
        add { }
        remove { }
    }

    ref IRaftClusterMember.ReplicationState IRaftClusterMember.State => ref state;

    public Task<Result<bool>> VoteAsync(
        long term,
        long lastLogIndex,
        long lastLogTerm,
        CancellationToken token)
        => IsRemote
            ? network.SendAsync(
                this,
                "RequestVote",
                (target, requestToken) => target.ReceiveVoteAsync(
                    source.Id,
                    term,
                    lastLogIndex,
                    lastLogTerm,
                    source.AuditTrail.Version,
                    requestToken),
                token)
            : Task.FromResult(new Result<bool> { Term = term, Value = true });

    public Task<Result<PreVoteResult>> PreVoteAsync(
        long term,
        long lastLogIndex,
        long lastLogTerm,
        CancellationToken token)
        => IsRemote
            ? network.SendAsync(
                this,
                "PreVote",
                (target, requestToken) => target.ReceivePreVoteAsync(
                    source.Id,
                    term + 1L,
                    lastLogIndex,
                    lastLogTerm,
                    source.AuditTrail.Version,
                    requestToken),
                token)
            : Task.FromResult(new Result<PreVoteResult>
            {
                Term = term,
                Value = PreVoteResult.Accepted,
            });

    public Task<Result<ReplicationStatus>> AppendEntriesAsync<TEntry, TList>(
        long term,
        TList entries,
        long prevLogIndex,
        long prevLogTerm,
        long commitIndex,
        CancellationToken token)
        where TEntry : IRaftLogEntry
        where TList : IReadOnlyList<TEntry>
        => network.SendAsync(
            this,
            entries.Count > 0 ? "AppendEntries" : "Heartbeat",
            (target, requestToken) => target.ReceiveEntriesAsync<TEntry, TList>(
                source,
                term,
                entries,
                prevLogIndex,
                prevLogTerm,
                commitIndex,
                requestToken),
            token);

    public Task<Result<HeartbeatResult>> InstallSnapshotAsync(
        long term,
        IRaftLogEntry snapshot,
        long snapshotIndex,
        IDataTransferObject configuration,
        long configurationVersion,
        CancellationToken token)
        => network.SendAsync(
            this,
            "InstallSnapshot",
            async (target, requestToken) =>
            {
                await target.ReceiveConfigurationAsync(
                    term,
                    configuration,
                    configurationVersion,
                    requestToken);
                return await target.ReceiveSnapshotAsync(
                    source.Id,
                    term,
                    snapshot,
                    snapshotIndex,
                    source.AuditTrail.Version,
                    requestToken);
            },
            token);

    public Task<long?> SynchronizeAsync(long commitIndex, CancellationToken token)
        => network.SendAsync(
            this,
            "ReadIndex",
            (target, requestToken) => target.ReceiveReadIndexAsync(commitIndex, requestToken),
            token);

    public ValueTask<IReadOnlyDictionary<string, string>> GetMetadataAsync(
        bool refresh = false,
        CancellationToken token = default)
        => ValueTask.FromResult(EmptyMetadata);

    public Task<bool> ResignAsync(CancellationToken token)
        => network.SendAsync(
            this,
            "Resign",
            (target, requestToken) => target.ReceiveResignAsync(requestToken),
            token);

    public ValueTask CancelPendingRequestsAsync() => ValueTask.CompletedTask;

    public void Dispose()
    {
    }
}
