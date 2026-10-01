using System.Buffers;
using System.Buffers.Binary;

namespace DotNext.Net.Cluster.Consensus.Raft.NetworkTransport;

using IO;

/// <summary>
/// Guards for issue #22: the member metadata returned by a TCP/UDP peer is decoded by <see cref="MetadataTransferObject"/>.
/// Its pair count prefix is untrusted input.
/// </summary>
[Collection(TestCollections.AllocationBudget)]
public sealed class MetadataPayloadBudgetTests : Test
{
    private const long AllocationBudget = 32L << 20;

    // A declared pair count without the pairs must be rejected without reserving memory for the declared pairs.
    // A large but representable count is not covered: it reserves memory for the declared pairs (see RAFT-REVIEW.md, #22).
    [Theory(Timeout = TestTimeouts.Default)]
    [InlineData(false, int.MaxValue)]
    [InlineData(false, -1)]
    [InlineData(true, int.MaxValue)]
    [InlineData(true, -1)]
    public static async Task DeclaredPairCountDoesNotDriveAllocation(bool stream, int declaredCount)
    {
        var payload = new byte[sizeof(int)];
        BinaryPrimitives.WriteInt32LittleEndian(payload, declaredCount);

        Exception error;
        long allocated;
        if (stream)
        {
            using var input = new MemoryStream(payload, writable: false);
            var buffer = new byte[512];
            var before = GC.GetTotalAllocatedBytes(precise: true);
            error = await Record.ExceptionAsync(() => MetadataTransferObject.ReadFromAsync(IAsyncBinaryReader.Create(input, buffer), TestToken).AsTask());
            allocated = GC.GetTotalAllocatedBytes(precise: true) - before;
        }
        else
        {
            var before = GC.GetTotalAllocatedBytes(precise: true);
            error = await Record.ExceptionAsync(() => MetadataTransferObject.ReadFromAsync(new Buffers.SequenceReader(new ReadOnlySequence<byte>(payload)), TestToken).AsTask());
            allocated = GC.GetTotalAllocatedBytes(precise: true) - before;
        }

        TestContext.Current.TestOutputHelper?.WriteLine($"Declared {declaredCount} pairs, delivered 0: {error?.GetType().Name ?? "accepted"}, allocated {allocated} bytes");
        NotNull(error);
        True(allocated < AllocationBudget, $"Allocated {allocated} bytes for 0 delivered pairs");
    }
}
