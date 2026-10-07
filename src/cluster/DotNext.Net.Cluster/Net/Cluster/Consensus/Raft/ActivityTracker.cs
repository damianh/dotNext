using System.Diagnostics;

namespace DotNext.Net.Cluster.Consensus.Raft;

/// <summary>
/// Counts the background work of a node that is runnable now, as opposed to waiting for a timer or a network response.
/// </summary>
/// <remarks>
/// This is a test seam: a deterministic scheduler must not advance a virtual clock while a node still has work to do
/// in real time (thread-pool hops, log I/O), otherwise timeouts fire against work the node never had the chance to finish.
/// The tracker is not set in production, so every hook costs a null check.
/// </remarks>
internal sealed class ActivityTracker
{
    private int count;

    /// <summary>
    /// Gets the number of activities that are in progress.
    /// </summary>
    internal int Count => Volatile.Read(in count);

    /// <summary>
    /// Invoked when the count drops to zero.
    /// </summary>
    internal Action? Idle { get; set; }

    internal void Enter() => Interlocked.Increment(ref count);

    internal void Exit()
    {
        var result = Interlocked.Decrement(ref count);
        Debug.Assert(result >= 0);

        if (result is 0)
            Idle?.Invoke();
    }

    internal void Exit(Task task)
    {
        if (task.IsCompleted)
            Exit();
        else
            task.ConfigureAwait(false).GetAwaiter().UnsafeOnCompleted(Exit);
    }

    /// <summary>
    /// Accounts a loop that waits for a signal or a timer: the loop is counted while it runs and is not counted
    /// while it waits, and whoever releases the wait counts the hop until the loop resumes.
    /// </summary>
    /// <remarks>
    /// The loop is counted from construction until <see cref="Close"/>.
    /// </remarks>
    internal sealed class Loop
    {
        private const int Closed = int.MinValue;
        private readonly ActivityTracker tracker;
        private int wakeups;

        internal Loop(ActivityTracker tracker)
        {
            this.tracker = tracker;
            tracker.Enter();
        }

        /// <summary>
        /// Must be called before the signal the loop waits for is set, and by the timer that releases the wait.
        /// </summary>
        internal void Wake()
        {
            tracker.Enter();

            // the loop has exited: nobody will release this wakeup
            if (Interlocked.Increment(ref wakeups) <= 0)
                tracker.Exit();
        }

        // The wait is armed: the loop is runnable again only when a wakeup is counted.
        internal void Park() => tracker.Exit();

        internal void Resume()
        {
            tracker.Enter();
            Consume();
        }

        // The signal was observed without waiting: the wakeups that announced it are no longer needed.
        // A wakeup for a signal that is set later keeps the event set, so the next wait completes without parking.
        internal void Consume() => Release(Interlocked.Exchange(ref wakeups, 0));

        internal void Close()
        {
            Release(Interlocked.Exchange(ref wakeups, Closed));
            tracker.Exit();
        }

        private void Release(int wakeups)
        {
            for (; wakeups > 0; wakeups--)
                tracker.Exit();
        }
    }
}
