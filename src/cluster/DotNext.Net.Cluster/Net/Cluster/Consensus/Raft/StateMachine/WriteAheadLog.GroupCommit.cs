using System.Collections.Concurrent;
using System.Runtime.CompilerServices;

namespace DotNext.Net.Cluster.Consensus.Raft.StateMachine;

using Buffers;
using Runtime.CompilerServices;
using Threading;

// Group commit (#125). Single buffered appends (BinaryLogEntry, entries exposing their payload as memory,
// and entries that format themselves into a buffer) are queued and written by one committer. It drains
// every request queued when its batch starts, writes them in queue order and persists them with one cycle.
// The durability contract is unchanged: LastEntryIndex is published by that cycle, and each caller completes
// only after the cycle that covers its entry. Unbuffered appends and configuration entries keep their own cycle.
partial class WriteAheadLog
{
    // Diagnostics only, see QueuedSynchronizer.TrackSuspendedCallers.
    internal const string GroupCommitCallerInfo = "Append Buffered Entries";

    private readonly ConcurrentQueue<AppendRequest> appendRequests = new();
    // The committer runs inline on the first caller's thread up to its first await, as a direct append did.
    private readonly AsyncAutoResetEventSlim appendTrigger = new(runContinuationsAsynchronously: false);
    private readonly List<AppendRequest> stagedRequests = []; // accessed by the committer only
    private readonly List<(AppendRequest Request, Exception Error)> rejectedRequests = []; // accessed by the committer only
    private readonly Task committerTask;

    // A custom allocator owner may block on, or re-enter, the WAL when disposed. Its release must not run on the
    // committer, which would then be unable to drain an append issued from Dispose. The pool owner is released inline.
    private readonly bool releaseBuffersAsynchronously;

    private ValueTask<long> AppendBufferedAsync(BinaryLogEntry entry, MemoryOwner<byte> buffer, bool requireCurrentTerm, CancellationToken token)
    {
        var request = new AppendRequest(entry, buffer, requireCurrentTerm, releaseBuffersAsynchronously);

        // An already-canceled token completes the request here, through its guarded release path.
        request.RegisterCancellation(token);
        if (request.Task.IsCompleted)
            return new(request.Task);

        appendRequests.Enqueue(request);
        appendTrigger.Set();

        // The committer stops on disposal, so it cannot see a request queued after its final drain.
        if (lifetimeToken.IsCancellationRequested)
            FailAppendRequests(new ObjectDisposedException(GetType().Name));

        return new(request.Task);
    }

    [AsyncMethodBuilder(typeof(SpawningAsyncTaskMethodBuilder))]
    private async Task CommitAppendsAsync(CancellationToken token)
    {
        try
        {
            for (; !token.IsCancellationRequested; await appendTrigger.WaitAsync().ConfigureAwait(false))
            {
                while (!appendRequests.IsEmpty && !token.IsCancellationRequested)
                {
                    try
                    {
                        await CommitAppendBatchAsync(token).ConfigureAwait(false);
                    }
                    catch (Exception e) when (!token.IsCancellationRequested)
                    {
                        // A lock failure is unexpected here; fail the waiting callers rather than stop the committer.
                        FailAppendRequests(e);
                    }
                }
            }
        }
        catch (OperationCanceledException e) when (e.CancellationToken == token)
        {
            // the WAL is being disposed
        }
        finally
        {
            FailAppendRequests(new ObjectDisposedException(GetType().Name));
        }
    }

    private async ValueTask CommitAppendBatchAsync(CancellationToken token)
    {
        Exception? failure = null;
        var staged = stagedRequests;
        var rejected = rejectedRequests;
        lockManager.SetCallerInformation(GroupCommitCallerInfo);
        await lockManager.AcquireAppendLockAsync(token).ConfigureAwait(false);
        try
        {
            await AcquirePersistenceLockAsync(AppendCause, token).ConfigureAwait(false);
            var mutationStarted = false;
            try
            {
                StageAppendRequests(staged, rejected);
                if (staged.Count > 0)
                {
                    mutationStarted = true;
                    await PrepareAppendAsync(LastEntryIndex + 1L).ConfigureAwait(false);
                    foreach (var request in staged)
                        request.Index = AppendBuffered(request.Entry);

                    await PersistAppendAsync(staged[0].Index).ConfigureAwait(false);
                }
            }
            catch (Exception e)
            {
                // Every entry of the batch shares the failed cycle, so none of them is acknowledged.
                if (mutationStarted)
                    OnBackgroundTaskFailure(e);

                failure = e;
            }
            finally
            {
                ReleasePersistenceLock();
            }
        }
        finally
        {
            lockManager.ReleaseAppendLock();
        }

        // Requests are settled, and their buffers released, outside the locks: a custom allocator owner
        // may block or re-enter the WAL on disposal.
        try
        {
            foreach (var (request, error) in rejected)
                request.Fail(error);

            foreach (var request in staged)
            {
                if (failure is null)
                    request.Complete();
                else
                    request.Fail(failure);
            }
        }
        finally
        {
            rejected.Clear();
            staged.Clear();
        }
    }

