using System.Diagnostics.CodeAnalysis;

namespace DotNext.Net.Cluster.Consensus.Raft;

using IO.Log;
using Membership;

/// <summary>
/// A custom <see cref="IPersistentState"/> that does not implement the internal term guard.
/// </summary>
[ExcludeFromCodeCoverage]
internal sealed class UnguardedPersistentState : IPersistentState, IDisposable
{
    private readonly IPersistentState inner = new ConsensusOnlyState();

    public bool IsVotedFor(in ClusterMemberId id) => inner.IsVotedFor(in id);

    public long Term => inner.Term;

    public ValueTask<long> IncrementTermAsync(ClusterMemberId member, CancellationToken token = default)
        => inner.IncrementTermAsync(member, token);

    public ValueTask UpdateTermAsync(long term, bool resetLastVote, CancellationToken token = default)
        => inner.UpdateTermAsync(term, resetLastVote, token);

    public ValueTask UpdateVotedForAsync(ClusterMemberId member, CancellationToken token = default)
        => inner.UpdateVotedForAsync(member, token);

    public IClusterConfigurationStorage ConfigurationStorage
    {
        get => inner.ConfigurationStorage;
        set => inner.ConfigurationStorage = value;
    }

    public int Version => inner.Version;

    public bool IsLogEntryLengthAlwaysPresented => inner.IsLogEntryLengthAlwaysPresented;

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

    public void Dispose() => (inner as IDisposable)?.Dispose();
}
