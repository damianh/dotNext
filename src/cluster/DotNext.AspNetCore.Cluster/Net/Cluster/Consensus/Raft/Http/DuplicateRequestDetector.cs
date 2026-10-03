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
        : base(Name, CreateConfiguration(config.PollingInterval, config.MemoryLimit), true)
        => expiration = config.Expiration;

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
        => AddOrGetExisting(message.Id, valuePlaceholder, DateTimeOffset.Now + expiration) is not null;
}