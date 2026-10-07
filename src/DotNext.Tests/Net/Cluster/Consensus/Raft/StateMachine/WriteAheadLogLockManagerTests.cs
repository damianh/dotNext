namespace DotNext.Net.Cluster.Consensus.Raft.StateMachine;

[Collection(TestCollections.WriteAheadLog)]
public sealed class WriteAheadLogLockManagerTests : Test
{
    [Fact(Timeout = TestTimeouts.Default)]
    public static async Task OverwriteUpgradeDoesNotWaitBehindAppend()
    {
        var lockManager = new WriteAheadLog.LockManager();
        lockManager.TrackSuspendedCallers();

        Task appendB = null, appendC = null, upgradeB = null;
        try
        {
            await lockManager.AcquireAppendLockAsync(TestToken);

            lockManager.SetCallerInformation("B: append");
            appendB = lockManager.AcquireAppendLockAsync(TestToken).AsTask();

            lockManager.SetCallerInformation("C: append");
            appendC = lockManager.AcquireAppendLockAsync(TestToken).AsTask();

            Equal(["B: append", "C: append"], lockManager.GetSuspendedCallers());

            lockManager.ReleaseAppendLock();
            await appendB.WaitAsync(TestToken);

            Equal(["C: append"], lockManager.GetSuspendedCallers());

            lockManager.SetCallerInformation("B: overwrite");
            upgradeB = lockManager.UpgradeToOverwriteLockAsync(TestToken).AsTask();

            var completed = await Task.WhenAny(upgradeB, Task.Delay(TimeSpan.FromSeconds(1), TestToken));
            Same(upgradeB, completed);
            await upgradeB;

            lockManager.ReleaseAppendLock();
            await appendC.WaitAsync(TestToken);
            lockManager.ReleaseAppendLock();

            await lockManager.AcquireAppendLockAsync(TestToken);
            lockManager.ReleaseAppendLock();
        }
        finally
        {
            lockManager.Dispose(new OperationCanceledException("Test cleanup"));
            await ObserveFailureAsync(appendB);
            await ObserveFailureAsync(appendC);
            await ObserveFailureAsync(upgradeB);
        }
    }

    [Fact(Timeout = TestTimeouts.Default)]
    public static async Task OverwriteUpgradeWaitsForReadersAndCommitters()
    {
        await using var lockManager = new WriteAheadLog.LockManager();
        lockManager.TrackSuspendedCallers();

        await lockManager.AcquireAppendLockAsync(TestToken);
        var readTicket = await lockManager.AcquireReadLockAsync(TestToken);
        await lockManager.AcquireCommitLockAsync(TestToken);

        lockManager.SetCallerInformation("B: overwrite");
        var upgrade = lockManager.UpgradeToOverwriteLockAsync(TestToken).AsTask();

        lockManager.SetCallerInformation("C: append");
        var append = lockManager.AcquireAppendLockAsync(TestToken).AsTask();

        Equal(["B: overwrite", "C: append"], lockManager.GetSuspendedCallers());

        lockManager.ReleaseReadLock(readTicket);
        False(upgrade.IsCompleted);
        False(append.IsCompleted);

        lockManager.ReleaseCommitLock();
        await upgrade.WaitAsync(TestToken);
        False(append.IsCompleted);
        False(lockManager.TryAcquireCommitLock());

        lockManager.ReleaseAppendLock();
        await append.WaitAsync(TestToken);
        lockManager.ReleaseAppendLock();

        await lockManager.AcquireReadBarrierAsync(TestToken);
        lockManager.ReleaseReadBarrier();
    }

