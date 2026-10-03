using System.Diagnostics.CodeAnalysis;
using System.Net;
using System.Runtime.CompilerServices;
using Microsoft.Extensions.Logging;

namespace DotNext.Net.Cluster.Consensus.Raft;

using IO;
using Threading;

public sealed class UnavailableMemberDetectionTests : RaftTest
{
    [Fact(Timeout = TestTimeouts.Default)]
    public static async Task ObsoleteCallerStateReleasesMembershipLock()
    {
        await using var cluster = new TestCluster();
        var callerState = new CallerStateIdentity(valid: false);
        await DetectAsync(cluster, callerState, TestToken);
        Equal(1, callerState.ClearCount);
        AssertLockReleased(cluster);
    }

    [Fact(Timeout = TestTimeouts.Default)]
    public static async Task ThrowingDetectionCallbackReleasesMembershipLock()
    {
        await using var cluster = new TestCluster(static (_, _, _) => throw new InvalidOperationException());
        var callerState = new CallerStateIdentity(valid: true);
        await DetectAsync(cluster, callerState, TestToken);
        Equal(1, callerState.ClearCount);
        AssertLockReleased(cluster);
    }

    [Fact(Timeout = TestTimeouts.Default)]
    public static async Task CanceledAcquisitionDoesNotReleaseForeignLock()
    {
        await using var cluster = new TestCluster();
        True(Accessors<TestMember>.MembershipLock(cluster).TryAcquire());
        using var cancellation = new CancellationTokenSource();
        await cancellation.CancelAsync();
        var callerState = new CallerStateIdentity(valid: true);
        await DetectAsync(cluster, callerState, cancellation.Token);
        Equal(1, callerState.ClearCount);
        True(Accessors<TestMember>.MembershipLock(cluster).IsLockHeld);
        False(Accessors<TestMember>.MembershipLock(cluster).TryAcquire());
        Accessors<TestMember>.MembershipLock(cluster).Release();
    }

    [Fact(Timeout = TestTimeouts.Default)]
    public static async Task CancellationAfterAcquisitionReleasesMembershipLock()
    {
        using var cancellation = new CancellationTokenSource();
        var callbackStarted = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        await using var cluster = new TestCluster(async (_, _, token) =>
        {
            callbackStarted.SetResult();
            await Task.Delay(Timeout.Infinite, token).ConfigureAwait(false);
        });
        var callerState = new CallerStateIdentity(valid: true);
        var detection = DetectAsync(cluster, callerState, cancellation.Token);
        await callbackStarted.Task.WaitAsync(DefaultTimeout, TestToken);
        True(Accessors<TestMember>.MembershipLock(cluster).IsLockHeld);
        await cancellation.CancelAsync();
        await detection;
        Equal(1, callerState.ClearCount);
        AssertLockReleased(cluster);
    }

    [Fact(Timeout = TestTimeouts.Default)]
    public static async Task DetectionCompletesAfterClusterDisposal()
    {
        var releaseCallback = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var callbackStarted = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var cluster = new TestCluster(async (_, _, _) =>
        {
            callbackStarted.SetResult();
            await releaseCallback.Task.ConfigureAwait(false);
        });
        var callerState = new CallerStateIdentity(valid: true);
        var detection = DetectAsync(cluster, callerState, TestToken);
        await callbackStarted.Task.WaitAsync(DefaultTimeout, TestToken);
        cluster.Dispose();
        releaseCallback.SetResult();
        await detection;
        Equal(1, callerState.ClearCount);
    }

    [Fact(Timeout = TestTimeouts.Default)]
    public static async Task LeadershipLossIsLoggedAtDebug()
    {
        var logger = new CapturingLogger();
        await using var cluster = new TestCluster(logger, useRealDetector: true);
        await DetectAsync(cluster, new CallerStateIdentity(valid: true), TestToken);

        DoesNotContain(logger.Entries, static entry =>
            entry.EventName is "DotNext.Net.Cluster.FailedToProcessUnresponsiveMember");
        var entry = Single(logger.Entries, static entry =>
            entry.EventName is "DotNext.Net.Cluster.UnresponsiveMemberProcessingAbandoned");
        Equal(LogLevel.Debug, entry.Level);
        IsType<NotLeaderException>(entry.Exception);
    }

