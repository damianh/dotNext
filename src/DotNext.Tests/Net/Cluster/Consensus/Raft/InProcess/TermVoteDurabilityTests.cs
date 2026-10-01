using System.Net;
using System.Runtime.CompilerServices;
using AsyncExclusiveLock = DotNext.Threading.AsyncExclusiveLock;

namespace DotNext.Net.Cluster.Consensus.Raft.InProcess;

using StateMachine;

/// <summary>
/// Guards the term/vote durability contract of <see cref="WriteAheadLog"/> in the process-crash model (#24).
/// A failed term/vote write is a transient storage error, and the "crash" is a dispose/reopen of the log.
/// </summary>
public sealed class TermVoteDurabilityTests : RaftTest
{
    [Fact(Timeout = TestTimeouts.Default)]
    public static async Task FailedVoteWriteGrantsNothing()
    {
        await using var fixture = await Fixture.CreateAsync(seedTerm: 1L);
        fixture.Fault.Break();

        await ThrowsAnyAsync<Exception>(() => fixture.VoteAsync(fixture.First, 1L));
        await fixture.RestartTargetAsync();

        Equal(1L, fixture.State.Term);
        NoDurableVote(fixture);
    }

    [Fact(Timeout = TestTimeouts.Default)]
    public static async Task CancelledVoteRequestGrantsNothing()
    {
        await using var fixture = await Fixture.CreateAsync(seedTerm: 1L);
        using var source = CancellationTokenSource.CreateLinkedTokenSource(TestToken);

        // the vote cannot complete while the state file is locked, so cancellation always wins
        await fixture.StateLock.AcquireAsync(TestToken);
        Task<Result<bool>> vote;
        try
        {
            vote = fixture.VoteAsync(fixture.First, 1L, source.Token);
            await source.CancelAsync();
            await ThrowsAnyAsync<OperationCanceledException>(() => vote);
        }
        finally
        {
            fixture.StateLock.Release();
        }

        Equal(1L, fixture.State.Term);
        NoDurableVote(fixture);
        await fixture.RestartTargetAsync();
        Equal(1L, fixture.State.Term);
        NoDurableVote(fixture);
    }

    [Fact(Timeout = TestTimeouts.Default)]
    public static async Task FailedVoteWriteNeverYieldsTwoGrantsInOneTerm()
    {
        await using var fixture = await Fixture.CreateAsync(seedTerm: 1L);
        var grants = new HashSet<string>();
        fixture.Fault.Break();

        // A request that fails with an error was never told that it was granted.
        await fixture.TryVoteAsync(fixture.First, grants);
        await fixture.TryVoteAsync(fixture.Second, grants);
        await fixture.TryVoteAsync(fixture.First, grants);
        Empty(grants);

        // Whatever the node decides before the restart (it may still refuse the candidate that was tried first),
        // it must not grant a second candidate in the same term after it.
        fixture.Fault.Restore();
        await fixture.TryVoteAsync(fixture.Second, grants);
        await fixture.TryVoteAsync(fixture.First, grants);
        await fixture.RestartTargetAsync();
        Equal(1L, fixture.State.Term);
        await fixture.TryVoteAsync(fixture.First, grants);
        await fixture.TryVoteAsync(fixture.Second, grants);

        Single(grants);
    }
    [Fact(Timeout = TestTimeouts.Default)]
    public static async Task FailedWriteThatReachedTheDiskIsNotOverwrittenByLowerTerm()
    {
        await using var fixture = await Fixture.CreateAsync(seedTerm: 1L);
        await fixture.FailWriteAfterItReachedTheDiskAsync(term: 2L);

        // The ambiguous failure was reconciled: the published term is the durable one, so a stale term-1
        // leader is not acknowledged, and a later write cannot lower the durable term.
        Equal(2L, fixture.State.Term);
        var stale = await fixture.AppendAsync(fixture.First, term: 1L, entryTerm: 1L);
        NotEqual(HeartbeatResult.ReplicatedWithLeaderTerm, stale.Value.Result);
        Equal(0L, fixture.Log.LastEntryIndex);

        await fixture.State.UpdateVotedForAsync(fixture.First.Id, TestToken);
        await fixture.RestartTargetAsync();
        Equal(2L, fixture.State.Term);
        True(fixture.State.IsVotedFor(fixture.First.Id));
        False(fixture.State.IsVotedFor(fixture.Second.Id));
    }
    [Fact(Timeout = TestTimeouts.Default)]
    public static async Task RecoveredVoteWriteKeepsTheGrantAcrossRestart()
    {
        await using var fixture = await Fixture.CreateAsync(seedTerm: 1L);
        fixture.Fault.Break();
        await ThrowsAnyAsync<Exception>(() => fixture.VoteAsync(fixture.First, 1L));

        // the retry writes the whole record, so the grant is reported only after it is durable
        fixture.Fault.Restore();
        True((await fixture.VoteAsync(fixture.First, 1L)).Value);
        False((await fixture.VoteAsync(fixture.Second, 1L)).Value);

        await fixture.RestartTargetAsync();
        Equal(1L, fixture.State.Term);
        True(fixture.State.IsVotedFor(fixture.First.Id));
        False(fixture.State.IsVotedFor(fixture.Second.Id));
        False((await fixture.VoteAsync(fixture.Second, 1L)).Value);
        True((await fixture.VoteAsync(fixture.First, 1L)).Value);
    }

