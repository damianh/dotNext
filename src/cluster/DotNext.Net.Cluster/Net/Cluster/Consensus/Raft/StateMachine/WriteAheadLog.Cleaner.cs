using System.Runtime.CompilerServices;

namespace DotNext.Net.Cluster.Consensus.Raft.StateMachine;

using Runtime.CompilerServices;

partial class WriteAheadLog
{
    // The newest snapshot index whose squashed pages are to be removed. Only grows.
    private long pendingCleanupIndex;

    // The index up to which the cleanup has removed the squashed pages. Modified by the running cleanup only.
    private long cleanedUpIndex;

    // 1 while a cleanup loop is running. At most one loop, and so at most one read barrier request, is outstanding.
    private int cleanupRunning;

    // Called by the flusher after a pass has observed a newer snapshot. The flusher never waits for the cleanup (#128):
    // the barrier waits for the readers that may still reach the squashed pages, and one of them can be a replication
    // read held for a whole RPC. A running cleanup picks up the newest index when it finishes the current one.
    private void ScheduleCleanUp(long upToIndex)
    {
        for (long current; (current = Volatile.Read(in pendingCleanupIndex)) < upToIndex;)
        {
            if (Interlocked.CompareExchange(ref pendingCleanupIndex, upToIndex, current) == current)
                break;
        }

        StartCleanUp();
    }

    private void StartCleanUp()
    {
        // The started loop is the one disposal waits for.
        if (Interlocked.CompareExchange(ref cleanupRunning, 1, 0) is 0)
            cleanupTask.SetTarget(CleanUpAsync(lifetimeToken));
    }

    [AsyncMethodBuilder(typeof(SpawningAsyncTaskMethodBuilder))]
    private async Task CleanUpAsync(CancellationToken token)
    {
        for (long upToIndex; (upToIndex = Volatile.Read(in pendingCleanupIndex)) > cleanedUpIndex; cleanedUpIndex = upToIndex)
        {
            // A failed or canceled cleanup keeps cleanupRunning set: the log is failed or disposed,
            // and no further cleanup is started.
            if (!await CleanUpAsync(upToIndex, token).ConfigureAwait(false))
                return;
        }

        // The exchange is a full fence, so either this loop observes an index published after its last check
        // and starts a new loop, or the flusher that published it observes the cleared flag and starts one.
        Interlocked.Exchange(ref cleanupRunning, 0);
        if (Volatile.Read(in pendingCleanupIndex) > cleanedUpIndex)
            StartCleanUp();
    }

    private async ValueTask<bool> CleanUpAsync(long upToIndex, CancellationToken token)
    {
        // After the barrier, we know that there is no competing reader that reads the old snapshot version.
        // The snapshot at upToIndex was published before the barrier is requested, so the readers registered later
        // are not waited for.
        lockManager.SetCallerInformation("Remove Pages");
        try
        {
            await lockManager.AcquireReadBarrierAsync(token).ConfigureAwait(false);
        }
        catch (OperationCanceledException e) when (e.CancellationToken == token)
        {
            return false;
        }

        try
        {
            // The barrier can suspend this async flow. However, the OS flushes the pages in the background
            RemoveSquashedPages(upToIndex);

            // ensure that garbage reclamation is not running concurrently with the snapshot installation process
            await stateMachine.ReclaimGarbageAsync(upToIndex, token).ConfigureAwait(false);
            return true;
        }
        catch (OperationCanceledException e) when (e.CancellationToken == token)
        {
            return false;
        }
        catch (Exception e)
        {
            OnBackgroundTaskFailure(e);
            return false;
        }
        finally
        {
            lockManager.ReleaseReadBarrier();
        }
    }

    private void RemoveSquashedPages(long toIndex)
    {
        if (!metadataPages.TryGetMetadata(toIndex, out var metadata))
            return;

        var removedBytes = dataPages.DeletePages(metadata.End) + metadataPages.DeletePages(toIndex);
        if (removedBytes > 0L)
            BytesDeletedMeter.Record(removedBytes, measurementTags);
    }
}