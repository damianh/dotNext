using System.Diagnostics.CodeAnalysis;
using System.Net;
using System.Runtime.CompilerServices;

namespace DotNext.Net.Cluster.Consensus.Raft.InProcess;

using Membership;
using StateMachine;
using Threading;

/// <summary>
/// Five WAL-backed voters and one joiner (by default) on a fully held in-process network.
/// </summary>
/// <remarks>
/// Nothing progresses on its own: elections need <see cref="ElectAsync"/> and RPCs need
/// <see cref="PumpAsync"/>. Each node derives its members from the latest configuration in its log,
/// as soon as the configuration is appended.
/// </remarks>
internal sealed class MembershipClusterFixture : Test, IAsyncDisposable
{
    internal const int VoterCount = 5;

    internal enum MessageAction
    {
        Deliver,
        Drop,
        Hold,
    }

    internal readonly ManualTimeProvider TimeProvider = new();
    internal readonly InProcessNetwork Network = new();
    internal readonly EndPoint[] Voters;
    internal readonly MembershipNode[] Nodes;

    internal MembershipClusterFixture(int voterCount = VoterCount, int joinerCount = 1)
    {
        Voters = Enumerable.Range(0, voterCount)
            .Select(EndPoint (i) => new DnsEndPoint($"member-{i}", 0))
            .ToArray();
        Nodes = Enumerable.Range(0, voterCount + joinerCount)
            .Select(i => new MembershipNode(Network, $"member-{i}", Voters, GetTempPath(), TimeProvider))
            .ToArray();
    }

    internal MembershipNode Joiner => Nodes[^1];

    /// <summary>
    /// Restarts the node with the same log location and configuration storage.
    /// </summary>
    internal async Task<MembershipNode> RestartAsync(int index)
    {
        var node = Nodes[index];
        await node.StopAsync(TestToken);
        await node.DisposeAsync();
        await node.Log.DisposeAsync();

        var replacement = new MembershipNode(Network, ((DnsEndPoint)node.EndPoint).Host, Voters, node.Location, TimeProvider, node.Storage);
        Nodes[index] = replacement;
        await replacement.StartAsync(TestToken);
        return replacement;
    }

    internal async Task StartAsync()
    {
        foreach (var source in Nodes)
        {
            foreach (var target in Nodes)
            {
                if (!object.ReferenceEquals(source, target))
                    Network.Hold(source.EndPoint, target.EndPoint);
            }
        }

        foreach (var node in Nodes)
            await node.StartAsync(TestToken);
    }

    /// <summary>
    /// Elects the candidate by delivering only its pre-votes and votes.
    /// </summary>
    /// <param name="candidate">The node to elect.</param>
    /// <param name="passWriteBarrier">
    /// <see langword="true"/> to replicate until the new leader's no-op is applied;
    /// <see langword="false"/> to leave the inherited tail uncommitted.
    /// </param>
    /// <param name="filter">
    /// Classifies the candidate's votes and write-barrier replication, for example to keep a partition.
    /// Defaults to delivering everything.
    /// </param>
    internal async Task ElectAsync(MembershipNode candidate, bool passWriteBarrier = true, Func<PendingMessage, MessageAction> filter = null)
    {
        filter ??= static _ => MessageAction.Deliver;
        var elected = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        candidate.LeaderChanged += OnLeaderChanged;
        try
        {
            candidate.StartElectionTimer();
            TimeProvider.Advance(TimeSpan.FromMilliseconds(100));
            await PumpAsync(candidate, elected.Task, message => message.MessageType is RaftMessageType.PreVote or RaftMessageType.Vote
                ? filter(message)
                : MessageAction.Hold, forceRounds: false);
        }
        finally
        {
            candidate.LeaderChanged -= OnLeaderChanged;
        }

        if (passWriteBarrier)
            await PumpAsync(candidate, candidate.WaitForLeadershipAsync(TestToken), filter);

        void OnLeaderChanged(RaftCluster<InProcessClusterMember> sender, InProcessClusterMember leader)
        {
            if (leader is not null && leader.Id == candidate.Id)
                elected.TrySetResult();
        }
    }

