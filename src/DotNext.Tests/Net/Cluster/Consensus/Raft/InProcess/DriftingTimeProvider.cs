namespace DotNext.Net.Cluster.Consensus.Raft.InProcess;

/// <summary>
/// Models a local monotonic clock that runs slower than the reference clock by a constant factor.
/// </summary>
/// <remarks>
/// One local tick takes <see cref="Slowdown"/> reference ticks, and a local timer due in <c>d</c>
/// fires after <c>d * Slowdown</c> of reference time.
/// </remarks>
internal sealed class DriftingTimeProvider : TimeProvider
{
    private readonly TimeProvider reference;
    private readonly long origin;

    internal DriftingTimeProvider(TimeProvider reference, double slowdown)
    {
        this.reference = reference;
        Slowdown = slowdown;
        origin = reference.GetTimestamp();
    }

    internal double Slowdown { get; }

    public override long TimestampFrequency => reference.TimestampFrequency;

    public override long GetTimestamp()
        => origin + (long)Math.Floor((reference.GetTimestamp() - origin) / Slowdown);

    public override DateTimeOffset GetUtcNow() => reference.GetUtcNow();

    public override TimeZoneInfo LocalTimeZone => reference.LocalTimeZone;

    public override ITimer CreateTimer(TimerCallback callback, object state, TimeSpan dueTime, TimeSpan period)
        => new DriftingTimer(reference.CreateTimer(callback, state, ToReference(dueTime), ToReference(period)), this);

    private TimeSpan ToReference(TimeSpan local)
        => local == Timeout.InfiniteTimeSpan || local <= TimeSpan.Zero
            ? local
            : TimeSpan.FromTicks((long)Math.Ceiling(local.Ticks * Slowdown));

    private sealed class DriftingTimer(ITimer timer, DriftingTimeProvider owner) : ITimer
    {
        public bool Change(TimeSpan dueTime, TimeSpan period)
            => timer.Change(owner.ToReference(dueTime), owner.ToReference(period));

        public void Dispose() => timer.Dispose();

        public ValueTask DisposeAsync() => timer.DisposeAsync();
    }
}
