using System.Diagnostics;
using System.Diagnostics.CodeAnalysis;

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
        /// Cannot be acquired concurrently with <see cref="Overwrite"/>, and cannot pass a suspended <see cref="ReadBarrier"/>.
        /// </remarks>
        Read = 0,

        /// <summary>
        /// Allows the infrastructure to remove the entries applied to the snapshot.
        /// </summary>
        /// <remarks>
        /// Waits for the readers that hold the lock, and cannot be acquired concurrently with
        /// <see cref="ReadBarrier"/>, <see cref="Overwrite"/>.
        /// The readers that arrive after the barrier is acquired are not blocked, because they observe the new snapshot.
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
        /// <see cref="Commit"/>, <see cref="ReadBarrier"/>.
        /// </remarks>
        Overwrite,
    }

#if DEBUG
    internal
#else 
    private
#endif
    sealed class LockManager : QueuedSynchronizer<LockType>
    {
        private ulong readersCount;
        private bool appendLockState, overwriteLockState, commitLockState;

        protected override bool CanAcquire(LockType type) => type switch
        {
            LockType.Read => !overwriteLockState,
            LockType.ReadBarrier => !overwriteLockState && readersCount is 0U,
            LockType.Append => !appendLockState,
            LockType.Commit => !overwriteLockState && !commitLockState,
            LockType.Overwrite => appendLockState && !overwriteLockState && !commitLockState && readersCount is 0UL,
            _ => false
        };

        // A caller passes a suspended caller only if holding its lock cannot make CanAcquire false for the suspended one,
        // so the suspended caller is never delayed: a persist cycle no longer blocks reads and commits (#126).
        // Callers of the same type stay in order, and nothing passes the upgrade to Overwrite.
        protected override bool CanOvertake(LockType type, LockType suspended) => (type, suspended) switch
        {
            (_, LockType.Overwrite) or (LockType.Overwrite, _) => false,
            (LockType.Read or LockType.ReadBarrier, LockType.ReadBarrier) => false,
            (LockType.Read or LockType.ReadBarrier, LockType.Read or LockType.Append or LockType.Commit) => true,
            (LockType.Append, LockType.Read or LockType.ReadBarrier or LockType.Commit) => true,
            (LockType.Commit, LockType.Read or LockType.ReadBarrier or LockType.Append) => true,
            _ => false,
        };

        protected override void AcquireCore(LockType type)
        {
            switch (type)
            {
                case LockType.Read:
                    readersCount++;
                    break;
                case LockType.ReadBarrier:
                    readersCount = 1L;
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
                case LockType.Read or LockType.ReadBarrier:
                    readersCount--;
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

        public ValueTask AcquireReadLockAsync(CancellationToken token = default)
            => AcquireReadLockAsync(ReadCause, token);

        public ValueTask AcquireReadLockAsync(string cause, CancellationToken token = default)
            => AcquireTracedAsync(LockType.Read, "read", cause, token);

        public void ReleaseReadLock() => Release(LockType.Read);
        
        public ValueTask AcquireReadBarrierAsync(CancellationToken token = default)
            => AcquireTracedAsync(LockType.ReadBarrier, "read-barrier", CleanupCause, token);

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