using System.Net.Sockets;
using System.Runtime.ExceptionServices;

namespace DotNext.Net.Cluster.Consensus.Raft;

/// <summary>
/// Represents the payload of a request received from a remote peer, such as the log entries of AppendEntries
/// or the snapshot of InstallSnapshot.
/// </summary>
/// <remarks>
/// A failure of the payload source (the peer disconnects or is too slow, or the payload ends before its declared length)
/// costs that request only. At the payload read boundary, and only there, the failure is reported as cancellation of
/// <see cref="Token"/>, so the persistent state rolls back the partial write as it does for any other cancellation.
/// Failures of the storage that receives the payload are not converted, so the persistent state still fails closed on them.
/// The request handler restores the original failure with <see cref="ThrowIfSourceFailed"/>.
/// This file is shared with DotNext.AspNetCore.Cluster.
/// </remarks>
internal sealed class PayloadSourceScope : IDisposable
{
    private readonly CancellationTokenSource source;
    private volatile ExceptionDispatchInfo? sourceFailure;

    internal PayloadSourceScope(CancellationToken token)
        => source = CancellationTokenSource.CreateLinkedTokenSource(token);

    /// <summary>
    /// Gets the token to be passed to the operation that consumes the payload.
    /// </summary>
    internal CancellationToken Token => source.Token;

    /// <summary>
    /// Determines whether the exception thrown by the payload source is a failure of the source rather than a cancellation.
    /// </summary>
    /// <remarks>
    /// <see cref="IOException"/> covers transport errors, <see cref="EndOfStreamException"/>, and Kestrel's
    /// <c>BadHttpRequestException</c> and <c>ConnectionResetException</c>.
    /// </remarks>
    internal static bool IsSourceFailure(Exception e) => e is IOException or SocketException;

    /// <summary>
    /// Reports the failure of the payload source as cancellation of the request.
    /// </summary>
    /// <param name="e">The failure of the payload source.</param>
    /// <returns>The exception to throw instead of <paramref name="e"/>.</returns>
    internal OperationCanceledException Fail(Exception e)
    {
        Interlocked.CompareExchange(ref sourceFailure, ExceptionDispatchInfo.Capture(e), comparand: null);
        try
        {
            source.Cancel();
        }
        catch (ObjectDisposedException)
        {
            // the request is completed already
        }

        return new(e.Message, e, source.Token);
    }

    internal ValueTask GuardAsync(ValueTask task)
        => task.IsCompletedSuccessfully ? task : GuardSlowAsync(task);

    private async ValueTask GuardSlowAsync(ValueTask task)
    {
        try
        {
            await task.ConfigureAwait(false);
        }
        catch (Exception e) when (IsSourceFailure(e))
        {
            throw Fail(e);
        }
    }

    internal ValueTask<T> GuardAsync<T>(ValueTask<T> task)
        => task.IsCompletedSuccessfully ? task : GuardSlowAsync(task);

    private async ValueTask<T> GuardSlowAsync<T>(ValueTask<T> task)
    {
        try
        {
            return await task.ConfigureAwait(false);
        }
        catch (Exception e) when (IsSourceFailure(e))
        {
            throw Fail(e);
        }
    }

    /// <summary>
    /// Rethrows the original failure of the payload source, if any.
    /// </summary>
    internal void ThrowIfSourceFailed() => sourceFailure?.Throw();

    public void Dispose() => source.Dispose();
}