    [Fact(Timeout = TestTimeouts.Default)]
    public static async Task CanceledOverwriteUpgradeDoesNotLeakLocks()
    {
        await using var lockManager = new WriteAheadLog.LockManager();
        lockManager.TrackSuspendedCallers();

        await lockManager.AcquireAppendLockAsync(TestToken);
        var readTicket = await lockManager.AcquireReadLockAsync(TestToken);

        using var cts = new CancellationTokenSource();
        lockManager.SetCallerInformation("B: overwrite");
        var upgrade = lockManager.UpgradeToOverwriteLockAsync(cts.Token).AsTask();

        lockManager.SetCallerInformation("C: append");
        var append = lockManager.AcquireAppendLockAsync(TestToken).AsTask();
        Equal(["B: overwrite", "C: append"], lockManager.GetSuspendedCallers());

        await cts.CancelAsync();
        await ThrowsAnyAsync<OperationCanceledException>(upgrade);
        Equal(["C: append"], lockManager.GetSuspendedCallers());

        lockManager.ReleaseReadLock(readTicket);
        False(append.IsCompleted);

        lockManager.ReleaseAppendLock();
        await append.WaitAsync(TestToken);
        lockManager.ReleaseAppendLock();

        await lockManager.AcquireAppendLockAsync(TestToken);
        await lockManager.UpgradeToOverwriteLockAsync(TestToken);
        lockManager.ReleaseAppendLock();
    }

    [Fact(Timeout = TestTimeouts.Default)]
    public static async Task OverwriteExcludesOtherLockTypes()
    {
        await using var lockManager = new WriteAheadLog.LockManager();
        await lockManager.AcquireAppendLockAsync(TestToken);
        await lockManager.UpgradeToOverwriteLockAsync(TestToken);

        var append = lockManager.AcquireAppendLockAsync(TestToken).AsTask();
        var read = lockManager.AcquireReadLockAsync(TestToken).AsTask();
        var commit = lockManager.AcquireCommitLockAsync(TestToken).AsTask();
        var readBarrier = lockManager.AcquireReadBarrierAsync(TestToken).AsTask();

        False(append.IsCompleted);
        False(read.IsCompleted);
        False(commit.IsCompleted);
        False(readBarrier.IsCompleted);

        // the read was not registered when the barrier was requested, so the barrier does not wait for it (#128)
        lockManager.ReleaseAppendLock();
        await Task.WhenAll(append, read, commit, readBarrier).WaitAsync(TestToken);

        lockManager.ReleaseAppendLock();
        lockManager.ReleaseCommitLock();
        lockManager.ReleaseReadLock(read.Result);
        lockManager.ReleaseReadBarrier();
    }

    [Fact(Timeout = TestTimeouts.Default)]
    public static async Task DisposedLockManagerRejectsPendingUpgrade()
    {
        var lockManager = new WriteAheadLog.LockManager();
        await lockManager.AcquireAppendLockAsync(TestToken);
        _ = await lockManager.AcquireReadLockAsync(TestToken);

        var upgrade = lockManager.UpgradeToOverwriteLockAsync(TestToken).AsTask();
        var append = lockManager.AcquireAppendLockAsync(TestToken).AsTask();

        lockManager.Dispose(new ArithmeticException());

        await ThrowsAsync<ArithmeticException>(upgrade);
        await ThrowsAsync<ArithmeticException>(append);
        await ThrowsAnyAsync<ObjectDisposedException>(lockManager.AcquireAppendLockAsync(TestToken).AsTask);
    }

    [Fact(Timeout = TestTimeouts.Default)]
    public static async Task PreCanceledOverwriteUpgradePreservesAppendLock()
    {
        await using var lockManager = new WriteAheadLog.LockManager();
        await lockManager.AcquireAppendLockAsync(TestToken);

        var canceledToken = new CancellationToken(canceled: true);
        await ThrowsAnyAsync<OperationCanceledException>(lockManager.UpgradeToOverwriteLockAsync(canceledToken).AsTask);

        var append = lockManager.AcquireAppendLockAsync(TestToken).AsTask();
        False(append.IsCompleted);

        lockManager.ReleaseAppendLock();
        await append.WaitAsync(TestToken);
        lockManager.ReleaseAppendLock();
    }

