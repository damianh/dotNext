using System.Collections.Specialized;
using System.Runtime.Caching;
using static System.Globalization.CultureInfo;

namespace DotNext.Net.Cluster.Consensus.Raft.Http;

/*
    Suppresses transport retries while their request IDs remain in this process-local, bounded cache.
    Expired or evicted IDs, and IDs received by a new detector instance, are accepted again.
 */
internal sealed class DuplicateRequestDetector : MemoryCache
{
    private new const string Name = "DotNextRaftDuplicationDetector";

    private readonly TimeSpan expiration;

    internal DuplicateRequestDetector(RequestJournalConfiguration config)
        : base(Name, CreateConfiguration(config.PollingInterval, config.MemoryLimit, config.Expiration), true)
        => expiration = config.Expiration;

    // Validates expiration before the base cache is created, so a rejected value leaves nothing to dispose.
    private static NameValueCollection CreateConfiguration(TimeSpan pollingTime, long memoryLimitMB, TimeSpan expiration)
    {
        ArgumentOutOfRangeException.ThrowIfLessThanOrEqual(expiration, TimeSpan.Zero, nameof(RequestJournalConfiguration.Expiration));

        return CreateConfiguration(pollingTime, memoryLimitMB);
    }

    private static NameValueCollection CreateConfiguration(TimeSpan pollingTime, long memoryLimitMB)
    {
        const string cacheMemoryLimitMegabytes = "CacheMemoryLimitMegabytes";
        const string pollingInterval = "PollingInterval";

        return new NameValueCollection
            {
                { cacheMemoryLimitMegabytes, memoryLimitMB.ToString(InvariantCulture) },
                { pollingInterval, pollingTime.ToString() },
            };
    }

    private readonly object valuePlaceholder = new();

    /*
        Logic of this method:
        If cache returns the same value for this message then it was not added previously; otherwise, it is different message but with the same id
     */
    internal bool IsDuplicated(HttpMessage message)
        => AddOrGetExisting(message.Id, valuePlaceholder, GetAbsoluteExpiration(DateTimeOffset.UtcNow, expiration)) is not null;

    // An expiration past DateTimeOffset.MaxValue never expires; MemoryLimit trimming still bounds the journal.
    private static DateTimeOffset GetAbsoluteExpiration(DateTimeOffset now, TimeSpan expiration)
        => expiration < DateTimeOffset.MaxValue - now ? now + expiration : InfiniteAbsoluteExpiration;
}