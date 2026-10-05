using System.Buffers.Binary;

namespace DotNext.Benchmarks.DurableWrite.Oracles;

/// <summary>
/// The identity of one client write, carried in the first bytes of its payload.
/// </summary>
/// <param name="Mode">0 for a closed-loop client, which sends its writes one at a time; 1 for open-loop writes.</param>
/// <param name="Client">The client number.</param>
/// <param name="Seq">The sequence number of the write, unique per client. A retry gets a new number.</param>
internal readonly record struct WriteKey(byte Mode, int Client, long Seq)
{
    internal const int HeaderSize = sizeof(byte) + sizeof(int) + sizeof(long);
    internal const byte ClosedLoop = 0, OpenLoop = 1;

    internal void Write(Span<byte> destination)
    {
        destination[0] = Mode;
        BinaryPrimitives.WriteInt32LittleEndian(destination[1..], Client);
        BinaryPrimitives.WriteInt64LittleEndian(destination[5..], Seq);
    }

    /// <summary>
    /// Reads the key of a payload. A payload shorter than the header, such as the no-op entry of a new leader, has none.
    /// </summary>
    internal static WriteKey? TryRead(ReadOnlySpan<byte> payload)
        => payload.Length < HeaderSize
            ? null
            : new WriteKey(payload[0], BinaryPrimitives.ReadInt32LittleEndian(payload[1..]), BinaryPrimitives.ReadInt64LittleEndian(payload[5..]));

    /// <summary>
    /// The payload string used by the <c>SimulationHistory</c> oracles.
    /// </summary>
    public override string ToString() => $"m{Mode}-c{Client}-s{Seq}";

    internal static string ToPayloadString(WriteKey? key) => key?.ToString() ?? "no-op";
}

/// <summary>
/// One entry as applied, or as found on disk, on one node.
/// </summary>
/// <remarks>
/// The write-ahead log does not pass entries without payload to the state machine. Such an index is recorded as
/// skipped, with term 0, which no entry appended by a leader has.
/// </remarks>
internal readonly record struct AppliedEntry(long Index, long Term, WriteKey? Key)
{
    internal bool IsSkipped => Term is 0L;

    internal static AppliedEntry CreateSkipped(long index) => new(index, 0L, null);
}

/// <summary>
/// A write that the cluster acknowledged, at the index where it was applied.
/// </summary>
internal readonly record struct AcknowledgedWrite(WriteKey Key, int Node, long Index);
