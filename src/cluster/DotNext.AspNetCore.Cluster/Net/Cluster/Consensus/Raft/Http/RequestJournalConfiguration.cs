namespace DotNext.Net.Cluster.Consensus.Raft.Http;

/// <summary>
/// Represents configuration of the in-memory request journal used to suppress repeated delivery
/// of one-way messages.
/// </summary>
/// <remarks>
/// Each cluster member keeps its own bounded, expiring journal of request identifiers. A repeated
/// one-way message is dropped only while its identifier remains in the journal of the member that
/// receives it. A message can be delivered again after its entry expires or is evicted, after the
/// member restarts, or when a retry reaches a different member.
/// </remarks>
public sealed class RequestJournalConfiguration
{
    /// <summary>
    /// Gets or sets the memory limit, in megabytes, for the request journal.
    /// </summary>
    /// <remarks>
    /// The value must be between <c>0</c> and <see cref="int.MaxValue"/>, inclusive. A value of
    /// <c>0</c> selects the cache's automatic memory limit. Other values are rejected with
    /// <see cref="ArgumentException"/> when the journal is created. Reaching the limit can evict
    /// identifiers and allow a later delivery of the same request.
    /// </remarks>
    public long MemoryLimit { get; set; } = 10L;

    /// <summary>
    /// Gets or sets how often the request journal checks its memory usage.
    /// </summary>
    /// <remarks>
    /// The value must be greater than <see cref="TimeSpan.Zero"/>. A nonpositive value is rejected
    /// with <see cref="ArgumentException"/> when the journal is created.
    /// </remarks>
    public TimeSpan PollingInterval { get; set; } = TimeSpan.FromMinutes(1);

    /// <summary>
    /// Gets or sets how long a request identifier remains eligible for duplicate suppression.
    /// </summary>
    /// <remarks>
    /// After this interval, the same request identifier is accepted as a new delivery. The value
    /// must be positive and small enough to produce a valid future <see cref="DateTimeOffset"/>.
    /// </remarks>
    public TimeSpan Expiration { get; set; } = TimeSpan.FromSeconds(10);
}
