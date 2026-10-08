namespace DotNext.Net.Cluster.Consensus.Raft;

using Threading;

internal static class RaftTimer
{
    // If loop is not null, the wait is not counted as activity. Signal setters must use ActivityTracker.Loop.Set().
    internal static async ValueTask<bool> WaitAsync(
        AsyncAutoResetEvent source,
        TimeSpan timeout,
        TimeProvider timeProvider,
        CancellationToken token,
        ActivityTracker.Loop? loop = null)
    {
        if (timeout == TimeSpan.Zero)
            return await source.WaitAsync(timeout, token).ConfigureAwait(false);

        using var timeoutSource = new CancellationTokenSource(timeout, timeProvider);
        using var linkedSource = loop is null
            ? CancellationTokenSource.CreateLinkedTokenSource(token, timeoutSource.Token)
            : CancellationTokenSource.CreateLinkedTokenSource(token);

        // The timeout releases the wait as a signal, so the hop until the loop resumes is counted.
        using var timeoutRegistration = loop is null
            ? default
            : timeoutSource.Token.UnsafeRegister(
                static state =>
                {
                    var (loop, source, linkedSource) = ((ActivityTracker.Loop, AsyncAutoResetEvent, CancellationTokenSource))state!;
                    loop.Signal(source, static linkedSource =>
                    {
                        linkedSource.Cancel();
                        return true;
                    }, linkedSource);
                },
                (loop, source, linkedSource));

        try
        {
            await ActivityTracker.Loop.WaitAsync(loop, source, source.WaitAsync(linkedSource.Token)).ConfigureAwait(false);
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
