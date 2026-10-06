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
        await lockManager.AcquireReadLockAsync(TestToken);
        await lockManager.AcquireCommitLockAsync(TestToken);

        lockManager.SetCallerInformation("B: overwrite");
        var upgrade = lockManager.UpgradeToOverwriteLockAsync(TestToken).AsTask();

        lockManager.SetCallerInformation("C: append");
        var append = lockManager.AcquireAppendLockAsync(TestToken).AsTask();

        Equal(["B: overwrite", "C: append"], lockManager.GetSuspendedCallers());

        lockManager.ReleaseReadLock();
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
        lockManager.ReleaseReadLock();
    }

    [Fact(Timeout = TestTimeouts.Default)]
    public static async Task CanceledOverwriteUpgradeDoesNotLeakLocks()
    {
        await using var lockManager = new WriteAheadLog.LockManager();
        lockManager.TrackSuspendedCallers();

        await lockManager.AcquireAppendLockAsync(TestToken);
        await lockManager.AcquireReadLockAsync(TestToken);

        using var cts = new CancellationTokenSource();
        lockManager.SetCallerInformation("B: overwrite");
        var upgrade = lockManager.UpgradeToOverwriteLockAsync(cts.Token).AsTask();

        lockManager.SetCallerInformation("C: append");
        var append = lockManager.AcquireAppendLockAsync(TestToken).AsTask();
        Equal(["B: overwrite", "C: append"], lockManager.GetSuspendedCallers());

        await cts.CancelAsync();
        await ThrowsAnyAsync<OperationCanceledException>(upgrade);
        Equal(["C: append"], lockManager.GetSuspendedCallers());

        lockManager.ReleaseReadLock();
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

        lockManager.ReleaseAppendLock();
        await Task.WhenAll(append, read, commit).WaitAsync(TestToken);
        False(readBarrier.IsCompleted);

        lockManager.ReleaseAppendLock();
        lockManager.ReleaseCommitLock();
        lockManager.ReleaseReadLock();

        await readBarrier.WaitAsync(TestToken);
        lockManager.ReleaseReadLock();
    }

    [Fact(Timeout = TestTimeouts.Default)]
    public static async Task DisposedLockManagerRejectsPendingUpgrade()
    {
        var lockManager = new WriteAheadLog.LockManager();
        await lockManager.AcquireAppendLockAsync(TestToken);
        await lockManager.AcquireReadLockAsync(TestToken);

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

            True(lockManager.AcquireReadLockAsync(TestToken).IsCompletedSuccessfully);
            True(lockManager.TryAcquireCommitLock());
            lockManager.ReleaseCommitLock();
            True(lockManager.AcquireCommitLockAsync(TestToken).IsCompletedSuccessfully);
            lockManager.ReleaseCommitLock();
            lockManager.ReleaseReadLock();

            True(lockManager.AcquireReadBarrierAsync(TestToken).IsCompletedSuccessfully);
            lockManager.ReleaseReadLock();

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

    [Fact(Timeout = TestTimeouts.Default)]
    public static async Task QueuedReadersAndCommittersPassBlockedAppendAfterOverwrite()
    {
        var lockManager = new WriteAheadLog.LockManager();
        ValueTask append1 = default, append2 = default, read = default, commit = default;
        try
        {
            await lockManager.AcquireAppendLockAsync(TestToken);
            await lockManager.UpgradeToOverwriteLockAsync(TestToken);

            append1 = lockManager.AcquireAppendLockAsync(TestToken);
            append2 = lockManager.AcquireAppendLockAsync(TestToken);
            read = lockManager.AcquireReadLockAsync(TestToken);
            commit = lockManager.AcquireCommitLockAsync(TestToken);
            False(read.IsCompleted);
            False(commit.IsCompleted);

            // the release grants the first append, and the reader and committer behind the second, blocked, append
            lockManager.ReleaseAppendLock();
            True(append1.IsCompletedSuccessfully);
            True(read.IsCompletedSuccessfully);
            True(commit.IsCompletedSuccessfully);
            False(append2.IsCompleted);

            lockManager.ReleaseReadLock();
            lockManager.ReleaseCommitLock();
            lockManager.ReleaseAppendLock();
            True(append2.IsCompletedSuccessfully);
            lockManager.ReleaseAppendLock();
        }
        finally
        {
            await CleanupAsync(lockManager, append1, append2, read, commit);
        }
    }

    [Fact(Timeout = TestTimeouts.Default)]
    public static async Task AppendsAreNotStarvedByContinuousReaders()
    {
        var lockManager = new WriteAheadLog.LockManager();
        var appends = new ValueTask[3];
        try
        {
            await lockManager.AcquireReadLockAsync(TestToken);
            await lockManager.AcquireAppendLockAsync(TestToken);

            for (var i = 0; i < appends.Length; i++)
                appends[i] = lockManager.AcquireAppendLockAsync(TestToken);

            // a reader always holds the lock, with overlapping readers arriving behind the queued appends
            foreach (var append in appends)
            {
                True(lockManager.AcquireReadLockAsync(TestToken).IsCompletedSuccessfully);
                lockManager.ReleaseReadLock();

                lockManager.ReleaseAppendLock();
                True(append.IsCompletedSuccessfully);
            }

            lockManager.ReleaseAppendLock();
            lockManager.ReleaseReadLock();
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
        ValueTask upgrade = default, read = default, commit = default;
        try
        {
            await lockManager.AcquireAppendLockAsync(TestToken);
            await lockManager.AcquireReadLockAsync(TestToken);

            lockManager.SetCallerInformation("B: overwrite");
            upgrade = lockManager.UpgradeToOverwriteLockAsync(TestToken);

            lockManager.SetCallerInformation("C: read");
            read = lockManager.AcquireReadLockAsync(TestToken);
            False(lockManager.TryAcquireCommitLock());

            lockManager.SetCallerInformation("D: commit");
            commit = lockManager.AcquireCommitLockAsync(TestToken);

            Equal(["B: overwrite", "C: read", "D: commit"], lockManager.GetSuspendedCallers());

            // the last reader leaves, so the upgrade is granted before the reader and committer queued behind it
            lockManager.ReleaseReadLock();
            True(upgrade.IsCompletedSuccessfully);
            False(read.IsCompleted);
            False(commit.IsCompleted);

            lockManager.ReleaseAppendLock();
            True(read.IsCompletedSuccessfully);
            True(commit.IsCompletedSuccessfully);
            lockManager.ReleaseReadLock();
            lockManager.ReleaseCommitLock();
        }
        finally
        {
            await CleanupAsync(lockManager, upgrade, read, commit);
        }
    }

    [Fact(Timeout = TestTimeouts.Default)]
    public static async Task ReadersDoNotPassQueuedReadBarrier()
    {
        var lockManager = new WriteAheadLog.LockManager();
        lockManager.TrackSuspendedCallers();
        ValueTask barrier = default, read = default;
        try
        {
            await lockManager.AcquireReadLockAsync(TestToken);

            lockManager.SetCallerInformation("B: read barrier");
            barrier = lockManager.AcquireReadBarrierAsync(TestToken);

            lockManager.SetCallerInformation("C: read");
            read = lockManager.AcquireReadLockAsync(TestToken);

            // appends and commits cannot delay the barrier, so they pass it
            True(lockManager.AcquireAppendLockAsync(TestToken).IsCompletedSuccessfully);
            True(lockManager.TryAcquireCommitLock());

            Equal(["B: read barrier", "C: read"], lockManager.GetSuspendedCallers());

            // the reader behind the barrier is granted only after the barrier, as before
            lockManager.ReleaseReadLock();
            True(barrier.IsCompletedSuccessfully);
            True(read.IsCompletedSuccessfully);

            lockManager.ReleaseReadLock();
            lockManager.ReleaseReadLock();
            lockManager.ReleaseCommitLock();
            lockManager.ReleaseAppendLock();
        }
        finally
        {
            await CleanupAsync(lockManager, barrier, read);
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

    [Fact(Timeout = TestTimeouts.Default)]
    public static async Task CanceledBlockedWaiterReleasesWaitersBehindIt()
    {
        await using var lockManager = new WriteAheadLog.LockManager();
        await lockManager.AcquireReadLockAsync(TestToken);

        using var cts = new CancellationTokenSource();
        var barrier = lockManager.AcquireReadBarrierAsync(cts.Token).AsTask();
        var read = lockManager.AcquireReadLockAsync(TestToken).AsTask();
        False(read.IsCompleted);

        await cts.CancelAsync();
        await ThrowsAnyAsync<OperationCanceledException>(() => barrier);
        await read.WaitAsync(TestToken);

        lockManager.ReleaseReadLock();
        lockManager.ReleaseReadLock();
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
            True(lockManager.AcquireReadLockAsync(TestToken).IsCompletedSuccessfully);

            // the holder of the append lock upgrades, and waits for the reader that passed the queued append
            upgrade = lockManager.UpgradeToOverwriteLockAsync(TestToken);
            False(upgrade.IsCompleted);

            lockManager.ReleaseReadLock();
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