    [Theory(Timeout = TestTimeouts.Default)]
    [InlineData(Trigger.AppendEntries)]
    [InlineData(Trigger.Vote)]
    public static async Task AcknowledgedTermSurvivesRestart(Trigger trigger)
    {
        await using var fixture = await Fixture.CreateAsync(seedTerm: 1L);
        await AcknowledgeTermTwoAsync(fixture, trigger);

        await fixture.RestartTargetAsync();

        // The node acknowledged an entry of term 2, so it must not come back in term 1.
        Equal(2L, fixture.State.Term);
    }

    [Theory(Timeout = TestTimeouts.Default)]
    [InlineData(Trigger.AppendEntries)]
    [InlineData(Trigger.Vote)]
    public static async Task StaleLeaderCannotOverwriteAcknowledgedEntry(Trigger trigger)
    {
        await using var fixture = await Fixture.CreateAsync(seedTerm: 1L);
        await AcknowledgeTermTwoAsync(fixture, trigger);

        await fixture.RestartTargetAsync();
        var stale = await fixture.AppendAsync(fixture.First, term: 1L, entryTerm: 1L);

        False(stale.Value.Result is HeartbeatResult.Replicated or HeartbeatResult.ReplicatedWithLeaderTerm);
        Equal(1L, fixture.Log.LastEntryIndex);
        Equal(2L, await fixture.Log.GetTermAsync(1L, TestToken));
    }

    [Theory(Timeout = TestTimeouts.Default)]
    [InlineData(Trigger.AppendEntries)]
    [InlineData(Trigger.Vote)]
    public static async Task AcknowledgedTermSurvivesRestartWhenReadBackFails(Trigger trigger)
    {
        await using var fixture = await Fixture.CreateAsync(seedTerm: 1L);

        // The read-back after the failed write cannot correct the published term, so a term published
        // before its write is durable stays published, and the next term-2 request would skip the write.
        await AcknowledgeTermTwoAsync(fixture, trigger, readBackFails: true);

        await fixture.RestartTargetAsync();
        Equal(2L, fixture.State.Term);
    }

    [Fact(Timeout = TestTimeouts.Default)]
    public static async Task ControlWithoutFaultKeepsTermAndEntry()
    {
        await using var fixture = await Fixture.CreateAsync(seedTerm: 1L);
        Equal(HeartbeatResult.ReplicatedWithLeaderTerm, (await fixture.AppendAsync(fixture.Second, term: 2L, entryTerm: 2L)).Value.Result);

        await fixture.RestartTargetAsync();
        var stale = await fixture.AppendAsync(fixture.First, term: 1L, entryTerm: 1L);

        Equal(2L, fixture.State.Term);
        False(stale.Value.Result is HeartbeatResult.Replicated or HeartbeatResult.ReplicatedWithLeaderTerm);
        Equal(2L, await fixture.Log.GetTermAsync(1L, TestToken));
    }

    public enum Trigger
    {
        AppendEntries,
        Vote,
    }

    private static async Task AcknowledgeTermTwoAsync(Fixture fixture, Trigger trigger, bool readBackFails = false)
    {
        // The state file is unwritable exactly when the node learns about term 2.
        if (readBackFails)
            fixture.Fault.BreakReadBack();
        else
            fixture.Fault.Break();

        if (trigger is Trigger.Vote)
            await ThrowsAnyAsync<Exception>(() => fixture.VoteAsync(fixture.Second, 2L));
        else
            await ThrowsAnyAsync<Exception>(() => fixture.AppendAsync(fixture.Second, term: 2L, entryTerm: 2L));

        // The storage error clears and the term-2 leader keeps replicating to the same node.
        fixture.Fault.Restore();
        var acknowledgment = await fixture.AppendAsync(fixture.Second, term: 2L, entryTerm: 2L);
        Equal(HeartbeatResult.ReplicatedWithLeaderTerm, acknowledgment.Value.Result);
        Equal(1L, fixture.Log.LastEntryIndex);
        Equal(2L, await fixture.Log.GetTermAsync(1L, TestToken));
    }

    // IsVotedFor is true for every candidate only while no vote is recorded.
    private static void NoDurableVote(Fixture fixture)
    {
        True(fixture.State.IsVotedFor(fixture.First.Id));
        True(fixture.State.IsVotedFor(fixture.Second.Id));
    }

    private sealed class Fixture : IAsyncDisposable
    {
        private readonly InProcessNetwork network = new();
        private readonly ManualTimeProvider clock = new();
        private readonly string location = GetTempPath();
        private readonly ConsensusOnlyState firstState = new();
        private readonly ConsensusOnlyState secondState = new();
        private readonly List<InProcessCluster> retired = [];
        private WriteAheadLog log;
        private InProcessCluster target;

