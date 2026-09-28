using System.Diagnostics.CodeAnalysis;

namespace DotNext.Net.Cluster.Consensus.Raft;

using Diagnostics;

internal partial class LeaderState<TMember>
{
    private sealed class Lease : CancellationTokenSource
    {
        internal new readonly CancellationToken Token; // cached to avoid ObjectDisposedException

        // The lease is valid while the monotonic clock of the time provider is below this value.
        // The timer only notifies about expiration: a timer callback can be delayed arbitrarily.
        private long deadline;

        internal Lease(TimeProvider timeProvider, long deadline = long.MaxValue)
            : base(Timeout.InfiniteTimeSpan, timeProvider)
        {
            Token = base.Token;
            this.deadline = deadline;
        }

        internal bool IsExpired(TimeProvider timeProvider)
            => timeProvider.GetTimestamp() >= Volatile.Read(in deadline);

        internal bool TryRenew(TimeSpan leaseTime, long deadline)
        {
            try
            {
                // refreshes the internal timer without invalidating cancellation subscriptions
                if (leaseTime <= TimeSpan.Zero)
                {
                    Cancel(throwOnFirstException: false);
                    return true;
                }

                Volatile.Write(ref this.deadline, deadline);
                CancelAfter(leaseTime);
            }
            catch (ObjectDisposedException)
            {
                return false;
            }

            return Token.IsCancellationRequested is false;
        }

        internal void Expire()
        {
            try
            {
                Cancel(throwOnFirstException: false);
            }
            catch (ObjectDisposedException)
            {
                // the lease is destroyed concurrently
            }
        }
    }

    private readonly TimeSpan maxLease;

    // Concurrency profile: multiple readers, single writer
    [SuppressMessage("Usage", "CA2213", Justification = "Disposed using DestroyLease() method")]
    private volatile Lease? lease; // null if disposed

    private void RenewLease(TimeSpan elapsed, long deadline)
    {
        if (lease is { } currentLease && currentLease.TryRenew(elapsed = maxLease - elapsed, deadline) is false)
        {
            var newLease = new Lease(TimeProvider, deadline);
            if (ReferenceEquals(Interlocked.CompareExchange(ref lease, newLease, currentLease), currentLease))
            {
                newLease.CancelAfter(elapsed);
            }
            else
            {
                newLease.Dispose();
            }
        }
    }
    
    private double RenewLease(Timestamp startTime)
    {
        // The elapsed time is measured at or after this point, so the computed deadline
        // never exceeds the start of the round plus the maximum lease duration.
        var now = TimeProvider.GetTimestamp();
        var elapsedTicks = startTime.GetElapsedTicks(TimeProvider, out startTime);
        var elapsed = TimeSpan.FromSeconds((double)elapsedTicks / TimeProvider.TimestampFrequency);
        var leaseTicks = (long)(maxLease.TotalSeconds * TimeProvider.TimestampFrequency);
        RenewLease(elapsed, now - elapsedTicks + leaseTicks);
        UpdateLeaderStickiness(startTime);
        return elapsed.TotalMilliseconds;
    }

    private void DestroyLease()
    {
        if (Interlocked.Exchange(ref lease, null) is { } disposable)
        {
            try
            {
                disposable.Cancel(throwOnFirstException: false);
            }
            finally
            {
                disposable.Dispose();
            }
        }
    }

    internal bool TryGetLeaseToken(out CancellationToken token)
    {
        if (lease is { } tokenSource)
        {
            // Do not rely on the timer: its callback can run late, e.g. under thread pool starvation.
            if (tokenSource.IsExpired(TimeProvider))
                tokenSource.Expire();

            token = tokenSource.Token;
            return true;
        }

        token = new(canceled: true);
        return false;
    }
}