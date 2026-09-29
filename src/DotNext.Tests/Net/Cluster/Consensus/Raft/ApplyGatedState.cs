using System.Diagnostics.CodeAnalysis;

namespace DotNext.Net.Cluster.Consensus.Raft;

using IO.Log;
using Membership;
using StateMachine;

/// <summary>
/// Wraps a <see cref="WriteAheadLog"/> and holds one <c>WaitForApplyAsync(index)</c> call before it reaches the log,
/// which models a proposer that is descheduled between its append and its wait for the apply.
/// </summary>
/// <remarks>
/// The append still goes through the log's own term guard.
/// </remarks>
[ExcludeFromCodeCoverage]
internal sealed class ApplyGatedState(WriteAheadLog inner) : IPersistentState, ITermGuardedAuditTrail
{
    private readonly TaskCompletionSource entered = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private readonly TaskCompletionSource release = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private long gatedIndex = -1L;

    /// <summary>
    /// Holds the next wait for the specified index until <see cref="Release"/> is called.
    /// </summary>
    public void Hold(long index) => Volatile.Write(ref gatedIndex, index);

    /// <summary>
    /// Gets the task that completes when a wait for the held index has been intercepted.
    /// </summary>
    public Task Entered => entered.Task;

    public void Release() => release.TrySetResult();

    ValueTask<long> ITermGuardedAuditTrail.AppendInCurrentTermAsync<TEntry>(TEntry entry, CancellationToken token)
        => ((ITermGuardedAuditTrail)inner).AppendInCurrentTermAsync(entry, token);

    async ValueTask IAuditTrail.WaitForApplyAsync(long index, CancellationToken token)
    {
        if (Volatile.Read(ref gatedIndex) == index)
        {
            entered.TrySetResult();
            await release.Task.ConfigureAwait(false);
        }

        await inner.WaitForApplyAsync(index, token).ConfigureAwait(false);
    }

    public bool IsVotedFor(in ClusterMemberId id) => ((IPersistentState)inner).IsVotedFor(in id);

    public long Term => ((IPersistentState)inner).Term;

    public ValueTask<long> IncrementTermAsync(ClusterMemberId member, CancellationToken token = default)
        => ((IPersistentState)inner).IncrementTermAsync(member, token);

    public ValueTask UpdateTermAsync(long term, bool resetLastVote, CancellationToken token = default)
        => ((IPersistentState)inner).UpdateTermAsync(term, resetLastVote, token);

    public ValueTask UpdateVotedForAsync(ClusterMemberId member, CancellationToken token = default)
        => ((IPersistentState)inner).UpdateVotedForAsync(member, token);

    public IClusterConfigurationStorage ConfigurationStorage
    {
        get => inner.ConfigurationStorage;
        set => inner.ConfigurationStorage = value;
    }

    public int Version => ((IPersistentState)inner).Version;

    public bool IsLogEntryLengthAlwaysPresented => ((IPersistentState)inner).IsLogEntryLengthAlwaysPresented;

    public long LastCommittedEntryIndex => inner.LastCommittedEntryIndex;

    public long LastEntryIndex => inner.LastEntryIndex;

    public ValueTask WaitForApplyAsync(CancellationToken token = default)
        => inner.WaitForApplyAsync(token);

    public ValueTask<long> CommitAsync(long endIndex, CancellationToken token = default)
        => inner.CommitAsync(endIndex, token);

    public Task InitializeAsync(CancellationToken token = default)
        => inner.InitializeAsync(token);

    public ValueTask<TResult> ReadAsync<TResult>(
        ILogEntryConsumer<IRaftLogEntry, TResult> reader,
        long startIndex,
        long endIndex,
        CancellationToken token = default)
        => inner.ReadAsync(reader, startIndex, endIndex, token);

    public ValueTask AppendAsync<TEntry>(
        ILogEntryProducer<TEntry> entries,
        long startIndex,
        bool skipCommitted = false,
        CancellationToken token = default)
        where TEntry : IRaftLogEntry
        => inner.AppendAsync(entries, startIndex, skipCommitted, token);

    public ValueTask AppendAsync<TEntry>(
        TEntry entry,
        long startIndex,
        CancellationToken token = default)
        where TEntry : IRaftLogEntry
        => inner.AppendAsync(entry, startIndex, token);

    public ValueTask<long> AppendAsync<TEntry>(TEntry entry, CancellationToken token = default)
        where TEntry : IRaftLogEntry
        => inner.AppendAsync(entry, token);
}