    // Call under the append and persistence locks. Drains the requests queued when the batch starts.
    // A request that fails its own checks is rejected alone, before the batch modifies the log.
    private void StageAppendRequests(List<AppendRequest> staged, List<(AppendRequest, Exception)> rejected)
    {
        for (var count = appendRequests.Count; count > 0 && appendRequests.TryDequeue(out var request); count--)
        {
            if (!request.TryStage())
                continue; // canceled by the caller

            try
            {
                ThrowOnInternalError();
                ThrowIfNotCurrentTerm(request.Entry.Term, request.RequireCurrentTerm);
            }
            catch (Exception e)
            {
                rejected.Add((request, e));
                continue;
            }

            staged.Add(request);
        }
    }

    private void FailAppendRequests(Exception e)
    {
        while (appendRequests.TryDequeue(out var request))
        {
            if (request.TryStage())
                request.Fail(e);
        }
    }

    private sealed class AppendRequest : TaskCompletionSource<long>
    {
        private const int QueuedState = 0, StagedState = 1, CanceledState = 2;

        internal readonly BinaryLogEntry Entry;
        internal readonly bool RequireCurrentTerm;
        private readonly bool releaseAsynchronously;
        private MemoryOwner<byte> buffer; // owns the payload of Entry, if not empty
        private CancellationTokenRegistration registration;
        private int state;
        internal long Index;

        internal AppendRequest(BinaryLogEntry entry, MemoryOwner<byte> buffer, bool requireCurrentTerm, bool releaseAsynchronously)
            : base(TaskCreationOptions.RunContinuationsAsynchronously)
        {
            Entry = entry;
            this.buffer = buffer;
            RequireCurrentTerm = requireCurrentTerm;
            this.releaseAsynchronously = releaseAsynchronously;
        }

        // Call before the request is queued.
        internal void RegisterCancellation(CancellationToken token)
        {
            if (token.CanBeCanceled)
            {
                registration = token.UnsafeRegister(
                    static (request, token) => Unsafe.As<AppendRequest>(request!).Cancel(token),
                    this);
            }
        }

        // Cancellation is observed only while the request is queued. Once staged, the entry is written
        // and the caller completes with the cycle, as an unbatched append canceled after its checks does.
        private void Cancel(CancellationToken token)
        {
            if (Interlocked.CompareExchange(ref state, CanceledState, QueuedState) is QueuedState)
            {
                try
                {
                    ReleaseResources(disposeRegistration: false);
                }
                finally
                {
                    TrySetCanceled(token);
                }
            }
        }

        // Grants the caller exclusive ownership of the request; it must then be completed or failed.
        internal bool TryStage() => Interlocked.CompareExchange(ref state, StagedState, QueuedState) is QueuedState;

        internal void Complete()
        {
            try
            {
                ReleaseResources(disposeRegistration: true);
            }
            finally
            {
                TrySetResult(Index);
            }
        }

        internal void Fail(Exception e)
        {
            try
            {
                ReleaseResources(disposeRegistration: true);
            }
            finally
            {
                TrySetException(e);
            }
        }

        // The request is settled whatever happens here, and a failure to release one request's buffer
        // must not prevent the other requests of the batch, whose entries are already durable, from completing.
        private void ReleaseResources(bool disposeRegistration)
        {
            try
            {
                if (disposeRegistration)
                    registration.Unregister();
            }
            catch
            {
                // It does not change the outcome of the append.
            }

            if (buffer.IsEmpty)
            {
                // nothing to release
            }
            else if (releaseAsynchronously)
            {
                ThreadPool.UnsafeQueueUserWorkItem(static buffer => Release(ref buffer), buffer, preferLocal: false);
                buffer = default;
            }
            else
            {
                Release(ref buffer);
            }
        }

        private static void Release(ref MemoryOwner<byte> buffer)
        {
            try
            {
                buffer.Dispose();
            }
            catch
            {
                // Options.Allocator may return an owner whose disposal throws. It does not change the outcome of the append.
            }
        }
    }
}
