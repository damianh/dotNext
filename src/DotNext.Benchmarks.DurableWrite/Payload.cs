using System.Buffers;
using DotNext.Benchmarks.DurableWrite.Oracles;

namespace DotNext.Benchmarks.DurableWrite;

/// <summary>
/// Builds the payload of a write: the <see cref="WriteKey"/> header, then a fixed filler up to the entry size.
/// </summary>
/// <remarks>
/// The filler is not compressible to nothing (it is a byte ramp), so the bytes on disk track the payload size.
/// The write-ahead log copies the payload when it appends the entry, so a buffer can be reused once the write
/// has returned, whatever its outcome.
/// </remarks>
internal sealed class Payload
{
    private readonly byte[] template;

    internal Payload(int size)
    {
        ArgumentOutOfRangeException.ThrowIfLessThan(size, WriteKey.HeaderSize);
        template = new byte[size];
        for (var i = WriteKey.HeaderSize; i < size; i++)
            template[i] = (byte)(i * 31);
    }

    internal int Size => template.Length;

    /// <summary>
    /// Allocates a buffer that holds the filler, for a client to reuse across its writes.
    /// </summary>
    internal byte[] CreateBuffer() => (byte[])template.Clone();

    /// <summary>
    /// Rents a buffer that holds the filler; return it to <see cref="ArrayPool{T}.Shared"/>.
    /// </summary>
    internal byte[] Rent()
    {
        var buffer = ArrayPool<byte>.Shared.Rent(template.Length);
        template.CopyTo(buffer, 0);
        return buffer;
    }

    internal ReadOnlyMemory<byte> Write(byte[] buffer, WriteKey key)
    {
        key.Write(buffer);
        return buffer.AsMemory(0, template.Length);
    }
}
