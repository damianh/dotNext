using System.IO.Pipelines;

namespace DotNext.Net.Cluster.Consensus.Raft.Http;

/// <summary>
/// Represents the request body as the payload source of AppendEntries or InstallSnapshot.
/// </summary>
/// <remarks>
/// A failure to read the body, such as a disconnect of the peer or Kestrel's <c>MinRequestBodyDataRate</c>,
/// is reported as cancellation of the request (see <see cref="PayloadSourceScope"/>).
/// </remarks>
internal sealed class PayloadReader(PipeReader body, PayloadSourceScope payload) : PipeReader
{
    public override void AdvanceTo(SequencePosition consumed)
        => body.AdvanceTo(consumed);

    public override void AdvanceTo(SequencePosition consumed, SequencePosition examined)
        => body.AdvanceTo(consumed, examined);

    public override void CancelPendingRead()
        => body.CancelPendingRead();

    public override void Complete(Exception? exception = null)
        => body.Complete(exception);

    public override ValueTask CompleteAsync(Exception? exception = null)
        => body.CompleteAsync(exception);

    public override ValueTask<ReadResult> ReadAsync(CancellationToken token = default)
        => payload.GuardAsync(body.ReadAsync(Bound(token)));

    protected override ValueTask<ReadResult> ReadAtLeastAsyncCore(int minimumSize, CancellationToken token)
        => payload.GuardAsync(body.ReadAtLeastAsync(minimumSize, Bound(token)));

    // A reader that doesn't pass its token, such as the one that skips a payload or reads the framing,
    // must not wait for the peer beyond the lifetime of the request
    private CancellationToken Bound(CancellationToken token)
        => token.CanBeCanceled ? token : payload.Token;

    public override bool TryRead(out ReadResult result)
    {
        try
        {
            return body.TryRead(out result);
        }
        catch (Exception e) when (PayloadSourceScope.IsSourceFailure(e))
        {
            throw payload.Fail(e);
        }
    }
}