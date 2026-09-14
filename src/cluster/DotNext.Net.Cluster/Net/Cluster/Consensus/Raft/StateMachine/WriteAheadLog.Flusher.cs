using System.Diagnostics;
using System.Diagnostics.CodeAnalysis;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;

namespace DotNext.Net.Cluster.Consensus.Raft.StateMachine;

using Diagnostics;
using IO.Log;
using Threading;

partial class WriteAheadLog
{
    private readonly AsyncAutoResetEventSlim? flushTrigger;
    private readonly AsyncTrigger? flushCompleted;
    private readonly AsyncExclusiveLock? foregroundFlushLock;
    private readonly Task flusherTask;
    private readonly WeakReference<Task?> cleanupTask = new(target: null, trackResurrection: false);
    
    private Checkpoint checkpoint;
    private long commitIndex; // Commit lock protects modification of this field
    private long nextUnflushedIndex, flusherOldSnapshot;

    private async Task FlushAsync<T>(T flushTrigger, CancellationToken token, long? targetIndex = null)
        where T : struct, IFlushTrigger
    {
        if (T.IsBackground)
            await Task.Yield();

        var cancellation = T.IsBackground ? cancellationTokens.Combine(token, backgroundTaskFailureToken) : default;
        if (T.IsBackground)
            token = cancellation.Token;

        // Weak ref tracks the task, but allows GC to collect associated state machine
        // as soon as possible. While the task is running, it cannot be collected, because it's referenced
        // by the async state machine.
        try
        {
            if (T.IsBackground)
            {
                flusherOldSnapshot = SnapshotIndex;
            }

            while (!token.IsCancellationRequested && backgroundTaskFailure is null)
            {
                // Ensure that the flusher is not running with the snapshot installation process concurrently.
                // The boundaries are read under the lock: an installation moves the commit index, the snapshot
                // index and the flushing boundary at once, and a pass built from a mixture of both states can
                // ask for squashed indices or persist a checkpoint that goes backwards.
                lockManager.SetCallerInformation("Flush Pages");
                await lockManager.AcquireReadLockAsync(token).ConfigureAwait(false);
                long newSnapshot;
                try
                {
                    newSnapshot = SnapshotIndex;
                    var fromIndex = nextUnflushedIndex;

                    // A snapshot covers everything below its index, so it is always part of the durable boundary.
                    var newIndex = long.Max(targetIndex ?? LastCommittedEntryIndex, newSnapshot);

                    if (newIndex >= fromIndex)
                    {
                        var ts = new Timestamp();
                        await Flush(fromIndex, newIndex, token).ConfigureAwait(false);

                        // everything up to newIndex is flushed, save the commit index
                        await checkpoint.UpdateAsync<CheckpointVersion0>(new(newIndex), token).ConfigureAwait(false);
                        FlushDurationMeter.Record(ts.ElapsedMilliseconds);

                        // A queued manual caller can have an older target than a completed pass.
                        Atomic.Write(ref nextUnflushedIndex, newIndex + 1L);
                    }
                }
                finally
                {
                    lockManager.ReleaseReadLock();
                }

                if ((!cleanupTask.TryGetTarget(out var task) || task.IsCompletedSuccessfully) && flusherOldSnapshot < newSnapshot)
                    cleanupTask.SetTarget(CleanUpAsync(newSnapshot, lifetimeToken));

                flusherOldSnapshot = newSnapshot;
                flushTrigger.NotifyCompleted();
                if (!await flushTrigger.WaitAsync(token).ConfigureAwait(false))
                    break;
            }
        }
        catch (OperationCanceledException e) when (T.IsBackground && e.CancellationToken == token)
        {
            // suspend
        }
        catch (Exception e) when (T.IsBackground)
        {
            OnBackgroundTaskFailure(e);
        }
        finally
        {
            flushTrigger.Dispose();
            await cancellation.DisposeAsync().ConfigureAwait(false);
        }
    }

    // Called under the overwrite lock right after a snapshot has been installed. The indices below the snapshot
    // are squashed, so the flusher must not look for their metadata; and the boundary has to be scheduled for
    // persistence even though no ordinary commit happened.
    private void OnSnapshotInstalled(long snapshotIndex)
    {
        Atomic.Write(ref nextUnflushedIndex, long.Max(Atomic.Read(in nextUnflushedIndex), snapshotIndex));
        flushTrigger?.Set();
    }

    private Task Flush(long fromIndex, long toIndex, CancellationToken token)
    {
        var metadataTask = metadataPages.FlushAsync(fromIndex, toIndex, token).AsTask();

        var toMetadata = metadataPages.GetView<MetadataReader>(toIndex).Metadata;
        var fromMetadata = metadataPages.GetView<MetadataReader>(fromIndex).Metadata;
        var dataTask = dataPages.FlushAsync(fromMetadata.Offset, toMetadata.End, token).AsTask();

        FlushRateMeter.Add(toIndex - fromIndex + 1L, measurementTags);
        return Task.WhenAll(metadataTask, dataTask);
    }

    private async Task EnsureFlushedAsync(long targetIndex, CancellationToken token)
    {
        ObjectDisposedException.ThrowIf(IsDisposingOrDisposed, this);
        ThrowOnInternalError();

        var linkedTokenSource = cancellationTokens.Combine(token, lifetimeToken, backgroundTaskFailureToken);
        try
        {
            if (flushCompleted is not null)
            {
                await flushCompleted.SpinWaitAsync(new FlushChecker(this, targetIndex), linkedTokenSource.Token).ConfigureAwait(false);
            }
            else
            {
                Debug.Assert(foregroundFlushLock is not null);
                await foregroundFlushLock.AcquireAsync(linkedTokenSource.Token).ConfigureAwait(false);
                try
                {
                    await FlushAsync<ForegroundTrigger>(new(), linkedTokenSource.Token, targetIndex).ConfigureAwait(false);
                    linkedTokenSource.Token.ThrowIfCancellationRequested();
                }
                finally
                {
                    foregroundFlushLock.Release();
                }
            }

            ThrowOnInternalError();
        }
        catch (OperationCanceledException e) when (e.CancellationToken == linkedTokenSource.Token)
        {
            ThrowWhenCanceled(linkedTokenSource);
        }
        finally
        {
            await linkedTokenSource.DisposeAsync().ConfigureAwait(false);
        }
    }

