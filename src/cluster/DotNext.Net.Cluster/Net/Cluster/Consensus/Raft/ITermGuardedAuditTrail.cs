namespace DotNext.Net.Cluster.Consensus.Raft;

/// <summary>
/// Represents a log that can reject a stale-term append while serializing the check with other appends.
/// </summary>
internal interface ITermGuardedAuditTrail
{
    /// <summary>
    /// Appends the entry if its term matches the log term observed under the append lock.
    /// </summary>
    /// <remarks>
    /// The check is atomic with other appends, not with term updates. A successful call can publish
    /// after the local term has advanced, but never after an entry with a higher term. That case is
    /// equivalent to append-then-step-down: the old leader state is stopped before this node votes or
    /// accepts newer-term entries, and an unreplicated tail is truncated by the next leader.
    /// </remarks>
    /// <typeparam name="TEntry">The type of the log entry.</typeparam>
    /// <param name="entry">The entry to append.</param>
    /// <param name="token">The token that can be used to cancel the operation.</param>
    /// <returns>The index of the appended entry.</returns>
    /// <exception cref="NotLeaderException">The term of <paramref name="entry"/> is not the current term; nothing is appended.</exception>
    ValueTask<long> AppendInCurrentTermAsync<TEntry>(TEntry entry, CancellationToken token)
        where TEntry : IRaftLogEntry;
}

internal static class TermGuardedAuditTrail
{
    /// <summary>
    /// Appends the entry only if its term is the current term of the log.
    /// </summary>
    /// <remarks>
    /// A custom <see cref="IPersistentState"/> gets a best-effort check before the append.
    /// </remarks>
    /// <exception cref="NotLeaderException">The term of <paramref name="entry"/> is not the current term; nothing is appended.</exception>
    internal static ValueTask<long> AppendInCurrentTermAsync<TEntry>(this IPersistentState state, TEntry entry, CancellationToken token)
        where TEntry : IRaftLogEntry
        => state switch
        {
            ITermGuardedAuditTrail guarded => guarded.AppendInCurrentTermAsync(entry, token),
            _ when entry.Term == state.Term => state.AppendAsync(entry, token),
            _ => ValueTask.FromException<long>(new NotLeaderException()),
        };
}
