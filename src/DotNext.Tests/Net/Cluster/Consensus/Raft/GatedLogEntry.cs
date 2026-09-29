using System.Diagnostics.CodeAnalysis;

namespace DotNext.Net.Cluster.Consensus.Raft;

using IO;

/// <summary>
/// An entry without length or memory representation, so the WAL writes it while holding the append lock.
/// The write blocks until <see cref="Release"/> is called, which lets a test queue other appends behind it.
/// </summary>
[ExcludeFromCodeCoverage]
internal sealed class GatedLogEntry : IRaftLogEntry
{
    private readonly TaskCompletionSource started = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private readonly TaskCompletionSource gate = new(TaskCreationOptions.RunContinuationsAsynchronously);

    public required long Term { get; init; }

    /// <summary>
    /// Completes when the log has begun writing the payload, that is, when it holds the append lock.
    /// </summary>
    internal Task Started => started.Task;

    internal void Release() => gate.TrySetResult();

    bool IDataTransferObject.IsReusable => true;

    long? IDataTransferObject.Length => null;

    async ValueTask IDataTransferObject.WriteToAsync<TWriter>(TWriter writer, CancellationToken token)
    {
        started.TrySetResult();
        await gate.Task.ConfigureAwait(false);
        await writer.WriteAsync(new byte[] { 1, 2, 3 }, token: token).ConfigureAwait(false);
    }
}
