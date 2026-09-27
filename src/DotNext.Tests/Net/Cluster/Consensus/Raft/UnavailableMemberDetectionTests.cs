using System.Diagnostics.CodeAnalysis;
using System.Net;
using System.Runtime.CompilerServices;

namespace DotNext.Net.Cluster.Consensus.Raft;

using IO;
using Threading;

public sealed class UnavailableMemberDetectionTests : RaftTest
{
    [Fact]
    public static async Task ObsoleteCallerStateReleasesMembershipLock()
    {
        await using var cluster = new TestCluster();
        var callerState = new CallerStateIdentity(valid: false);
        await DetectAsync(cluster, callerState, TestToken);
        Equal(1, callerState.ClearCount);
        AssertLockReleased(cluster);
    }

    [Fact]
    public static async Task ThrowingDetectionCallbackReleasesMembershipLock()
    {
        await using var cluster = new TestCluster(static (_, _, _) => throw new InvalidOperationException());
        var callerState = new CallerStateIdentity(valid: true);
        await DetectAsync(cluster, callerState, TestToken);
        Equal(1, callerState.ClearCount);
        AssertLockReleased(cluster);
    }

    [Fact]
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

    [Fact]
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
        [SetsRequiredMembers]
        internal TestCluster(Func<TestMember, long, CancellationToken, ValueTask> detector = null)
            : base(new Configuration())
        {
            AuditTrail = new ConsensusOnlyState();
            this.detector = detector;
        }

        protected override ValueTask UnavailableMemberDetected(TestMember member, long term, CancellationToken token)
            => detector?.Invoke(member, term, token) ?? base.UnavailableMemberDetected(member, term, token);

        protected override void Dispose(bool disposing)
        {
            if (disposing)
                (AuditTrail as IDisposable)?.Dispose();
            base.Dispose(disposing);
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
