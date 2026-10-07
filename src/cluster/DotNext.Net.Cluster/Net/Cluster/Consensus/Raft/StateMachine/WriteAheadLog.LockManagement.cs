using System.Diagnostics;
using System.Diagnostics.CodeAnalysis;
using System.Runtime.CompilerServices;

namespace DotNext.Net.Cluster.Consensus.Raft.StateMachine;

using Threading;

partial class WriteAheadLog
{
    [SuppressMessage("Usage", "CA2213", Justification = "False positive")]
    private readonly LockManager lockManager;

#if DEBUG
    internal
#else 
    private
#endif
    enum LockType
    {
        /// <summary>
        /// Allows reading of the log entries.
        /// </summary>
        /// <remarks>
        /// Cannot be acquired concurrently with <see cref="Overwrite"/>. Passes a suspended <see cref="ReadBarrier"/>,
        /// because the barrier waits only for the readers registered before it was requested (#128).
        /// </remarks>
        Read = 0,

        /// <summary>
        /// Allows the infrastructure to remove the entries applied to the snapshot.
        /// </summary>
        /// <remarks>
        /// Waits for the readers (<see cref="Read"/> and <see cref="Flush"/>) registered before the barrier was requested,
        /// and cannot be acquired concurrently with <see cref="ReadBarrier"/>, <see cref="Overwrite"/>.
        /// The readers registered after the barrier is requested are not blocked, because they observe the new snapshot:
        /// the cleanup requests the barrier only after the snapshot it removes the pages for has been published.
        /// </remarks>
        ReadBarrier,

        /// <summary>
        /// Allows appending of a new entries to the end of the log.
        /// </summary>
        /// <remarks>
        /// Cannot be acquired concurrently with <see cref="Append"/>, <see cref="Overwrite"/>.
        /// </remarks>
        Append,
        
        /// <summary>
        /// Allows committing of the log entries.
        /// </summary>
        /// <remarks>
        /// Cannot be acquired concurrently with <see cref="Commit"/> and <see cref="Overwrite"/>.
        /// </remarks>
        Commit,

        /// <summary>
        /// Allows overwriting of the existing uncommitted entries. 
        /// </summary>
        /// <remarks>
        /// Cannot be acquired concurrently with <see cref="Append"/>, <see cref="Overwrite"/>, <see cref="Read"/>,
        /// <see cref="Commit"/>, <see cref="ReadBarrier"/>, <see cref="Flush"/>.
        /// </remarks>
        Overwrite,

        /// <summary>
        /// Allows the flusher to read the log boundaries and persist the committed entries.
        /// </summary>
        /// <remarks>
        /// Compatible with the same locks as <see cref="Read"/>. It passes a suspended <see cref="ReadBarrier"/>
        /// like a read, but no other suspended caller: a flush pass also takes the persistence lock, and letting it
        /// pass the queued appends would put a whole flush cycle between two consecutive append cycles instead of one
        /// flush after them.
        /// </remarks>
        Flush,
    }

#if DEBUG
    internal
#else 
    private
#endif
    sealed class LockManager : QueuedSynchronizer<LockType>
    {
        private ulong readersCount;
        private bool appendLockState, overwriteLockState, commitLockState, readBarrierState;

        // Grace period of the read barrier (#128). A Read or Flush holder registers after the lock is granted and before
        // it observes the snapshot or the pages, and receives the current epoch as a ticket. Requesting the barrier
        // starts a new epoch and moves the registered readers to preexistingReaders, the only readers the barrier waits
        // for: a reader registered later observes the snapshot published before the request, so it cannot reach
        // the pages the barrier removes. The set only shrinks until the next request, so continuous reads cannot
        // starve the barrier. The cleanup keeps at most one barrier request outstanding.
        private readonly System.Threading.Lock graceLock = new();
        private long graceEpoch;
        private ulong currentReaders, preexistingReaders;

