using System.Runtime.CompilerServices;

namespace DotNext.Net.Cluster.Consensus.Raft.StateMachine;

using Runtime.CompilerServices;

partial class WriteAheadLog
{
    // Guards the cleanup coordination state below.
    private readonly System.Threading.Lock cleanupLock = new();

    // The newest snapshot index whose squashed pages are to be removed. Only grows.
    private long pendingCleanupIndex;

    // The index up to which the cleanup has removed the squashed pages. Modified by the running cleanup only.
    private long cleanedUpIndex;

    // At most one cleanup loop, and so at most one read barrier request, is outstanding. A loop is started only by
    // ScheduleCleanUp, and it is stored in cleanupTask under the same lock, so cleanupTask is always the latest loop.
    // An earlier loop clears cleanupRunning under the lock and does nothing after that.
    private bool cleanupRunning, cleanupStopped;
    private Task? cleanupTask;

    // Called by the flusher after a pass has observed a newer snapshot. The flusher never waits for the cleanup (#128):
    // the barrier waits for the readers that may still reach the squashed pages, and one of them can be a replication
    // read held for a whole RPC. A running cleanup picks up the newest index when it finishes the current one.
    private void ScheduleCleanUp(long upToIndex)
    {
        lock (cleanupLock)
        {
            pendingCleanupIndex = Math.Max(pendingCleanupIndex, upToIndex);
            if (cleanupRunning || cleanupStopped)
                return;

            cleanupRunning = true;

            // The builder never runs the loop synchronously, so the loop cannot enter the lock recursively.
            cleanupTask = CleanUpAsync(lifetimeToken);
        }
    }

    // Prevents new cleanup loops and returns the one that disposal must wait for.
    private Task? StopCleanUp()
    {
        lock (cleanupLock)
        {
            cleanupStopped = true;
            return cleanupTask;
        }
    }

    private bool TryGetPendingCleanup(out long upToIndex)
    {
        lock (cleanupLock)
        {
            upToIndex = pendingCleanupIndex;
            if (upToIndex > cleanedUpIndex)
                return true;

            cleanupRunning = false;
            return false;
        }
    }

    [AsyncMethodBuilder(typeof(SpawningAsyncTaskMethodBuilder))]
    private async Task CleanUpAsync(CancellationToken token)
    {
        while (TryGetPendingCleanup(out var upToIndex))
        {
            // A failed or canceled cleanup keeps cleanupRunning set: the log is failed or disposed,
            // and no further cleanup is started.
            if (!await CleanUpAsync(upToIndex, token).ConfigureAwait(false))
                return;

            lock (cleanupLock)
            {
                cleanedUpIndex = upToIndex;
            }
        }
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