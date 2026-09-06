namespace DotNext.Net.Cluster.Consensus.Raft;

using Threading;

internal static class RaftTimer
{
    internal static async ValueTask<bool> WaitAsync(
        AsyncAutoResetEvent source,
        TimeSpan timeout,
        TimeProvider timeProvider,
        CancellationToken token)
    {
        if (timeout == TimeSpan.Zero)
            return await source.WaitAsync(timeout, token).ConfigureAwait(false);

        using var timeoutSource = new CancellationTokenSource(timeout, timeProvider);
        using var linkedSource = CancellationTokenSource.CreateLinkedTokenSource(token, timeoutSource.Token);

        try
        {
            await source.WaitAsync(linkedSource.Token).ConfigureAwait(false);
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