    // #126: an append holds its lock across its persist cycle, so a compatible waiter queued behind N appenders
    // used to wait for N cycles.
    [Fact(Timeout = TestTimeouts.Default)]
    public static async Task CompatibleWaitersDoNotQueueBehindPendingAppends()
    {
        var lockManager = new WriteAheadLog.LockManager();
        var appends = new ValueTask[3];
        try
        {
            await lockManager.AcquireAppendLockAsync(TestToken);
            for (var i = 0; i < appends.Length; i++)
                appends[i] = lockManager.AcquireAppendLockAsync(TestToken);

            var read = lockManager.AcquireReadLockAsync(TestToken);
            True(read.IsCompletedSuccessfully);
            True(lockManager.TryAcquireCommitLock());
            lockManager.ReleaseCommitLock();
            True(lockManager.AcquireCommitLockAsync(TestToken).IsCompletedSuccessfully);
            lockManager.ReleaseCommitLock();
            lockManager.ReleaseReadLock(read.Result);

            True(lockManager.AcquireReadBarrierAsync(TestToken).IsCompletedSuccessfully);
            lockManager.ReleaseReadBarrier();

            DoesNotContain(appends, static task => task.IsCompleted);

            // the appends keep their order
            for (var i = 0; i < appends.Length; i++)
            {
                lockManager.ReleaseAppendLock();
                True(appends[i].IsCompletedSuccessfully);
                DoesNotContain(appends[(i + 1)..], static task => task.IsCompleted);
            }

            lockManager.ReleaseAppendLock();
        }
        finally
        {
            await CleanupAsync(lockManager, appends);
        }
    }

    // #126: a flush pass takes the persistence lock, so it waits for the queued appends and runs once after them
    // instead of between each pair of their persist cycles.
    [Fact(Timeout = TestTimeouts.Default)]
    public static async Task FlushKeepsQueueOrderBehindPendingAppends()
    {
        var lockManager = new WriteAheadLog.LockManager();
        ValueTask append1 = default, append2 = default, commit = default, append3 = default;
        Task<long> flush = null, read = null;
        try
        {
            await lockManager.AcquireAppendLockAsync(TestToken);
            append1 = lockManager.AcquireAppendLockAsync(TestToken);
            append2 = lockManager.AcquireAppendLockAsync(TestToken);
            flush = lockManager.AcquireFlushLockAsync(TestToken).AsTask();
            False(flush.IsCompleted);

            // readers and committers pass the queued flush, and a later append queues behind it
            read = lockManager.AcquireReadLockAsync(TestToken).AsTask();
            True(read.IsCompletedSuccessfully);
            commit = lockManager.AcquireCommitLockAsync(TestToken);
            True(commit.IsCompletedSuccessfully);
            append3 = lockManager.AcquireAppendLockAsync(TestToken);
            lockManager.ReleaseReadLock(read.Result);
            lockManager.ReleaseCommitLock();

            lockManager.ReleaseAppendLock();
            True(append1.IsCompletedSuccessfully);
            False(flush.IsCompleted);

            // the flush is compatible with the append that holds the lock, and is granted with the last queued append
            lockManager.ReleaseAppendLock();
            True(append2.IsCompletedSuccessfully);
            await flush.WaitAsync(TestToken);
            False(append3.IsCompleted);

            lockManager.ReleaseFlushLock(flush.Result);
            lockManager.ReleaseAppendLock();
            True(append3.IsCompletedSuccessfully);
            lockManager.ReleaseAppendLock();
        }
        finally
        {
            await CleanupAsync(lockManager, append1, append2, commit, append3);
            await ObserveFailureAsync(flush);
            await ObserveFailureAsync(read);
        }
    }

