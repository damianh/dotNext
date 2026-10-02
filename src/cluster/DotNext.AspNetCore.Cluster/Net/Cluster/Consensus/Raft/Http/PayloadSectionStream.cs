namespace DotNext.Net.Cluster.Consensus.Raft.Http;

/// <summary>
/// Represents the body of a multipart section as the payload source of a log entry.
/// </summary>
/// <remarks>
/// The multipart parser reports a malformed or truncated body with its own exceptions, after <see cref="PayloadReader"/>
/// has read the request body successfully. Only the reads from the section are guarded, so a failure of the storage
/// that receives the payload is not converted (see <see cref="PayloadSourceScope"/>).
/// </remarks>
internal sealed class PayloadSectionStream(Stream section, PayloadSourceScope payload) : Stream
{
    internal static bool IsSourceFailure(Exception e)
        => PayloadSourceScope.IsSourceFailure(e) || e is InvalidDataException;

    public override bool CanRead => section.CanRead;

    public override bool CanSeek => section.CanSeek;

    public override bool CanWrite => false;

    public override long Length => section.Length;

    public override long Position
    {
        get => section.Position;
        set => section.Position = value;
    }

    public override long Seek(long offset, SeekOrigin origin) => section.Seek(offset, origin);

    public override int Read(byte[] buffer, int offset, int count) => Read(buffer.AsSpan(offset, count));

    public override int Read(Span<byte> buffer)
    {
        try
        {
            return section.Read(buffer);
        }
        catch (Exception e) when (IsSourceFailure(e))
        {
            throw payload.Fail(e);
        }
    }

    public override Task<int> ReadAsync(byte[] buffer, int offset, int count, CancellationToken token)
        => ReadAsync(buffer.AsMemory(offset, count), token).AsTask();

    public override async ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken token = default)
    {
        try
        {
            return await section.ReadAsync(buffer, token).ConfigureAwait(false);
        }
        catch (Exception e) when (IsSourceFailure(e))
        {
            throw payload.Fail(e);
        }
    }

    public override void Flush()
    {
    }

    public override void SetLength(long value) => throw new NotSupportedException();

    public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
}