        protected override bool CanAcquire(LockType type) => type switch
        {
            LockType.Read or LockType.Flush => !overwriteLockState,
            LockType.ReadBarrier => !overwriteLockState && !readBarrierState && Volatile.Read(in preexistingReaders) is 0UL,
            LockType.Append => !appendLockState,
            LockType.Commit => !overwriteLockState && !commitLockState,
            LockType.Overwrite => appendLockState && !overwriteLockState && !commitLockState && readersCount is 0UL,
            _ => false
        };

        // A caller passes a suspended caller only if holding its lock cannot make CanAcquire false for the suspended one,
        // so the suspended caller is never delayed: a persist cycle no longer blocks reads and commits (#126).
        // A read or a flush pass registers in the current epoch, never in the preexisting readers the barrier waits for,
        // so both pass a suspended barrier (#128).
        // Callers of the same type stay in order, nothing passes the upgrade to Overwrite, and the flusher passes nobody else.
        protected override bool CanOvertake(LockType type, LockType suspended) => (type, suspended) switch
        {
            (_, LockType.Overwrite) or (LockType.Overwrite, _) => false,
            (LockType.Read or LockType.Flush, LockType.ReadBarrier) => true,
            (LockType.Flush, _) => false,
            (LockType.ReadBarrier, LockType.ReadBarrier) => false,
            (LockType.Read or LockType.ReadBarrier, LockType.Read or LockType.Flush or LockType.Append or LockType.Commit) => true,
            (LockType.Append, LockType.Read or LockType.Flush or LockType.ReadBarrier or LockType.Commit) => true,
            (LockType.Commit, LockType.Read or LockType.Flush or LockType.ReadBarrier or LockType.Append) => true,
            _ => false,
        };

        protected override void AcquireCore(LockType type)
        {
            switch (type)
            {
                case LockType.Read or LockType.Flush:
                    readersCount++;
                    break;
                case LockType.ReadBarrier:
                    // The readers registered after the request may hold the lock, so the barrier adds itself to them.
                    // It still keeps Overwrite out while the pages are removed.
                    readersCount++;
                    readBarrierState = true;
                    break;
                case LockType.Append:
                    appendLockState = true;
                    break;
                case LockType.Commit:
                    commitLockState = true;
                    break;
                case LockType.Overwrite:
                    overwriteLockState = true;
                    break;
                default:
                    Debug.Fail($"Unexpected lock type {type}");
                    break;
            }
        }

        protected override void ReleaseCore(LockType type)
        {
            switch (type)
            {
                case LockType.Read or LockType.Flush:
                    readersCount--;
                    break;
                case LockType.ReadBarrier:
                    readersCount--;
                    readBarrierState = false;
                    break;
                case LockType.Append or LockType.Overwrite:
                    appendLockState = overwriteLockState = false;
                    break;
                case LockType.Commit:
                    commitLockState = false;
                    break;
                default:
                    Debug.Fail($"Unexpected lock type {type}");
                    break;
            }
        }

        private long RegisterReader()
        {
            lock (graceLock)
            {
                currentReaders++;
                return graceEpoch;
            }
        }

        private void UnregisterReader(long ticket)
        {
            lock (graceLock)
            {
                if (ticket == graceEpoch)
                {
                    Debug.Assert(currentReaders > 0UL);
                    currentReaders--;
                }
                else
                {
                    // Registered before the latest barrier request, or before an earlier one that was canceled:
                    // every request moves all the registered readers to preexistingReaders.
                    Debug.Assert(ticket < graceEpoch);
                    Debug.Assert(preexistingReaders > 0UL);
                    Volatile.Write(ref preexistingReaders, preexistingReaders - 1UL);
                }
            }
        }

        private void StartGracePeriod()
        {
            lock (graceLock)
            {
                graceEpoch++;
                Volatile.Write(ref preexistingReaders, preexistingReaders + currentReaders);
                currentReaders = 0UL;
            }
        }

        private ValueTask<long> AcquireRegisteredAsync(LockType type, string lockName, string cause, CancellationToken token)
        {
            var task = AcquireTracedAsync(type, lockName, cause, token);
            if (!task.IsCompletedSuccessfully)
                return AcquireRegisteredCoreAsync(task);

            task.GetAwaiter().GetResult();
            return new(RegisterReader());
        }