    [Fact(Timeout = TestTimeouts.Default)]
    public static async Task UpgradeWaitsForFlush()
    {
        var lockManager = new WriteAheadLog.LockManager();
        ValueTask upgrade = default;
        Task<long> flush = null;
        try
        {
            await lockManager.AcquireAppendLockAsync(TestToken);
            var flushTicket = await lockManager.AcquireFlushLockAsync(TestToken);

            upgrade = lockManager.UpgradeToOverwriteLockAsync(TestToken);
            False(upgrade.IsCompleted);

            lockManager.ReleaseFlushLock(flushTicket);
            True(upgrade.IsCompletedSuccessfully);

            // a flush that arrives during the overwrite waits for it
            flush = lockManager.AcquireFlushLockAsync(TestToken).AsTask();
            False(flush.IsCompleted);
            lockManager.ReleaseAppendLock();
            await flush.WaitAsync(TestToken);
            lockManager.ReleaseFlushLock(flush.Result);
        }
        finally
        {
            await CleanupAsync(lockManager, upgrade);
            await ObserveFailureAsync(flush);
        }
    }

    [Fact(Timeout = TestTimeouts.Default)]
    public static async Task QueuedReadersAndCommittersPassBlockedAppendAfterOverwrite()
    {
        var lockManager = new WriteAheadLog.LockManager();
        ValueTask append1 = default, append2 = default, commit = default;
        Task<long> read = null;
        try
        {
            await lockManager.AcquireAppendLockAsync(TestToken);
            await lockManager.UpgradeToOverwriteLockAsync(TestToken);

            append1 = lockManager.AcquireAppendLockAsync(TestToken);
            append2 = lockManager.AcquireAppendLockAsync(TestToken);
            read = lockManager.AcquireReadLockAsync(TestToken).AsTask();
            commit = lockManager.AcquireCommitLockAsync(TestToken);
            False(read.IsCompleted);
            False(commit.IsCompleted);

            // the release grants the first append, and the reader and committer behind the second, blocked, append
            lockManager.ReleaseAppendLock();
            True(append1.IsCompletedSuccessfully);
            await read.WaitAsync(TestToken);
            True(commit.IsCompletedSuccessfully);
            False(append2.IsCompleted);

            lockManager.ReleaseReadLock(read.Result);
            lockManager.ReleaseCommitLock();
            lockManager.ReleaseAppendLock();
            True(append2.IsCompletedSuccessfully);
            lockManager.ReleaseAppendLock();
        }
        finally
        {
            await CleanupAsync(lockManager, append1, append2, commit);
            await ObserveFailureAsync(read);
        }
    }

    [Fact(Timeout = TestTimeouts.Default)]
    public static async Task AppendsAreNotStarvedByContinuousReaders()
    {
        var lockManager = new WriteAheadLog.LockManager();
        var appends = new ValueTask[3];
        try
        {
            var firstRead = await lockManager.AcquireReadLockAsync(TestToken);
            await lockManager.AcquireAppendLockAsync(TestToken);

            for (var i = 0; i < appends.Length; i++)
                appends[i] = lockManager.AcquireAppendLockAsync(TestToken);

            // a reader always holds the lock, with overlapping readers arriving behind the queued appends
            foreach (var append in appends)
            {
                var read = lockManager.AcquireReadLockAsync(TestToken);
                True(read.IsCompletedSuccessfully);
                lockManager.ReleaseReadLock(read.Result);

                lockManager.ReleaseAppendLock();
                True(append.IsCompletedSuccessfully);
            }

            lockManager.ReleaseAppendLock();
            lockManager.ReleaseReadLock(firstRead);
        }
        finally
        {
            await CleanupAsync(lockManager, appends);
        }
    }

