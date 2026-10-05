using System.Diagnostics;
using DotNext.Benchmarks.DurableWrite.Oracles;
using DotNext.Net.Cluster.Consensus.Raft.StateMachine;
using static DotNext.Net.Cluster.Consensus.Raft.StateMachine.WriteAheadLog;

namespace DotNext.Benchmarks.DurableWrite;

/// <summary>
/// The storage of one node: a <see cref="WriteAheadLog"/> in <c>wal</c> and the snapshots of its
/// <see cref="HistoryStateMachine"/> in <c>sm</c>.
/// </summary>
internal static class NodeStorage
{
    // One system page, the default, is too small for 16 KiB entries.
    internal const int ChunkSize = 4 * 1024 * 1024;

    // The library default: a commit checkpoint on every commit. Appends are persisted before they complete either way.
    internal static readonly TimeSpan FlushInterval = TimeSpan.Zero;

    // The library default.
    internal static IntegrityHashAlgorithm HashAlgorithm => default;

    internal static DurabilitySettings Describe(bool noBuffering) => new()
    {
        FlushInterval = FlushInterval == TimeSpan.Zero ? "0 (commit checkpoint on every commit)" : FlushInterval.ToString(),
        ChunkSize = ChunkSize,
        NoBuffering = noBuffering,
        HashAlgorithm = HashAlgorithm.ToString(),
        AppendPersistence =
            "every append flushes its data and metadata pages to the device, flushes both directories and writes the " +
            "recovery checkpoint before LastEntryIndex is published (WriteAheadLog.PersistAppendAsync)",
        Acknowledgment =
            "RaftCluster.ReplicateAsync returns after the entry is appended durably on the leader, replicated to a " +
            "majority (a follower replies only after its own durable append), committed and applied on the leader",
    };

    internal static string StateMachineDirectory(string root) => Path.Combine(root, "sm");

    internal static string LogDirectory(string root) => Path.Combine(root, "wal");

    /// <param name="node">The <c>node</c> measurement tag, or <see langword="null"/> so the meter listener ignores this log.</param>
    internal static Options CreateOptions(string root, MemoryManagementStrategy memory, bool noBuffering, int? node)
    {
        var tags = new TagList();
        if (node is { } id)
            tags.Add(WalMeterListener.NodeTag, id);

        return new()
        {
            Location = LogDirectory(root),
            FlushInterval = FlushInterval,
            ChunkSize = ChunkSize,
            MemoryManagement = memory,
            NoBuffering = noBuffering,
            HashAlgorithm = HashAlgorithm,
            MeasurementTags = tags,
        };
    }

    internal static HistoryStateMachine CreateStateMachine(string root, int node, long snapshotInterval,
        OnlineHistoryChecker? checker, ApplyTimeline? timeline)
    {
        var directory = new DirectoryInfo(StateMachineDirectory(root));
        directory.Create();
        return new(directory, node, snapshotInterval, checker, timeline);
    }

    /// <summary>
    /// Reads what the node recovers from its storage alone: the snapshot, the committed entries replayed into a new
    /// state machine, and the uncommitted tail of the log.
    /// </summary>
    /// <remarks>
    /// The node must be stopped and its log disposed. The log is reopened without the node tag, so its counters do not
    /// count towards the cell. This runs in the same OS instance, so it proves the entries reached the file system,
    /// not that they survive a power loss; the latter is a residual blind spot of the tool.
    /// </remarks>
    /// <returns>The durable log, from index 1 without gaps; an entry without payload has no key.</returns>
    internal static async Task<IReadOnlyList<AppliedEntry>> RecoverAsync(string root, MemoryManagementStrategy memory, bool noBuffering,
        int node, TimeSpan timeout, CancellationToken token)
    {
        if (!Directory.Exists(LogDirectory(root)))
            return [];

        var stateMachine = CreateStateMachine(root, node, long.MaxValue, checker: null, timeline: null);
        await using (stateMachine.ConfigureAwait(false))
        {
            await stateMachine.RestoreAsync(token).ConfigureAwait(false);
            var log = new WriteAheadLog(CreateOptions(root, memory, noBuffering, node: null), stateMachine);
            await using (log.ConfigureAwait(false))
            {
                await log.InitializeAsync(token).ConfigureAwait(false);

                using var timeoutSource = CancellationTokenSource.CreateLinkedTokenSource(token);
                timeoutSource.CancelAfter(timeout);
                try
                {
                    await log.WaitForApplyAsync(log.LastCommittedEntryIndex, timeoutSource.Token).ConfigureAwait(false);
                }
                catch (OperationCanceledException) when (!token.IsCancellationRequested)
                {
                    throw new LivenessFailureException(
                        $"node {node} did not replay its {log.LastCommittedEntryIndex} committed entries within {timeout.TotalSeconds:F0} s");
                }

                var result = new List<AppliedEntry>(stateMachine.History);

                // The state machine sees no entry without payload, so the indexes after the last entry it saw are padded.
                for (var index = result.Count + 1L; index <= log.LastAppliedIndex; index++)
                    result.Add(AppliedEntry.CreateSkipped(index));

                var start = result.Count + 1L;
                if (start <= log.LastEntryIndex)
                {
                    using var reader = await log.ReadAsync(start, log.LastEntryIndex, token).ConfigureAwait(false);
                    foreach (var entry in reader)
                        result.Add(new(entry.Index, entry.Term, HistoryStateMachine.ReadKey(in entry)));
                }

                return result;
            }
        }
    }

    internal static void Delete(string root)
    {
        for (var attempt = 0; ; attempt++)
        {
            try
            {
                if (Directory.Exists(root))
                    Directory.Delete(root, recursive: true);

                return;
            }
            catch (Exception e) when (e is IOException or UnauthorizedAccessException && attempt < 10)
            {
                // A memory-mapped file can stay open for a moment after the log is disposed on Windows.
                Thread.Sleep(200);
            }
        }
    }
}