        [AsyncMethodBuilder(typeof(PoolingAsyncValueTaskMethodBuilder<>))]
        private async ValueTask<long> AcquireRegisteredCoreAsync(ValueTask acquisition)
        {
            await acquisition.ConfigureAwait(false);
            return RegisterReader();
        }

        private void ReleaseRegistered(LockType type, long ticket)
        {
            // Unregister first: releasing the lock drains the queue, which grants the barrier once the last
            // preexisting reader is gone.
            UnregisterReader(ticket);
            Release(type);
        }

        // Node tags of the opt-in lock wait diagnostics, see WriteAheadLog.Diagnostics.cs.
        private readonly TagList diagnosticTags;

        public TagList DiagnosticTags
        {
            init => diagnosticTags = value;
        }

        private ValueTask AcquireTracedAsync(LockType type, string lockName, string cause, CancellationToken token)
            => IsLockTracingEnabled ? AcquireTracedCoreAsync(type, lockName, cause, token) : AcquireAsync(type, token);

        private async ValueTask AcquireTracedCoreAsync(LockType type, string lockName, string cause, CancellationToken token)
        {
            var start = Stopwatch.GetTimestamp();
            await AcquireAsync(type, token).ConfigureAwait(false);
            RecordLockWait(in diagnosticTags, lockName, cause, start);
        }

        // Returns the ticket to be passed to ReleaseReadLock.
        public ValueTask<long> AcquireReadLockAsync(CancellationToken token = default)
            => AcquireReadLockAsync(ReadCause, token);

        public ValueTask<long> AcquireReadLockAsync(string cause, CancellationToken token = default)
            => AcquireRegisteredAsync(LockType.Read, "read", cause, token);

        public void ReleaseReadLock(long ticket) => ReleaseRegistered(LockType.Read, ticket);

        // Reported as the read lock with the flush cause, as before #126, so the diagnostics stay comparable.
        // Returns the ticket to be passed to ReleaseFlushLock.
        public ValueTask<long> AcquireFlushLockAsync(CancellationToken token = default)
            => AcquireRegisteredAsync(LockType.Flush, "read", FlushCause, token);

        public void ReleaseFlushLock(long ticket) => ReleaseRegistered(LockType.Flush, ticket);

        // The caller must publish the snapshot whose squashed pages it removes before the request,
        // and must not request the barrier again until this request completes.
        public ValueTask AcquireReadBarrierAsync(CancellationToken token = default)
        {
            StartGracePeriod();
            return AcquireTracedAsync(LockType.ReadBarrier, "read-barrier", CleanupCause, token);
        }

        public void ReleaseReadBarrier() => Release(LockType.ReadBarrier);

        public ValueTask AcquireAppendLockAsync(CancellationToken token = default)
            => AcquireTracedAsync(LockType.Append, "append", AppendCause, token);

        public void ReleaseAppendLock() => Release(LockType.Append);

        public ValueTask AcquireCommitLockAsync(CancellationToken token = default)
            => AcquireTracedAsync(LockType.Commit, "commit", CommitCause, token);

        public bool TryAcquireCommitLock()
        {
            if (!IsLockTracingEnabled)
                return TryAcquire(LockType.Commit);

            var start = Stopwatch.GetTimestamp();
            var acquired = TryAcquire(LockType.Commit);

            // A failed attempt is followed by AcquireCommitLockAsync, which records the wait.
            if (acquired)
                RecordLockWait(in diagnosticTags, "commit", CommitCause, start);

            return acquired;
        }
        
        public void ReleaseCommitLock() => Release(LockType.Commit);

        public ValueTask UpgradeToOverwriteLockAsync(CancellationToken token = default)
            // The caller retains Append, so an ordinary writer ahead of this upgrade cannot make progress.
            => AcquirePriorityAsync(LockType.Overwrite, token);
    }
}