    /// <summary>
    /// Replicates until the node has applied the specified index.
    /// </summary>
    internal Task ReplicateUntilAppliedAsync(MembershipNode leader, long index, Func<PendingMessage, MessageAction> filter = null)
        => PumpAsync(leader, leader.Log.WaitForApplyAsync(index, TestToken).AsTask(), filter);

    /// <summary>
    /// Replicates the leader's log to one follower only; the rest of its outgoing RPCs are dropped.
    /// </summary>
    /// <remarks>
    /// The leader loses quorum on the first such round and steps down, which is the point: the entries
    /// reach exactly one follower and stay uncommitted. Requests that do not carry the entries, such as those
    /// of a round that started before the entries were appended, are delivered to everyone.
    /// </remarks>
    internal async Task ReplicateOnlyToAsync(MembershipNode leader, MembershipNode follower, long index)
    {
        var target = follower.Id;
        for (var attempt = 0; follower.Log.LastEntryIndex < index; attempt++)
        {
            True(attempt < 10, $"{follower.EndPoint} did not receive index {index}");

            // Others are held until the follower has the entries: dropping them first makes the leader
            // step down and cancel the in-flight append to the follower.
            await PumpAsync(
                leader,
                ForceRoundAsync(leader),
                message => (message.TargetId == target && message.MessageType is RaftMessageType.AppendEntries) switch
                {
                    true => MessageAction.Deliver,
                    false when follower.Log.LastEntryIndex >= index => MessageAction.Drop,
                    false when message.MessageType is RaftMessageType.AppendEntries && message.LastEntryIndex < index
                        => MessageAction.Deliver,
                    false => MessageAction.Hold,
                },
                forceRounds: false);
        }
    }

    /// <summary>
    /// Handles RPCs sent by <paramref name="source"/> until <paramref name="operation"/> completes.
    /// </summary>
    /// <remarks>
    /// Timers never fire here. When <paramref name="forceRounds"/> is set and the source is the leader,
    /// the pump keeps one forced replication round in flight, so a rejected append is retried in the
    /// next round rather than waiting for a heartbeat deadline. The last round is completed before
    /// returning, so no stale round leaks into the next step. Messages classified as
    /// <see cref="MessageAction.Hold"/> stay queued for a later step.
    /// </remarks>
    internal async Task PumpAsync(MembershipNode source, Task operation, Func<PendingMessage, MessageAction> filter = null,
        bool forceRounds = true)
    {
        filter ??= static _ => MessageAction.Deliver;
        var sourceId = source.Id;
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(TestToken);
        timeout.CancelAfter(DefaultTimeout); // deadlock guard, not a schedule
        var round = Task.CompletedTask;
        try
        {
            while (!operation.IsCompleted || !round.IsCompleted)
            {
                if (round.IsCompleted && !operation.IsCompleted && forceRounds)
                    round = ForceRoundAsync(source);

                using var wait = CancellationTokenSource.CreateLinkedTokenSource(timeout.Token);
                var next = Network.WaitForMessageAsync(
                    message => message.SourceId == sourceId && filter(message) is not MessageAction.Hold,
                    wait.Token);
                var wake = (operation.IsCompleted, round.IsCompleted) switch
                {
                    (true, _) => round,
                    (_, true) => operation,
                    _ => Task.WhenAny(operation, round),
                };
                if (!object.ReferenceEquals(await Task.WhenAny(next, wake), next))
                {
                    await wait.CancelAsync();
                    await ((Task)next).ConfigureAwait(ConfigureAwaitOptions.SuppressThrowing);
                    timeout.Token.ThrowIfCancellationRequested();
                    continue;
                }

                var message = await next;
                if (filter(message) is MessageAction.Deliver)
                    await Network.TryDeliverAsync(message);
                else
                    Network.TryDrop(message);
            }
        }
        catch (OperationCanceledException e) when (e.CancellationToken == timeout.Token && !TestToken.IsCancellationRequested)
        {
            throw new TimeoutException($"{source.EndPoint} did not complete the pumped operation.", e);
        }

        await operation;
    }

