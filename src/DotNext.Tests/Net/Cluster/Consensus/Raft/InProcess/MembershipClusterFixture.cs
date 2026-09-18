using System.Collections.Concurrent;
using System.Diagnostics.CodeAnalysis;
using System.Net;
using System.Runtime.CompilerServices;
using System.Threading.Channels;
using Microsoft.Extensions.Logging;

namespace DotNext.Net.Cluster.Consensus.Raft.InProcess;

using Diagnostics;
using Membership;
using StateMachine;
using Threading;

internal sealed class MembershipClusterFixture : Test, IAsyncDisposable
{
    internal readonly ManualTimeProvider TimeProvider = new();
    internal readonly InProcessNetwork Network = new();
    internal readonly MembershipNode[] Nodes;

    internal MembershipClusterFixture(int memberCount = 5)
    {
        EndPoint[] membership = Enumerable.Range(0, memberCount)
            .Select(i => new DnsEndPoint($"member-{i}", 0)).ToArray();
        Nodes = Enumerable.Range(0, memberCount + 1).Select(i =>
        {
            var storage = new InMemoryClusterConfigurationStorage(EqualityComparer<EndPoint>.Default);
            var builder = storage.CreateInitialConfigurationBuilder();
            builder.UnionWith(membership);
            builder.Build();
            var log = new WriteAheadLog(new() { Location = GetTempPath() }, IStateMachine.CreateNoOp())
            {
                ConfigurationStorage = storage,
            };
            return new MembershipNode(Network, $"member-{i}", membership, log, storage, TimeProvider);
        }).ToArray();
    }

    internal MembershipNode Leader => Nodes[0];

    internal async Task StartAsync()
    {
        foreach (var node in Nodes)
            await node.StartAsync(TestToken);
        await ElectAsync(Leader);
    }

    internal async Task ElectAsync(MembershipNode node)
    {
        node.StartElectionTimer();
        TimeProvider.Advance(TimeSpan.FromMilliseconds(100));
        Equal(node.Id, (await node.WaitForLeaderAsync(DefaultTimeout, TestToken)).Id);
        await node.ForceReplicationAsync(TestToken);
        await node.WaitForLeadershipAsync(TestToken).WaitAsync(DefaultTimeout, TestToken);
    }

    internal async Task RemoveAsync(MembershipNode member)
    {
        True(await Leader.RemoveAsync(member.EndPoint, TestToken).WaitAsync(DefaultTimeout, TestToken));
        await Leader.WaitForMembersAsync(members => !members.Contains(member.EndPoint));
    }

    internal void Hold(MembershipNode source)
    {
        foreach (var node in Nodes.Where(node => !object.ReferenceEquals(source, node)))
            Network.Hold(source.EndPoint, node.EndPoint);
    }

    internal async Task PumpAsync(MembershipNode source, Task operation)
    {
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(TestToken);
        timeout.CancelAfter(DefaultTimeout);
        try
        {
            while (!operation.IsCompleted)
            {
                var next = Network.WaitForMessageAsync(source.EndPoint, timeout.Token);
                if (await Task.WhenAny(operation, next) == operation)
                    break;
                var message = await next;
                if (!message.IsCompleted)
                    await Network.DeliverAsync(message);
            }
            await operation;
        }
        finally
        {
            await timeout.CancelAsync();
        }
    }

    public async ValueTask DisposeAsync()
    {
        try
        {
            await Task.WhenAll(Nodes.Select(node => node.DisposeAsync().AsTask()))
                .WaitAsync(DefaultTimeout, TestToken);
        }
        finally
        {
            foreach (var node in Nodes)
            {
                await node.Log.DisposeAsync();
                node.Storage.Dispose();
            }
        }
    }

    internal sealed class MembershipNode : InProcessCluster
    {
        private readonly InProcessNetwork network;
        private readonly Channel<IClusterConfiguration<EndPoint>> configurations =
            Channel.CreateUnbounded<IClusterConfiguration<EndPoint>>();
        private readonly System.Threading.Lock appliedLock = new();
        private TaskCompletionSource applied = new(TaskCreationOptions.RunContinuationsAsynchronously);
        private Task polling = Task.CompletedTask;

        internal readonly InMemoryClusterConfigurationStorage Storage;
        internal readonly ConcurrentDictionary<EndPoint, Detector> Detectors = new();
        internal readonly ErrorLogger Errors = new();
        internal Func<InProcessClusterMember, long, CancellationToken, ValueTask> OnUnavailable;

        [SetsRequiredMembers]
        internal MembershipNode(InProcessNetwork network, string name, EndPoint[] membership,
            WriteAheadLog log, InMemoryClusterConfigurationStorage storage, TimeProvider timeProvider)
            : base(network, name, membership, log, timeProvider, TimeSpan.FromMilliseconds(100), startFollower: false)
        {
            this.network = network;
            Storage = storage;
            FailureDetectorFactory = (_, member) => Detectors.GetOrAdd(member.EndPoint, static _ => new());
        }

