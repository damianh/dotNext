namespace DotNext.Net.Cluster.Consensus.Raft;

/// <summary>
/// Represents a log that can reject an append atomically when the entry is not from the current term.
/// </summary>
internal interface ITermGuardedAuditTrail
{
    /// <summary>
    /// Appends the entry if its term is still the current term of the log.
    /// </summary>
    /// <remarks>
    /// The term is checked while holding the lock that serializes appends. The term of the log only grows,
    /// and it grows before any entry of the newer term is appended. Therefore, a successful append never
    /// places the entry after an entry with a higher term.
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