    /// <summary>
    /// Handles RPCs sent by any node until <paramref name="operation"/> completes.
    /// </summary>
    /// <remarks>
    /// Unlike <see cref="PumpAsync"/>, a delivery is not awaited before the next one: a leader answering
    /// a follower's read barrier waits for its own replication round, which needs further deliveries.
    /// When <paramref name="leader"/> is set, the pump keeps one forced replication round of that node in flight.
    /// Messages classified as <see cref="MessageAction.Hold"/> stay queued for a later step.
    /// </remarks>
    internal async Task PumpAllAsync(Task operation, Func<PendingMessage, MessageAction> filter = null, MembershipNode leader = null)
    {
        filter ??= static _ => MessageAction.Deliver;
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(TestToken);
        timeout.CancelAfter(DefaultTimeout); // deadlock guard, not a schedule
        var round = Task.CompletedTask;
        try
        {
            while (!operation.IsCompleted)
            {
                if (leader is not null && round.IsCompleted)
                    round = ForceRoundAsync(leader);

                using var wait = CancellationTokenSource.CreateLinkedTokenSource(timeout.Token);
                var next = Network.WaitForMessageAsync(message => filter(message) is not MessageAction.Hold, wait.Token);
                var wake = leader is null ? operation : Task.WhenAny(operation, round);
                if (!object.ReferenceEquals(await Task.WhenAny(next, wake), next))
                {
                    await wait.CancelAsync();
                    await ((Task)next).ConfigureAwait(ConfigureAwaitOptions.SuppressThrowing);
                    timeout.Token.ThrowIfCancellationRequested();
                    continue;
                }

                var message = await next;
                if (filter(message) is MessageAction.Deliver)
                    _ = Network.TryDeliverAsync(message);
                else
                    Network.TryDrop(message);
            }
        }
        catch (OperationCanceledException e) when (timeout.IsCancellationRequested && !TestToken.IsCancellationRequested)
        {
            throw new TimeoutException("The pumped operation did not complete.", e);
        }

        await operation;
    }

    private static async Task ForceRoundAsync(MembershipNode source)
    {
        try
        {
            await source.ForceReplicationAsync(TestToken);
        }
        catch (NotLeaderException)
        {
            // the operation under test observes leadership loss itself
        }
    }

    public async ValueTask DisposeAsync()
    {
        try
        {
            // A lifecycle regression must not hang the rest of the test process.
            await Task.WhenAll(Nodes.Select(static node => node.DisposeAsync().AsTask()))
                .WaitAsync(DefaultTimeout, TestToken);
        }
        finally
        {
            foreach (var node in Nodes)
                await node.Log.DisposeAsync();
        }
    }

    internal sealed class MembershipNode : InProcessCluster
    {
        // Identifies the WAL measurements of a node by its location.
        internal const string NodeMeasurementTag = "inprocess-node";

        private readonly InProcessNetwork network;

        [SetsRequiredMembers]
        internal MembershipNode(InProcessNetwork network, string name, EndPoint[] voters, string location, TimeProvider timeProvider,
            InMemoryClusterConfigurationStorage storage = null)
            : base(network, name, voters, CreateLog(storage ?? CreateStorage(voters), location), timeProvider, TimeSpan.FromMilliseconds(100), startFollower: false)
        {
            this.network = network;
            Location = location;
            Storage = (InMemoryClusterConfigurationStorage)Log.ConfigurationStorage;
            UseLogConfiguration(Storage, address => new InProcessClusterMember(this, network, address), static member => member.EndPoint);
        }

        private static InMemoryClusterConfigurationStorage CreateStorage(EndPoint[] voters)
        {
            var storage = new InMemoryClusterConfigurationStorage(EqualityComparer<EndPoint>.Default);
            var builder = storage.CreateInitialConfigurationBuilder();
            builder.UnionWith(voters);
            builder.Build();
            return storage;
        }