        internal WriteAheadLog Log => (WriteAheadLog)AuditTrail;
        protected override ILogger Logger => Errors;

        public override async Task StartAsync(CancellationToken token = default)
        {
            Storage.ConfigurationChanged += configurations.Writer.WriteAsync;
            polling = PollAsync();
            await base.StartAsync(token);
        }

        public override async Task StopAsync(CancellationToken token = default)
        {
            Storage.ConfigurationChanged -= configurations.Writer.WriteAsync;
            configurations.Writer.TryComplete();
            await polling;
            await base.StopAsync(token);
        }

        private async Task PollAsync()
        {
            await foreach (var config in configurations.Reader.ReadAllAsync())
            {
                await using (var scope = await ChangeConfigurationAsync(CancellationToken.None))
                {
                    foreach (var member in scope.Members.Values)
                        if (!config.Members.Contains(member.EndPoint))
                            scope.MarkAsRemoved(member);
                    foreach (var address in config.Members)
                        if (!scope.Members.Values.Any(member => Equals(member.EndPoint, address)))
                            scope.MarkAsAdded(new(this, network, address));
                }

                lock (appliedLock)
                {
                    applied.TrySetResult();
                    applied = new(TaskCreationOptions.RunContinuationsAsynchronously);
                }
            }
        }

        internal async Task WaitForMembersAsync(Func<HashSet<EndPoint>, bool> predicate)
        {
            for (;;)
            {
                Task changed;
                lock (appliedLock)
                {
                    if (predicate(Members.Select(member => member.EndPoint).ToHashSet()))
                        return;
                    changed = applied.Task;
                }
                await changed.WaitAsync(DefaultTimeout, TestToken);
            }
        }

        internal async Task<bool> AddAsync(EndPoint address, CancellationToken token)
        {
            using var member = new InProcessClusterMember(this, network, address);
            return await AddMemberAsync(member, 10, Storage, static member => member.EndPoint, token);
        }

        internal Task<bool> RemoveAsync(EndPoint address, CancellationToken token)
            => RemoveMemberAsync(ClusterMemberId.FromEndPoint(address), Storage, static member => member.EndPoint, token);

        internal ValueTask<bool> ResignAsync() => base.ResignAsync(TestToken);

        internal ValueTask RemoveUnavailableAsync(InProcessClusterMember member, long term, CancellationToken token)
            => UnavailableMemberDetected(Storage, member.EndPoint, term, token);

        protected override ValueTask UnavailableMemberDetected(InProcessClusterMember member, long term, CancellationToken token)
            => OnUnavailable is { } callback ? callback(member, term, token) : RemoveUnavailableAsync(member, term, token);

        internal CallerIdentity CaptureCaller() => new(Accessors<InProcessClusterMember>.CurrentState(this));

        internal Task DetectAsync(CallerIdentity caller, EndPoint member, CancellationToken token)
            => ((IRaftStateMachine<InProcessClusterMember>)this).UnavailableMemberDetected(
                caller, GetMember(member), AuditTrail.Term, token);

        internal bool IsMembershipLockHeld => Accessors<InProcessClusterMember>.MembershipLock(this).IsLockHeld;
    }

    private static class Accessors<TMember> where TMember : class, IRaftClusterMember, IDisposable
    {
        [UnsafeAccessor(UnsafeAccessorKind.Field, Name = "state")]
        internal static extern ref RaftState<TMember> CurrentState(RaftCluster<TMember> cluster);

        [UnsafeAccessor(UnsafeAccessorKind.Field, Name = "membershipLock")]
        internal static extern ref AsyncExclusiveLock MembershipLock(RaftCluster<TMember> cluster);
    }

    internal sealed class CallerIdentity(object state) : IRaftStateMachine.IWeakCallerStateIdentity
    {
        private readonly WeakReference<object> target = new(state);
        internal int Validations { get; private set; }
        internal bool Cleared { get; private set; }

        public bool IsValid([NotNullWhen(true)] object state)
        {
            Validations++;
            return target.TryGetTarget(out var expected) && ReferenceEquals(expected, state);
        }

        public void Clear()
        {
            Cleared = true;
            target.SetTarget(null);
        }
    }

    internal sealed class Detector : IFailureDetector
    {
        private volatile bool healthy = true;
        public bool IsHealthy => healthy;
        public bool IsMonitoring => true;
        public void ReportHeartbeat() { }
        public void Reset() => healthy = true;
        internal void Fail() => healthy = false;
    }

    internal sealed class ErrorLogger : ILogger
    {
        internal readonly ConcurrentQueue<Exception> Exceptions = new();
        public IDisposable BeginScope<TState>(TState state) where TState : notnull => null;
        public bool IsEnabled(LogLevel logLevel) => logLevel >= LogLevel.Warning;
        public void Log<TState>(LogLevel level, EventId id, TState state, Exception exception, Func<TState, Exception, string> formatter)
        {
            if (exception is not null)
                Exceptions.Enqueue(exception);
        }
    }
}