    [Fact(Timeout = TestTimeouts.Default)]
    public static async Task LeadershipTokenCancellationAfterAcquisitionIsLoggedAtDebug()
    {
        using var cancellation = new CancellationTokenSource();
        var callbackStarted = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var logger = new CapturingLogger();
        await using var cluster = new TestCluster(logger, async (_, _, token) =>
        {
            callbackStarted.SetResult();
            await Task.Delay(Timeout.Infinite, token).ConfigureAwait(false);
        });

        var detection = DetectAsync(cluster, new CallerStateIdentity(valid: true), cancellation.Token);
        await callbackStarted.Task.WaitAsync(DefaultTimeout, TestToken);
        await cancellation.CancelAsync();
        await detection;

        DoesNotContain(logger.Entries, static entry =>
            entry.EventName is "DotNext.Net.Cluster.FailedToProcessUnresponsiveMember");
        var entry = Single(logger.Entries, static entry =>
            entry.EventName is "DotNext.Net.Cluster.UnresponsiveMemberProcessingAbandoned");
        Equal(LogLevel.Debug, entry.Level);
        IsType<TaskCanceledException>(entry.Exception);
    }

    [Fact(Timeout = TestTimeouts.Default)]
    public static async Task GenuineDetectionFailureUsesFailureLog()
    {
        var logger = new CapturingLogger();
        await using var cluster = new TestCluster(logger, static (_, _, _) => throw new InvalidOperationException());
        await DetectAsync(cluster, new CallerStateIdentity(valid: true), TestToken);

        DoesNotContain(logger.Entries, static entry =>
            entry.EventName is "DotNext.Net.Cluster.UnresponsiveMemberProcessingAbandoned");
        var entry = Single(logger.Entries, static entry =>
            entry.EventName is "DotNext.Net.Cluster.FailedToProcessUnresponsiveMember");
        Equal(LogLevel.Warning, entry.Level);
        IsType<InvalidOperationException>(entry.Exception);
    }

    private static Task DetectAsync(TestCluster cluster, CallerStateIdentity callerState, CancellationToken token)
        => ((IRaftStateMachine<TestMember>)cluster)
            .UnavailableMemberDetected(callerState, TestMember.Instance, term: 42L, token)
            .WaitAsync(DefaultTimeout, TestToken);

    private static void AssertLockReleased(TestCluster cluster)
    {
        False(Accessors<TestMember>.MembershipLock(cluster).IsLockHeld);
        True(Accessors<TestMember>.MembershipLock(cluster).TryAcquire());
        Accessors<TestMember>.MembershipLock(cluster).Release();
    }

    private sealed class CallerStateIdentity(bool valid) : IRaftStateMachine.IWeakCallerStateIdentity
    {
        internal int ClearCount { get; private set; }
        public bool IsValid([NotNullWhen(true)] object state) => valid;
        public void Clear() => ClearCount++;
    }

    private sealed class TestCluster : RaftCluster<TestMember>
    {
        private readonly Func<TestMember, long, CancellationToken, ValueTask> detector;
        private readonly ILogger logger;

        [SetsRequiredMembers]
        internal TestCluster(Func<TestMember, long, CancellationToken, ValueTask> detector = null)
            : this(null, detector)
        {
        }

        [SetsRequiredMembers]
        internal TestCluster(CapturingLogger logger, Func<TestMember, long, CancellationToken, ValueTask> detector = null)
            : base(new Configuration())
        {
            AuditTrail = new ConsensusOnlyState();
            this.logger = logger;
            this.detector = detector;
        }

