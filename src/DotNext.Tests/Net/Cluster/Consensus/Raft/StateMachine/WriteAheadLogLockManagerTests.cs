namespace DotNext.Net.Cluster.Consensus.Raft.StateMachine;

[Collection(TestCollections.WriteAheadLog)]
public sealed class WriteAheadLogLockManagerTests : Test
{
    [Fact]
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

    [Fact]
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

    [Fact]
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

    [Fact]
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

    [Fact]
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

    [Fact]
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
