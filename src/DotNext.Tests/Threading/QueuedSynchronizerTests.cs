namespace DotNext.Threading;

public sealed class QueuedSynchronizerTests : Test
{
    [Fact]
    public static async Task ThrowOnAcquisitionAsync()
    {
        await using var synchronizer = new MySynchronizer();
        await ThrowsAsync<ArithmeticException>(synchronizer.ThrowAsync(TestToken).AsTask);
        False(synchronizer.TryAcquire());
    }
    
    private sealed class MySynchronizer : QueuedSynchronizer<bool>
    {
        public ValueTask ThrowAsync(CancellationToken token = default)
            => AcquireAsync(context: false, token);

        public bool TryAcquire() => TryAcquire(context: false);

        protected override bool CanAcquire(bool context) => context;

        protected override ExceptionFactory GetAcquisitionException(bool canAcquire)
            => canAcquire ? null : ExceptionFactory.Of<ArithmeticException>();
    }

    [Fact]
    public static async Task ResumeReadLockAsync()
    {
        using var synchronizer = new CustomReaderWriterLock();
        await synchronizer.EnterReadLockAsync(TestToken);

        using var cts = CancellationTokenSource.CreateLinkedTokenSource(TestToken);
        var writeLockTask = synchronizer.EnterWriteLockAsync(cts.Token).AsTask();
        False(writeLockTask.IsCompleted);

        var readLockTask = synchronizer.EnterReadLockAsync(TestToken).AsTask();

        await cts.CancelAsync();
        await readLockTask;
    }

    [Fact]
    public static async Task PriorityAcquisitionPrecedesQueuedCallers()
    {
        await using var synchronizer = new PriorityLock();
        synchronizer.TrackSuspendedCallers();

        await synchronizer.AcquireAsync("holder", TestToken);
        var first = synchronizer.AcquireAsync("first", TestToken).AsTask();
        var priority = synchronizer.AcquirePriorityAsync("priority", TestToken).AsTask();
        var last = synchronizer.AcquireAsync("last", TestToken).AsTask();

        Equal(["priority", "first", "last"], synchronizer.GetSuspendedCallers());

        synchronizer.Release();
        await priority.WaitAsync(TestToken);
        False(first.IsCompleted);
        False(last.IsCompleted);

        synchronizer.Release();
        await first.WaitAsync(TestToken);
        False(last.IsCompleted);

        synchronizer.Release();
        await last.WaitAsync(TestToken);
        synchronizer.Release();

        await synchronizer.AcquireAsync("after", TestToken);
        synchronizer.Release();
    }
    
    private sealed class CustomReaderWriterLock : QueuedSynchronizer<bool>
    {
        private uint readLocks;
        private bool writeLockTaken;

        protected override bool CanAcquire(bool writeLock)
        {
            if (writeLockTaken)
                return false;

            return !writeLock || readLocks is 0U;
        }

        public ValueTask EnterWriteLockAsync(CancellationToken token)
            => AcquireAsync(true, token);

        public ValueTask EnterReadLockAsync(CancellationToken token)
            => AcquireAsync(false, token);

        protected override void AcquireCore(bool writeLock)
        {
            if (writeLock)
            {
                writeLockTaken = true;
            }
            else
            {
                readLocks++;
            }
        }

        protected override void ReleaseCore(bool writeLock)
        {
            if (writeLock)
            {
                writeLockTaken = false;
            }
            else
            {
                readLocks--;
            }
        }
    }

    private sealed class PriorityLock : QueuedSynchronizer<string>
    {
        private bool held;

        protected override bool CanAcquire(string context) => !held;

        protected override void AcquireCore(string context) => held = true;

        protected override void ReleaseCore(string context) => held = false;

        public new ValueTask AcquireAsync(string caller, CancellationToken token)
        {
            SetCallerInformation(caller);
            return base.AcquireAsync(caller, token);
        }

        public new ValueTask AcquirePriorityAsync(string caller, CancellationToken token)
        {
            SetCallerInformation(caller);
            return base.AcquirePriorityAsync(caller, token);
        }

        public new void Release() => Release(string.Empty);
    }
}