        [SetsRequiredMembers]
        internal TestCluster(CapturingLogger logger, bool useRealDetector)
            : this(logger)
        {
            if (useRealDetector)
            {
                detector = (member, term, token) =>
                    UnavailableMemberDetected<EndPoint>(null, member.EndPoint, term, token);
            }
        }

        protected override ILogger Logger => logger ?? base.Logger;

        protected override ValueTask UnavailableMemberDetected(TestMember member, long term, CancellationToken token)
            => detector?.Invoke(member, term, token) ?? base.UnavailableMemberDetected(member, term, token);

        protected override void Dispose(bool disposing)
        {
            if (disposing)
                (AuditTrail as IDisposable)?.Dispose();
            base.Dispose(disposing);
        }
    }

    private sealed class CapturingLogger : ILogger
    {
        internal readonly record struct Entry(LogLevel Level, string EventName, Exception Exception);

        private readonly List<Entry> entries = [];

        internal IReadOnlyList<Entry> Entries
        {
            get
            {
                lock (entries)
                    return [.. entries];
            }
        }

        IDisposable ILogger.BeginScope<TState>(TState state) => null;

        bool ILogger.IsEnabled(LogLevel logLevel) => true;

        void ILogger.Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception exception,
            Func<TState, Exception, string> formatter)
        {
            lock (entries)
                entries.Add(new(logLevel, eventId.Name, exception));
        }
    }

    private sealed class TestMember : IRaftClusterMember, IDisposable
    {
        internal static readonly TestMember Instance = new();
        private IRaftClusterMember.ReplicationState state;

        public EndPoint EndPoint { get; } = new DnsEndPoint("member", 0);
        public bool IsLeader => false;
        public bool IsRemote => true;
        public ClusterMemberStatus Status => ClusterMemberStatus.Available;
        ref IRaftClusterMember.ReplicationState IRaftClusterMember.State => ref state;
        public event Action<ClusterMemberStatusChangedEventArgs> MemberStatusChanged { add { } remove { } }
        public Task<Result<bool>> VoteAsync(long term, long lastLogIndex, long lastLogTerm, CancellationToken token)
            => throw new NotSupportedException();

        public Task<Result<ReplicationStatus>> AppendEntriesAsync<TEntry, TList>(long term, TList entries, long prevLogIndex, long prevLogTerm, long commitIndex, CancellationToken token)
            where TEntry : IRaftLogEntry
            where TList : IReadOnlyList<TEntry>
            => throw new NotSupportedException();

        public Task<Result<HeartbeatResult>> InstallSnapshotAsync(long term, IRaftLogEntry snapshot, long snapshotIndex, IDataTransferObject configuration, long configurationVersion, CancellationToken token)
            => throw new NotSupportedException();

        public Task<long?> SynchronizeAsync(long commitIndex, CancellationToken token) => throw new NotSupportedException();
        public ValueTask<IReadOnlyDictionary<string, string>> GetMetadataAsync(bool refresh, CancellationToken token) => throw new NotSupportedException();
        public Task<bool> ResignAsync(CancellationToken token) => throw new NotSupportedException();
        public ValueTask CancelPendingRequestsAsync() => ValueTask.CompletedTask;
        public void Dispose() { }
    }

    private sealed class Configuration : IClusterMemberConfiguration
    {
        public double HeartbeatThreshold => 0.5D;
        public ElectionTimeout ElectionTimeout => new() { LowerValue = 1000, UpperValue = 1000 };
        public bool Standby => false;
        public bool IsLeaderLeaseEnabled => false;
    }

    private static class Accessors<TMember>
        where TMember : class, IRaftClusterMember, IDisposable
    {
        [UnsafeAccessor(UnsafeAccessorKind.Field, Name = "membershipLock")]
        internal static extern ref AsyncExclusiveLock MembershipLock(RaftCluster<TMember> cluster);
    }
}
