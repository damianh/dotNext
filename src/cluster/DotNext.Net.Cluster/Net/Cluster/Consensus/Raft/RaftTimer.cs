namespace DotNext.Net.Cluster.Consensus.Raft;

using Threading;

internal static class RaftTimer
{
    // If loop is not null, the wait is not counted as activity once it is armed. The timeout counts as a wakeup,
    // signal setters must call ActivityTracker.Loop.Wake() before setting the signal.
    internal static async ValueTask<bool> WaitAsync(
        AsyncAutoResetEvent source,
        TimeSpan timeout,
        TimeProvider timeProvider,
        CancellationToken token,
        ActivityTracker.Loop? loop = null)
    {
        if (timeout == TimeSpan.Zero)
        {
            var signaled = await source.WaitAsync(timeout, token).ConfigureAwait(false);
            if (signaled)
                loop?.Consume();

            return signaled;
        }

        using var timeoutSource = new CancellationTokenSource(timeout, timeProvider);
        using var linkedSource = CancellationTokenSource.CreateLinkedTokenSource(token, timeoutSource.Token);
        using var wakeup = loop is null
            ? default
            : timeoutSource.Token.UnsafeRegister(static loop => ((ActivityTracker.Loop)loop!).Wake(), loop);

        try
        {
            var task = source.WaitAsync(linkedSource.Token);
            if (loop is null || task.IsCompleted)
            {
                loop?.Consume();
                await task.ConfigureAwait(false);
            }
            else
            {
                loop.Park();
                try
                {
                    await task.ConfigureAwait(false);
                }
                finally
                {
                    loop.Resume();
                }
            }

            return true;
        }
        catch (OperationCanceledException) when (token.IsCancellationRequested)
        {
            throw new OperationCanceledException(token);
        }
        catch (OperationCanceledException) when (timeoutSource.IsCancellationRequested)
        {
            token.ThrowIfCancellationRequested();
            return false;
        }
    }
}
