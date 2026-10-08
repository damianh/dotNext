using System.Diagnostics;

namespace DotNext.Net.Cluster.Consensus.Raft;

using Threading;

/// <summary>
/// Counts the background work of a node that is runnable now, as opposed to waiting for a timer or a network response.
/// </summary>
/// <remarks>
/// This is a test seam: a deterministic scheduler must not advance a virtual clock while a node still has work to do
/// in real time (thread-pool hops, log I/O), otherwise timeouts fire against work the node never had the chance to finish.
/// The tracker is not set in production, so every hook costs a null check.
/// A unit of work that waits for an outbound request is the only one waiting for it: the transport may stop counting
/// it while the request is held, and must count it again before the response is delivered.
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
    /// Accounts a loop that waits for a signal: the loop is counted while it runs and is not counted while it waits,
    /// and the signal that releases the wait counts the hop until the loop resumes.
    /// </summary>
    /// <remarks>
    /// The loop is counted from construction until <see cref="Close"/>.
    /// </remarks>
    internal sealed class Loop
    {
        private readonly System.Threading.Lock syncRoot = new();
        private readonly ActivityTracker tracker;
        private object? parkedOn; // the signal the loop waits for, null if it runs
        private object? reservation; // the signal that counts the hop until the loop resumes

        internal Loop(ActivityTracker tracker)
        {
            this.tracker = tracker;
            tracker.Enter();
        }

        /// <summary>
        /// Sets the signal: if the loop waits for it, the hop until the loop resumes is counted.
        /// </summary>
        /// <remarks>
        /// The wait is checked and the signal is set atomically, so a signal cannot be missed by a loop
        /// that is about to wait, nor counted for a wait it doesn't release.
        /// </remarks>
        /// <param name="target">The signal the loop waits for.</param>
        /// <param name="signal">Sets the signal; returns <see langword="false"/> if it doesn't release the wait.</param>
        /// <param name="state">The state passed to <paramref name="signal"/>.</param>
        /// <returns>The result of <paramref name="signal"/>.</returns>
        internal bool Signal<TState>(object target, Func<TState, bool> signal, TState state)
        {
            lock (syncRoot)
            {
                // the signal may resume the loop synchronously, which consumes the reservation
                var reserved = reservation is null && ReferenceEquals(parkedOn, target);
                if (reserved)
                {
                    tracker.Enter();
                    reservation = target;
                }

                var released = false;
                try
                {
                    released = signal(state);
                }
                finally
                {
                    if (reserved && !released && ReferenceEquals(reservation, target))
                    {
                        reservation = null;
                        tracker.Exit();
                    }
                }

                return released;
            }
        }

        // Returns false if the loop doesn't need to wait. The completion is checked under the lock,
        // so a signal either completes the wait before this check or observes the parked loop.
        private bool TryPark<TWait>(object target, in TWait wait, Func<TWait, bool> isCompleted)
        {
            lock (syncRoot)
            {
                if (isCompleted(wait))
                    return false;

                parkedOn = target;
            }

            tracker.Exit();
            return true;
        }

        private void Resume()
        {
            tracker.Enter();
            lock (syncRoot)
            {
                parkedOn = null;
                if (reservation is not null)
                {
                    reservation = null;
                    tracker.Exit();
                }
            }
        }

        private async ValueTask<T> ResumeAsync<T>(ValueTask<T> wait)
        {
            try
            {
                return await wait.ConfigureAwait(false);
            }
            finally
            {
                Resume();
            }
        }

        private async ValueTask ResumeAsync(ValueTask wait)
        {
            try
            {
                await wait.ConfigureAwait(false);
            }
            finally
            {
                Resume();
            }
        }

        internal void Close()
        {
            lock (syncRoot)
            {
                parkedOn = null;
                if (reservation is not null)
                {
                    reservation = null;
                    tracker.Exit();
                }
            }

            tracker.Exit();
        }

        /// <summary>
        /// Waits for the signal; the loop is not counted while it waits.
        /// </summary>
        /// <param name="loop">The loop, or <see langword="null"/> if activity tracking is disabled.</param>
        /// <param name="target">The signal, setters must use <see cref="Signal{TState}"/>.</param>
        /// <param name="wait">The wait for the signal.</param>
        internal static ValueTask<T> WaitAsync<T>(Loop? loop, object target, ValueTask<T> wait)
            => loop is not null && loop.TryPark(target, in wait, static wait => wait.IsCompleted) ? loop.ResumeAsync(wait) : wait;

        /// <inheritdoc cref="WaitAsync{T}(Loop?, object, ValueTask{T})"/>
        internal static ValueTask WaitAsync(Loop? loop, object target, ValueTask wait)
            => loop is not null && loop.TryPark(target, in wait, static wait => wait.IsCompleted) ? loop.ResumeAsync(wait) : wait;

        /// <summary>
        /// Sets the event the loop waits for.
        /// </summary>
        internal static void Set(Loop? loop, AsyncAutoResetEvent signal)
        {
            if (loop is null)
                signal.Set();
            else
                loop.Signal(signal, static signal => signal.Set() | true, signal);
        }
    }

    /// <summary>
    /// Accounts a caller that starts requests and observes their completions one by one: each request is counted from
    /// its start until the caller observes it, so the caller is not counted while it waits for the next completion.
    /// </summary>
    /// <remarks>
    /// The caller must be counted when it starts, observes, or abandons the requests.
    /// The cancellation of the wait counts the hop until the caller resumes, the same way a request does.
    /// </remarks>
    internal sealed class Requests
    {
        private readonly System.Threading.Lock syncRoot = new();
        private readonly ActivityTracker tracker;
        private readonly HashSet<Task> unobserved = [];
        private readonly CancellationToken token;
        private readonly CancellationTokenRegistration registration;
        private bool parked, reserved;

        // Must be created before the wait registers the token, so the reservation is made after the wait is canceled
        // (callbacks run in the reverse order).
        internal Requests(ActivityTracker tracker, CancellationToken token)
        {
            this.tracker = tracker;
            this.token = token;
            registration = token.UnsafeRegister(static requests => ((Requests)requests!).OnCanceled(), this);
        }

        private void OnCanceled()
        {
            lock (syncRoot)
            {
                if (parked && !reserved)
                {
                    tracker.Enter();
                    reserved = true;
                }
            }
        }

        internal static Task<T> Start<T, TArg>(Requests? requests, Func<TArg, Task<T>> start, TArg arg)
        {
            if (requests is null)
                return start(arg);

            requests.tracker.Enter();
            Task<T> task;
            try
            {
                task = start(arg);
            }
            catch
            {
                requests.tracker.Exit();
                throw;
            }

            requests.unobserved.Add(task);
            return task;
        }

        internal void Observe(Task task)
        {
            if (unobserved.Remove(task))
                tracker.Exit();
        }

        // The wait is released by a request that completes, which is counted until it's observed, or by the cancellation.
        internal static ValueTask<T> WaitAsync<T>(Requests? requests, ValueTask<T> wait)
            => requests is not null && requests.TryPark(in wait) ? requests.ResumeAsync(wait) : wait;

        private bool TryPark<T>(in ValueTask<T> wait)
        {
            lock (syncRoot)
            {
                if (wait.IsCompleted || token.IsCancellationRequested)
                    return false;

                parked = true;
            }

            tracker.Exit();
            return true;
        }

        private async ValueTask<T> ResumeAsync<T>(ValueTask<T> wait)
        {
            try
            {
                return await wait.ConfigureAwait(false);
            }
            finally
            {
                tracker.Enter();
                lock (syncRoot)
                {
                    parked = false;
                    if (reserved)
                    {
                        reserved = false;
                        tracker.Exit();
                    }
                }
            }
        }

        // The caller doesn't observe the remaining requests: each one is counted until it completes.
        internal void Abandon()
        {
            registration.Dispose();

            foreach (var task in unobserved)
                tracker.Exit(task);

            unobserved.Clear();
        }
    }
}
