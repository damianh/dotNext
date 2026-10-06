namespace DotNext.Threading;

public sealed class QueuedSynchronizerTests : Test
{
    [Fact(Timeout = TestTimeouts.Default)]
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

    [Fact(Timeout = TestTimeouts.Default)]
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

    [Fact(Timeout = TestTimeouts.Default)]
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

    [Fact(Timeout = TestTimeouts.Default)]
    public static async Task CallersKeepQueueOrderByDefault()
    {
        var synchronizer = new ResourceLock();
        ValueTask first = default, second = default;
        try
        {
            await synchronizer.AcquireAsync(1, TestToken);

            // the second resource is free, but the caller does not pass the suspended caller
            first = synchronizer.AcquireAsync(1, TestToken);
            second = synchronizer.AcquireAsync(2, TestToken);
            False(second.IsCompleted);

            synchronizer.Release(1);
            True(first.IsCompletedSuccessfully);
            True(second.IsCompletedSuccessfully);
        }
        finally
        {
            synchronizer.Dispose();
            await ObserveAsync(first, second);
        }
    }

    [Fact(Timeout = TestTimeouts.Default)]
    public static async Task CallerPassesSuspendedCallersThatItCannotDelay()
    {
        var synchronizer = new OvertakingResourceLock();
        ValueTask a = default, b = default, d = default;
        try
        {
            await synchronizer.AcquireAsync(1, TestToken);
            await synchronizer.AcquireAsync(2, TestToken);

            a = synchronizer.AcquireAsync(1, TestToken);
            b = synchronizer.AcquireAsync(2, TestToken);
            True(synchronizer.AcquireAsync(3, TestToken).IsCompletedSuccessfully);
            d = synchronizer.AcquireAsync(1, TestToken);

            // the drain passes the blocked caller of the first resource, but the callers of that resource stay in order
            synchronizer.Release(2);
            False(a.IsCompleted);
            True(b.IsCompletedSuccessfully);
            False(d.IsCompleted);

            synchronizer.Release(1);
            True(a.IsCompletedSuccessfully);
            False(d.IsCompleted);

            synchronizer.Release(1);
            True(d.IsCompletedSuccessfully);
        }
        finally
        {
            synchronizer.Dispose();
            await ObserveAsync(a, b, d);
        }
    }

    [Fact(Timeout = TestTimeouts.Default)]
    public static async Task DrainStopsWhenTooManyCallersArePassed()
    {
        const int resources = 10;
        var synchronizer = new OvertakingResourceLock();
        var waiters = new ValueTask[resources];
        try
        {
            for (var i = 0; i < resources; i++)
                await synchronizer.AcquireAsync(i, TestToken);

            for (var i = 0; i < resources; i++)
                waiters[i] = synchronizer.AcquireAsync(i, TestToken);

            // the drain records eight blocked callers at most, and then keeps the order of the queue
            synchronizer.Release(resources - 1);
            DoesNotContain(waiters, static task => task.IsCompleted);

            synchronizer.Release(resources - 2);
            True(waiters[resources - 2].IsCompletedSuccessfully);
            True(waiters[resources - 1].IsCompletedSuccessfully);
            DoesNotContain(waiters[..^2], static task => task.IsCompleted);
        }
        finally
        {
            synchronizer.Dispose();
            await ObserveAsync(waiters);
        }
    }

    private static async Task ObserveAsync(params ValueTask[] tasks)
    {
        foreach (var task in tasks)
        {
            try
            {
                await task;
            }
            catch (ObjectDisposedException)
            {
                // The assertion before cleanup is the test oracle.
            }
        }
    }

    // Each context is an independent resource.
    private class ResourceLock : QueuedSynchronizer<int>
    {
        private readonly HashSet<int> resources = new();

        protected sealed override bool CanAcquire(int resource) => !resources.Contains(resource);

        protected sealed override void AcquireCore(int resource) => resources.Add(resource);

        protected sealed override void ReleaseCore(int resource) => resources.Remove(resource);

        public new ValueTask AcquireAsync(int resource, CancellationToken token)
            => base.AcquireAsync(resource, token);

        public new void Release(int resource) => base.Release(resource);
    }

    private sealed class OvertakingResourceLock : ResourceLock
    {
        protected override bool CanOvertake(int resource, int suspended) => resource != suspended;
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