    [Fact(Timeout = TestTimeouts.Default)]
    public static async Task ReadersAndCommittersDoNotPassQueuedUpgrade()
    {
        var lockManager = new WriteAheadLog.LockManager();
        lockManager.TrackSuspendedCallers();
        ValueTask upgrade = default, commit = default;
        Task<long> read = null;
        try
        {
            await lockManager.AcquireAppendLockAsync(TestToken);
            var firstRead = await lockManager.AcquireReadLockAsync(TestToken);

            lockManager.SetCallerInformation("B: overwrite");
            upgrade = lockManager.UpgradeToOverwriteLockAsync(TestToken);

            lockManager.SetCallerInformation("C: read");
            read = lockManager.AcquireReadLockAsync(TestToken).AsTask();
            False(lockManager.TryAcquireCommitLock());

            lockManager.SetCallerInformation("D: commit");
            commit = lockManager.AcquireCommitLockAsync(TestToken);

            Equal(["B: overwrite", "C: read", "D: commit"], lockManager.GetSuspendedCallers());

            // the last reader leaves, so the upgrade is granted before the reader and committer queued behind it
            lockManager.ReleaseReadLock(firstRead);
            True(upgrade.IsCompletedSuccessfully);
            False(read.IsCompleted);
            False(commit.IsCompleted);

            lockManager.ReleaseAppendLock();
            await read.WaitAsync(TestToken);
            True(commit.IsCompletedSuccessfully);
            lockManager.ReleaseReadLock(read.Result);
            lockManager.ReleaseCommitLock();
        }
        finally
        {
            await CleanupAsync(lockManager, upgrade, commit);
            await ObserveFailureAsync(read);
        }
    }

    // #128: the barrier waits only for the readers registered before it was requested. A read or a flush pass that
    // arrives later observes the snapshot published before the request, so it passes the queued barrier instead of
    // parking behind it until the held read is released.
    [Fact(Timeout = TestTimeouts.Default)]
    public static async Task ReadersAndFlushPassQueuedReadBarrier()
    {
        var lockManager = new WriteAheadLog.LockManager();
        lockManager.TrackSuspendedCallers();
        ValueTask barrier = default;
        try
        {
            var heldRead = await lockManager.AcquireReadLockAsync(TestToken);

            lockManager.SetCallerInformation("B: read barrier");
            barrier = lockManager.AcquireReadBarrierAsync(TestToken);
            False(barrier.IsCompleted);

            var read = lockManager.AcquireReadLockAsync(TestToken);
            True(read.IsCompletedSuccessfully);
            var flush = lockManager.AcquireFlushLockAsync(TestToken);
            True(flush.IsCompletedSuccessfully);

            // appends and commits cannot delay the barrier, so they pass it
            True(lockManager.AcquireAppendLockAsync(TestToken).IsCompletedSuccessfully);
            True(lockManager.TryAcquireCommitLock());

            Equal(["B: read barrier"], lockManager.GetSuspendedCallers());

            // the later readers still hold the lock, but the barrier waits only for the reader registered before it
            lockManager.ReleaseReadLock(heldRead);
            True(barrier.IsCompletedSuccessfully);

            // and the readers keep passing the granted barrier
            var lateRead = lockManager.AcquireReadLockAsync(TestToken);
            True(lateRead.IsCompletedSuccessfully);

            lockManager.ReleaseReadLock(lateRead.Result);
            lockManager.ReleaseReadLock(read.Result);
            lockManager.ReleaseFlushLock(flush.Result);
            lockManager.ReleaseReadBarrier();
            lockManager.ReleaseCommitLock();
            lockManager.ReleaseAppendLock();
        }
        finally
        {
            await CleanupAsync(lockManager, barrier);
        }
    }

    [Fact(Timeout = TestTimeouts.Default)]
    public static async Task ReadBarrierWaitsForEveryPreexistingReader()
    {
        var lockManager = new WriteAheadLog.LockManager();
        ValueTask barrier = default;
        try
        {
            var read1 = await lockManager.AcquireReadLockAsync(TestToken);
            var flush = await lockManager.AcquireFlushLockAsync(TestToken);
            var read2 = await lockManager.AcquireReadLockAsync(TestToken);

            barrier = lockManager.AcquireReadBarrierAsync(TestToken);

            lockManager.ReleaseReadLock(read2);
            False(barrier.IsCompleted);
            lockManager.ReleaseFlushLock(flush);
            False(barrier.IsCompleted);
            lockManager.ReleaseReadLock(read1);
            True(barrier.IsCompletedSuccessfully);

            lockManager.ReleaseReadBarrier();
        }
        finally
        {
            await CleanupAsync(lockManager, barrier);
        }
    }

