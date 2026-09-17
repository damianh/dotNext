using System.Buffers.Binary;
using System.IO.Hashing;
using static Xunit.Assert;

namespace DotNext.Net.Cluster.Consensus.Raft.StateMachine;

internal static class WalCheckpointAssertions
{
    internal static byte[] ReadCheckpointBytes(string location)
    {
        using var handle = File.OpenHandle(
            Path.Combine(location, "checkpoint"),
            access: FileAccess.Read,
            share: FileShare.ReadWrite | FileShare.Delete);
        var content = new byte[checked((int)RandomAccess.GetLength(handle))];
        for (var offset = 0; offset < content.Length;)
        {
            var count = RandomAccess.Read(handle, content.AsSpan(offset), offset);
            True(count > 0);
            offset += count;
        }

        return content;
    }

    internal static long ReadCommittedCheckpoint(string location)
    {
        ReadOnlySpan<byte> content = ReadCheckpointBytes(location);
        switch (content.Length)
        {
            case 0:
                return 0L;
            case sizeof(long):
                return BinaryPrimitives.ReadInt64LittleEndian(content);
            case sizeof(uint) + sizeof(long):
                Equal(0U, BinaryPrimitives.ReadUInt32LittleEndian(content));
                return BinaryPrimitives.ReadInt64LittleEndian(content.Slice(sizeof(uint)));
        }

        Equal(1U, BinaryPrimitives.ReadUInt32LittleEndian(content));
        var blockSize = BinaryPrimitives.ReadInt32LittleEndian(content.Slice(12));
        Equal(blockSize * 3, content.Length);
        var first = ReadSlot(content.Slice(blockSize, blockSize));
        var second = ReadSlot(content.Slice(blockSize * 2, blockSize));
        return first.Generation > second.Generation ? first.CommittedIndex : second.CommittedIndex;

        static (long Generation, long CommittedIndex) ReadSlot(ReadOnlySpan<byte> slot)
        {
            Equal(1U, BinaryPrimitives.ReadUInt32LittleEndian(slot));
            Equal(Crc64.HashToUInt64(slot[..^sizeof(ulong)]),
                BinaryPrimitives.ReadUInt64LittleEndian(slot[^sizeof(ulong)..]));
            return (BinaryPrimitives.ReadInt64LittleEndian(slot.Slice(64)),
                BinaryPrimitives.ReadInt64LittleEndian(slot.Slice(32)));
        }
    }
}
