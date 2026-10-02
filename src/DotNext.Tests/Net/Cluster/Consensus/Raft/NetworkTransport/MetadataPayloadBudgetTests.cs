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
    [Theory(Timeout = TestTimeouts.Default)]
    [InlineData(false, 1 << 23)]
    [InlineData(false, int.MaxValue)]
    [InlineData(false, -1)]
    [InlineData(true, 1 << 23)]
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
        if (declaredCount < 0)
            IsType<RaftProtocolException>(error);
    }

    // The memory must follow the bytes received, not the declared count.
    [Theory(Timeout = TestTimeouts.Default)]
    [InlineData(false)]
    [InlineData(true)]
    public static async Task TruncatedPairsAreRejected(bool stream)
    {
        var payload = Encode(new Dictionary<string, string> { ["a"] = "1", ["b"] = "2" });
        BinaryPrimitives.WriteInt32LittleEndian(payload, 1 << 23);

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

        NotNull(error);
        True(allocated < AllocationBudget, $"Allocated {allocated} bytes for 2 delivered pairs");
        IsType<RaftProtocolException>(error);
    }

    // Control: valid metadata still round-trips through both readers.
    [Theory(Timeout = TestTimeouts.Default)]
    [InlineData(false, 0)]
    [InlineData(false, 1)]
    [InlineData(false, 1000)]
    [InlineData(true, 0)]
    [InlineData(true, 1)]
    [InlineData(true, 1000)]
    public static async Task ValidMetadataRoundTrips(bool stream, int count)
    {
        var expected = new Dictionary<string, string>(StringComparer.Ordinal);
        for (var i = 0; i < count; i++)
            expected.Add($"key{i}", $"value-{i}-\u00e9\u4e2d");

        var payload = Encode(expected);
        MetadataTransferObject result;
        if (stream)
        {
            using var input = new MemoryStream(payload, writable: false);
            result = await MetadataTransferObject.ReadFromAsync(IAsyncBinaryReader.Create(input, new byte[512]), TestToken);
        }
        else
        {
            result = await MetadataTransferObject.ReadFromAsync(new Buffers.SequenceReader(new ReadOnlySequence<byte>(payload)), TestToken);
        }

        Equal(expected.Count, result.Metadata.Count);
        foreach (var (key, value) in expected)
            Equal(value, result.Metadata[key]);
    }

    private static byte[] Encode(IReadOnlyDictionary<string, string> metadata)
    {
        using var output = new MemoryStream();
        new MetadataTransferObject(metadata).WriteToAsync(output, bufferSize: 512, token: TestToken).AsTask().GetAwaiter().GetResult();
        return output.ToArray();
    }
}