    // #128: continuous overlapping reads, with a reader always holding the lock, cannot starve the barrier,
    // because the set of the readers it waits for only shrinks.
    [Fact(Timeout = TestTimeouts.Default)]
    public static async Task ReadBarrierIsNotStarvedByOverlappingReaders()
    {
        var lockManager = new WriteAheadLog.LockManager();
        ValueTask barrier = default;
        try
        {
            var held = await lockManager.AcquireReadLockAsync(TestToken);
            barrier = lockManager.AcquireReadBarrierAsync(TestToken);
            False(barrier.IsCompleted);

            for (var i = 0; i < 100; i++)
            {
                var next = lockManager.AcquireReadLockAsync(TestToken);
                True(next.IsCompletedSuccessfully);
                lockManager.ReleaseReadLock(held);
                held = next.Result;

                // the first iteration releases the only reader registered before the request
                True(barrier.IsCompletedSuccessfully);
            }

            await barrier;
            lockManager.ReleaseReadBarrier();

            // the next barrier request waits only for the reader that holds the lock now
            barrier = lockManager.AcquireReadBarrierAsync(TestToken);
            False(barrier.IsCompleted);
            var other = lockManager.AcquireReadLockAsync(TestToken);
            True(other.IsCompletedSuccessfully);
            lockManager.ReleaseReadLock(held);
            True(barrier.IsCompletedSuccessfully);

            lockManager.ReleaseReadLock(other.Result);
            lockManager.ReleaseReadBarrier();
        }
        finally
        {
            await CleanupAsync(lockManager, barrier);
        }
    }

    // A canceled request leaves its readers in the preexisting set, so the next request waits for them too.
    [Fact(Timeout = TestTimeouts.Default)]
    public static async Task ReadBarrierWaitsForReadersOfCanceledRequest()
    {
        var lockManager = new WriteAheadLog.LockManager();
        ValueTask barrier = default;
        try
        {
            var read1 = await lockManager.AcquireReadLockAsync(TestToken);

            using (var cts = new CancellationTokenSource())
            {
                var canceled = lockManager.AcquireReadBarrierAsync(cts.Token).AsTask();
                await cts.CancelAsync();
                await ThrowsAnyAsync<OperationCanceledException>(() => canceled);
            }

            var read2 = await lockManager.AcquireReadLockAsync(TestToken);
            barrier = lockManager.AcquireReadBarrierAsync(TestToken);

            lockManager.ReleaseReadLock(read2);
            False(barrier.IsCompleted);
            lockManager.ReleaseReadLock(read1);
            True(barrier.IsCompletedSuccessfully);

            lockManager.ReleaseReadBarrier();
        }
        finally
        {
            await CleanupAsync(lockManager, barrier);
        }
    }

    // The barrier counts as a reader while it is held, so the pages it removes cannot be overwritten under it.
    [Fact(Timeout = TestTimeouts.Default)]
    public static async Task OverwriteWaitsForGrantedReadBarrier()
    {
        var lockManager = new WriteAheadLog.LockManager();
        ValueTask upgrade = default, barrier = default;
        try
        {
            await lockManager.AcquireReadBarrierAsync(TestToken);
            await lockManager.AcquireAppendLockAsync(TestToken);

            upgrade = lockManager.UpgradeToOverwriteLockAsync(TestToken);
            False(upgrade.IsCompleted);

            lockManager.ReleaseReadBarrier();
            True(upgrade.IsCompletedSuccessfully);

            // and a barrier requested during the overwrite waits for it
            barrier = lockManager.AcquireReadBarrierAsync(TestToken);
            False(barrier.IsCompleted);
            lockManager.ReleaseAppendLock();
            True(barrier.IsCompletedSuccessfully);
            lockManager.ReleaseReadBarrier();
        }
        finally
        {
            await CleanupAsync(lockManager, upgrade, barrier);
        }
    }

