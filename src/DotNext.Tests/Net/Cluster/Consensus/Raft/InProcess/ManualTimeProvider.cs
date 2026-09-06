using Microsoft.Extensions.Time.Testing;

namespace DotNext.Net.Cluster.Consensus.Raft.InProcess;

internal sealed class ManualTimeProvider : FakeTimeProvider
{
    public override ITimer CreateTimer(TimerCallback callback, object state, TimeSpan dueTime, TimeSpan period)
    {
        ArgumentNullException.ThrowIfNull(callback);
        return new DrainingTimer(this, callback, state, dueTime, period);
    }

    private ITimer CreateTimerCore(TimerCallback callback, TimeSpan dueTime, TimeSpan period)
        => base.CreateTimer(callback, null, dueTime, period);

    // FakeTimeProvider handles scheduling, but its timers do not await running
    // callbacks on disposal. Raft deadlines need that lifetime guarantee.
    private sealed class DrainingTimer : ITimer
    {
        private readonly Lock syncRoot = new();
        private readonly TimerCallback callback;
        private readonly object state;
        private readonly ITimer timer;
        private TaskCompletionSource drained;
        private int firing;
        private bool disposed;

        internal DrainingTimer(ManualTimeProvider owner, TimerCallback callback, object state, TimeSpan dueTime, TimeSpan period)
        {
            this.callback = callback;
            this.state = state;
            timer = owner.CreateTimerCore(Invoke, dueTime, period);
        }

        public bool Change(TimeSpan dueTime, TimeSpan period)
        {
            lock (syncRoot)
                return timer.Change(dueTime, period);
        }

        private void Invoke(object _)
        {
            lock (syncRoot)
            {
                if (disposed)
                    return;

                firing++;
            }

            try
            {
                callback(state);
            }
            finally
            {
                lock (syncRoot)
                {
                    if (--firing is 0)
                        drained?.TrySetResult();
                }
            }
        }

        public void Dispose() => _ = DisposeAsync();

        public ValueTask DisposeAsync()
        {
            ValueTask result;
            lock (syncRoot)
            {
                disposed = true;
                timer.Dispose();
                result = firing > 0
                    ? new((drained ??= new(TaskCreationOptions.RunContinuationsAsynchronously)).Task)
                    : ValueTask.CompletedTask;
            }

            return result;
        }
    }
}