    [StructLayout(LayoutKind.Auto)]
    private readonly struct FlushChecker(WriteAheadLog log, long targetIndex) : ISupplier<bool>
    {
        bool ISupplier<bool>.Invoke()
            => log.backgroundTaskFailure is not null || Atomic.Read(in log.nextUnflushedIndex) > targetIndex;
    }

    [DoesNotReturn]
    private void ThrowWhenCanceled(CancellationTokenMultiplexer.Scope cts)
    {
        ObjectDisposedException.ThrowIf(cts.CancellationOrigin == lifetimeToken, this);
        if (cts.CancellationOrigin == backgroundTaskFailureToken)
        {
            ThrowOnInternalError();
            ObjectDisposedException.ThrowIf(IsDisposingOrDisposed, this);
        }

        throw new OperationCanceledException(cts.CancellationOrigin);
    }

    /// <summary>
    /// Flushes and writes the checkpoint.
    /// </summary>
    /// <remarks>
    /// Captures <see cref="LastCommittedEntryIndex"/> when called and waits until entries through
    /// that index are persisted. Later commits do not extend this request's target.
    /// Uncommitted appended entries are not included in the recoverable checkpoint.
    /// When automatic flushing is enabled, this method waits for the background flusher;
    /// otherwise, it performs the flush. Concurrent manual flushes are serialized.
    /// A fatal error in the flusher, applier, or cleanup worker fails pending flush waits.
    /// Queued manual requests fail without waiting for an active flush to finish.
    /// Subsequent requests fail even if their target was already persisted.
    /// Requests that completed successfully before the error remain successful.
    /// </remarks>
    /// <param name="token">The token that can be used to cancel the operation.</param>
    /// <returns>The task representing asynchronous state of the operation.</returns>
    /// <exception cref="OperationCanceledException">The operation has been canceled.</exception>
    /// <exception cref="ObjectDisposedException">The log is disposed while waiting for the flush.</exception>
    /// <exception cref="InternalException">
    /// A background WAL operation failed. The first failure is retained as the inner exception.
    /// </exception>
    public Task FlushAsync(CancellationToken token = default)
        => EnsureFlushedAsync(LastCommittedEntryIndex, token);

    /// <inheritdoc cref="IAuditTrail.LastCommittedEntryIndex"/>
    public long LastCommittedEntryIndex
    {
        get => Atomic.Read(in commitIndex);
        private set => Atomic.Write(ref commitIndex, value);
    }
    
    private long Commit(long index)
    {
        var oldCommitIndex = LastCommittedEntryIndex;
        if (index > oldCommitIndex)
        {
            LastCommittedEntryIndex = index;
        }
        else
        {
            index = oldCommitIndex;
        }

        return index - oldCommitIndex;
    }

    [MethodImpl(MethodImplOptions.NoInlining)]
    private void OnCommitted(long count)
    {
        applyTrigger.Set();
        flushTrigger?.Set();
        CommitRateMeter.Add(count, measurementTags);
    }
    
    private interface IFlushTrigger : IDisposable
    {
        ValueTask<bool> WaitAsync(CancellationToken token);

        void NotifyCompleted();

        static virtual bool IsBackground => true;
    }

    [StructLayout(LayoutKind.Auto)]
    private readonly struct BackgroundTrigger : IFlushTrigger
    {
        private readonly AsyncAutoResetEventSlim flushTrigger;
        private readonly AsyncTrigger flushNotification;
        
        public BackgroundTrigger(AsyncAutoResetEventSlim resetEvent, AsyncTrigger notification)
        {
            flushTrigger = resetEvent;
            flushNotification = notification;
        }
        
        ValueTask<bool> IFlushTrigger.WaitAsync(CancellationToken token)
            => flushTrigger.WaitAsync();

        void IFlushTrigger.NotifyCompleted() => flushNotification.Signal(resumeAll: true);

        void IDisposable.Dispose()
        {
            // nothing to do
        }
    }

    [StructLayout(LayoutKind.Auto)]
    [SuppressMessage("Usage", "CA1001", Justification = "False positive")]
    private readonly struct TimeoutTrigger : IFlushTrigger
    {
        private readonly PeriodicTimer timer;
        private readonly AsyncTrigger flushNotification;

        public TimeoutTrigger(TimeSpan timeout, AsyncTrigger notification)
        {
            timer = new(timeout);
            flushNotification = notification;
        }
        
        ValueTask<bool> IFlushTrigger.WaitAsync(CancellationToken token)
            => timer.WaitForNextTickAsync(token);

        void IFlushTrigger.NotifyCompleted() => flushNotification.Signal(resumeAll: true);

        void IDisposable.Dispose() => timer.Dispose();
    }

    [StructLayout(LayoutKind.Auto)]
    private readonly struct ForegroundTrigger : IFlushTrigger
    {
        ValueTask<bool> IFlushTrigger.WaitAsync(CancellationToken token)
            => ValueTask.FromResult(false);

        void IFlushTrigger.NotifyCompleted()
        {
        }

        static bool IFlushTrigger.IsBackground => false;
        
        void IDisposable.Dispose()
        {
            // nothing to do
        }
    }
}