    [Fact(Timeout = TestTimeouts.Default)]
    public static async Task CommittersStayInOrder()
    {
        var lockManager = new WriteAheadLog.LockManager();
        ValueTask append = default, commit = default, lastCommit = default;
        try
        {
            await lockManager.AcquireAppendLockAsync(TestToken);
            append = lockManager.AcquireAppendLockAsync(TestToken);

            True(lockManager.AcquireCommitLockAsync(TestToken).IsCompletedSuccessfully);
            commit = lockManager.AcquireCommitLockAsync(TestToken);
            False(lockManager.TryAcquireCommitLock());
            lastCommit = lockManager.AcquireCommitLockAsync(TestToken);

            lockManager.ReleaseCommitLock();
            True(commit.IsCompletedSuccessfully);
            False(lastCommit.IsCompleted);
            False(append.IsCompleted);

            lockManager.ReleaseCommitLock();
            True(lastCommit.IsCompletedSuccessfully);
            lockManager.ReleaseCommitLock();

            lockManager.ReleaseAppendLock();
            True(append.IsCompletedSuccessfully);
            lockManager.ReleaseAppendLock();
        }
        finally
        {
            await CleanupAsync(lockManager, append, commit, lastCommit);
        }
    }

    // #128: a canceled append no longer holds back the flush queued behind it.
    [Fact(Timeout = TestTimeouts.Default)]
    public static async Task CanceledBlockedWaiterReleasesWaitersBehindIt()
    {
        await using var lockManager = new WriteAheadLog.LockManager();
        await lockManager.AcquireAppendLockAsync(TestToken);

        using var cts = new CancellationTokenSource();
        var append = lockManager.AcquireAppendLockAsync(cts.Token).AsTask();
        var flush = lockManager.AcquireFlushLockAsync(TestToken).AsTask();
        False(append.IsCompleted);
        False(flush.IsCompleted);

        await cts.CancelAsync();
        await ThrowsAnyAsync<OperationCanceledException>(() => append);
        var ticket = await flush.WaitAsync(TestToken);

        lockManager.ReleaseFlushLock(ticket);
        lockManager.ReleaseAppendLock();
    }

    [Fact(Timeout = TestTimeouts.Default)]
    public static async Task UpgradeAfterReaderPassedQueuedAppends()
    {
        var lockManager = new WriteAheadLog.LockManager();
        ValueTask append = default, upgrade = default;
        try
        {
            await lockManager.AcquireAppendLockAsync(TestToken);
            append = lockManager.AcquireAppendLockAsync(TestToken);
            var read = lockManager.AcquireReadLockAsync(TestToken);
            True(read.IsCompletedSuccessfully);

            // the holder of the append lock upgrades, and waits for the reader that passed the queued append
            upgrade = lockManager.UpgradeToOverwriteLockAsync(TestToken);
            False(upgrade.IsCompleted);

            lockManager.ReleaseReadLock(read.Result);
            True(upgrade.IsCompletedSuccessfully);
            False(append.IsCompleted);

            lockManager.ReleaseAppendLock();
            True(append.IsCompletedSuccessfully);
            lockManager.ReleaseAppendLock();
        }
        finally
        {
            await CleanupAsync(lockManager, append, upgrade);
        }
    }

    private static async Task CleanupAsync(WriteAheadLog.LockManager lockManager, params ValueTask[] tasks)
    {
        lockManager.Dispose(new OperationCanceledException("Test cleanup"));

        // consume every pending acquisition exactly once
        foreach (var task in tasks)
        {
            try
            {
                await task;
            }
            catch (OperationCanceledException)
            {
                // The assertion before cleanup is the test oracle.
            }
        }
    }

    private static async Task ObserveFailureAsync(Task task)
    {
        if (task is null)
            return;

        try
        {
            await task;
        }
        catch (OperationCanceledException)
        {
            // The assertion before cleanup is the test oracle.
        }
    }
}