        private Fixture()
        {
        }

        internal InProcessCluster First { get; private set; }

        internal InProcessCluster Second { get; private set; }

        internal NodeStateFault Fault { get; private set; }

        internal WriteAheadLog Log => log;

        internal IPersistentState State => log;

        internal AsyncExclusiveLock StateLock => StateLockOf(log);

        internal static async Task<Fixture> CreateAsync(long seedTerm)
        {
            var fixture = new Fixture();
            fixture.Open();
            await ((IPersistentState)fixture.log).UpdateTermAsync(seedTerm, resetLastVote: true, TestToken);

            EndPoint[] membership = [new DnsEndPoint("first", 0), new DnsEndPoint("second", 0), new DnsEndPoint("target", 0)];
            fixture.First = fixture.CreateNode(membership, 0, fixture.firstState);
            fixture.Second = fixture.CreateNode(membership, 1, fixture.secondState);
            fixture.target = fixture.CreateNode(membership, 2, fixture.log, startFollower: true);
            await fixture.First.StartAsync(TestToken);
            await fixture.Second.StartAsync(TestToken);
            await fixture.target.StartAsync(TestToken);
            return fixture;
        }

        /// <summary>
        /// The simulated crash: the target WAL is disposed without any shutdown flush and reopened from disk.
        /// </summary>
        internal async Task RestartTargetAsync()
        {
            var previous = target;
            target = await target.RestartAsync(_ =>
            {
                Fault.Dispose();
                log.Dispose();
                Open();
                return log;
            }, TestToken);
            retired.Add(previous);
        }

        /// <summary>
        /// Stores a record with no vote, as if a write reported as failed had reached the disk.
        /// </summary>
        internal void WriteRecordBehindTheLog(long term)
        {
            var record = new byte[1 + ClusterMemberId.Size + sizeof(long)];
            System.Buffers.Binary.BinaryPrimitives.WriteInt64LittleEndian(record.AsSpan(1 + ClusterMemberId.Size), term);
            Fault.WriteRecord(record);
        }
        /// <summary>
        /// A term update whose write fails while the record, as far as the disk is concerned, was already stored.
        /// The update waits on the state lock, the record is stored meanwhile, then the write fails.
        /// </summary>
        internal async Task FailWriteAfterItReachedTheDiskAsync(long term)
        {
            Fault.Break();
            Task update;
            await StateLock.AcquireAsync(TestToken);
            try
            {
                update = State.UpdateTermAsync(term, resetLastVote: true, TestToken).AsTask();
                WriteRecordBehindTheLog(term);
            }
            finally
            {
                StateLock.Release();
            }

            await ThrowsAnyAsync<Exception>(() => update);
            Fault.Restore();
        }
        internal Task<Result<bool>> VoteAsync(InProcessCluster candidate, long term, CancellationToken token = default)
            => candidate.GetMember(target.EndPoint).As<IRaftClusterMember>()
                .VoteAsync(term, 0L, 0L, token.CanBeCanceled ? token : TestToken);

        internal async Task TryVoteAsync(InProcessCluster candidate, HashSet<string> grants)
        {
            try
            {
                if ((await VoteAsync(candidate, 1L)).Value)
                    grants.Add(candidate.EndPoint.ToString());
            }
            catch (Exception e) when (e is not OperationCanceledException)
            {
                // the vote was not granted
            }
        }

        internal Task<Result<ReplicationStatus>> AppendAsync(InProcessCluster leader, long term, long entryTerm)
            => leader.GetMember(target.EndPoint).As<IRaftClusterMember>()
                .AppendEntriesAsync<EmptyLogEntry, EmptyLogEntry[]>(term, [new() { Term = entryTerm }], 0L, 0L, 0L, TestToken);

        private void Open()
        {
            log = new(new()
            {
                Location = location,
                MemoryManagement = WriteAheadLog.MemoryManagementStrategy.PrivateMemory,
                FlushInterval = Timeout.InfiniteTimeSpan,
            }, IStateMachine.CreateNoOp());
            Fault = new(log, location);
        }

        private InProcessCluster CreateNode(EndPoint[] membership, int index, IPersistentState state, bool startFollower = false)
            => new(network, ((DnsEndPoint)membership[index]).Host, membership, state, clock, TimeSpan.FromMilliseconds(100), startFollower);

        public async ValueTask DisposeAsync()
        {
            await First.DisposeAsync();
            await Second.DisposeAsync();
            await target.DisposeAsync();
            foreach (var node in retired)
                await node.DisposeAsync();

            Fault.Dispose();
            await log.DisposeAsync();
            firstState.Dispose();
            secondState.Dispose();
        }

        [UnsafeAccessor(UnsafeAccessorKind.Field, Name = "stateLock")]
        private static extern ref AsyncExclusiveLock StateLockOf(WriteAheadLog log);
    }
}
