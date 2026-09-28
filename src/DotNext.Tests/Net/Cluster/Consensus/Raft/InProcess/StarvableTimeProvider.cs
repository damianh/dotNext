namespace DotNext.Net.Cluster.Consensus.Raft.InProcess;

/// <summary>
/// Models a node whose timer callbacks run late, for example under thread-pool starvation.
/// </summary>
/// <remarks>
/// The clock is read from the underlying provider without delay. While <see cref="IsStarved"/> is set,
/// callbacks of timers created through this provider are queued instead of invoked; <see cref="Resume"/>
/// runs the queued callbacks of timers that are still alive.
/// </remarks>
internal sealed class StarvableTimeProvider(TimeProvider clock) : TimeProvider
{
    private readonly Lock syncRoot = new();
    private readonly Queue<StarvableTimer> queued = new();
    private bool starved;

    internal bool IsStarved
    {
        get
        {
            lock (syncRoot)
                return starved;
        }
    }

    internal int QueuedCallbacks
    {
        get
        {
            lock (syncRoot)
                return queued.Count;
        }
    }

    internal void Starve()
    {
        lock (syncRoot)
            starved = true;
    }

    internal void Resume()
    {
        StarvableTimer[] callbacks;
        lock (syncRoot)
        {
            starved = false;
            callbacks = queued.ToArray();
            queued.Clear();
        }

        foreach (var timer in callbacks)
            timer.InvokeIfAlive();
    }

    public override long TimestampFrequency => clock.TimestampFrequency;

    public override long GetTimestamp() => clock.GetTimestamp();

    public override DateTimeOffset GetUtcNow() => clock.GetUtcNow();

    public override TimeZoneInfo LocalTimeZone => clock.LocalTimeZone;

    public override ITimer CreateTimer(TimerCallback callback, object state, TimeSpan dueTime, TimeSpan period)
    {
        ArgumentNullException.ThrowIfNull(callback);

        var timer = new StarvableTimer(this, callback, state);
        timer.Start(clock, dueTime, period);
        return timer;
    }

    private bool TryQueue(StarvableTimer timer)
    {
        lock (syncRoot)
        {
            if (!starved)
                return false;

            queued.Enqueue(timer);
            return true;
        }
    }

    private sealed class StarvableTimer(StarvableTimeProvider owner, TimerCallback callback, object state) : ITimer
    {
        private ITimer timer;
        private volatile bool disposed;

        internal void Start(TimeProvider clock, TimeSpan dueTime, TimeSpan period)
            => timer = clock.CreateTimer(static self => ((StarvableTimer)self).OnTick(), this, dueTime, period);

        private void OnTick()
        {
            if (!disposed && !owner.TryQueue(this))
                callback(state);
        }

        internal void InvokeIfAlive()
        {
            if (!disposed)
                callback(state);
        }

        public bool Change(TimeSpan dueTime, TimeSpan period) => timer.Change(dueTime, period);

        public void Dispose()
        {
            disposed = true;
            timer.Dispose();
        }

        public ValueTask DisposeAsync()
        {
            disposed = true;
            return timer.DisposeAsync();
        }
    }
}