        private static WriteAheadLog CreateLog(InMemoryClusterConfigurationStorage storage, string location)
            => new(new()
            {
                Location = location,
                MemoryManagement = WriteAheadLog.MemoryManagementStrategy.PrivateMemory,
                FlushInterval = System.Threading.Timeout.InfiniteTimeSpan,
                MeasurementTags = new() { { NodeMeasurementTag, location } },
            }, IStateMachine.CreateNoOp())
            {
                ConfigurationStorage = storage,
            };

        internal string Location { get; }

        internal InMemoryClusterConfigurationStorage Storage { get; }

        internal WriteAheadLog Log => (WriteAheadLog)AuditTrail;

        internal bool IsMembershipLockHeld => Accessors<InProcessClusterMember>.MembershipLock(this).IsLockHeld;

        internal ValueTask<IClusterConfiguration<EndPoint>> LoadConfigurationAsync()
            => ((IClusterConfigurationStorage<EndPoint>)Storage).LoadConfigurationAsync(TestToken);

        internal async ValueTask<long> LoadConfigurationVersionAsync()
            => (await ((IClusterConfigurationStorage)Storage).LoadConfigurationAsync(TestToken)).Version;

        internal async Task<bool> AddAsync(EndPoint address, CancellationToken token)
        {
            using var member = new InProcessClusterMember(this, network, address);
            return await AddMemberAsync(member, rounds: 10, Storage, static member => member.EndPoint, token);
        }

        internal Task<bool> RemoveAsync(EndPoint address, CancellationToken token)
            => RemoveMemberAsync(ClusterMemberId.FromEndPoint(address), Storage, static member => member.EndPoint, token);

        /// <summary>
        /// Enters the production failure-detection callback on behalf of the current state.
        /// </summary>
        internal Task DetectAsync(EndPoint address)
            => ((IRaftStateMachine<InProcessClusterMember>)this).UnavailableMemberDetected(
                new CallerIdentity(Accessors<InProcessClusterMember>.State(this)),
                GetMember(address),
                AuditTrail.Term,
                ConsensusToken);

        /// <summary>
        /// Calls the protected automatic-removal helper with a caller-supplied term.
        /// </summary>
        internal ValueTask RemoveUnavailableAsync(EndPoint address, long term, CancellationToken token)
            => UnavailableMemberDetected(Storage, address, term, token);

        /// <summary>
        /// Appends a removal of <paramref name="address"/> to the local log without replicating or activating it.
        /// </summary>
        /// <remarks>
        /// Uses the internal storage-level append, which bypasses the membership API on purpose.
        /// </remarks>
        internal async Task<long> AppendRemovalAsync(EndPoint address)
        {
            var config = await LoadConfigurationAsync();
            True(IClusterConfiguration<EndPoint>.TryRemove(ref config, address));
            return await ClusterConfigurationExtensions.AppendAsync(Log, config, ((IPersistentState)Log).Term, TestToken);
        }

        // mirrors RaftCluster.DefaultImpl and RaftHttpCluster
        protected override ValueTask UnavailableMemberDetected(InProcessClusterMember member, long term, CancellationToken token)
            => UnavailableMemberDetected(Storage, member.EndPoint, term, token);
    }

    private static class Accessors<TMember>
        where TMember : class, IRaftClusterMember, IDisposable
    {
        [UnsafeAccessor(UnsafeAccessorKind.Field, Name = "state")]
        internal static extern ref RaftState<TMember> State(RaftCluster<TMember> cluster);

        [UnsafeAccessor(UnsafeAccessorKind.Field, Name = "membershipLock")]
        internal static extern ref AsyncExclusiveLock MembershipLock(RaftCluster<TMember> cluster);
    }

    private sealed class CallerIdentity(object state) : IRaftStateMachine.IWeakCallerStateIdentity
    {
        private readonly WeakReference<object> target = new(state);

        public bool IsValid([NotNullWhen(true)] object state)
            => target.TryGetTarget(out var expected) && object.ReferenceEquals(expected, state);

        public void Clear() => target.SetTarget(null);
    }
}
