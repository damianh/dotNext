using System.Net;

namespace DotNext.Net.Cluster.Consensus.Raft.Membership;

using Buffers;
using IO;
using HttpEndPoint = Net.Http.HttpEndPoint;

/// <summary>
/// Guards for issue #22: a configuration payload received from a peer (a staged snapshot configuration
/// or a configuration log entry) is decoded by <see cref="ClusterConfigurationStorage{TAddress}"/>.
/// Its member count prefix is untrusted input.
/// </summary>
[Collection(TestCollections.AllocationBudget)]
public sealed class ConfigurationPayloadBudgetTests : Test
{
    private const long AllocationBudget = 32L << 20;

    private static readonly HttpEndPoint Member = new(IPAddress.Loopback, 4292, false);

    // A negative member count is not a valid encoding of any configuration.
    // It must be rejected, not decoded as an empty member set, and the applied configuration must not change.
    [Theory(Timeout = TestTimeouts.Default)]
    [InlineData(false)]
    [InlineData(true)]
    public static async Task NegativeMemberCountIsRejected(bool persistent)
    {
        var path = GetTempPath();
        byte[] payload = [0xFF, 0xFF, 0xFF, 0xFF];

        using (var storage = await CreateSeededStorageAsync(persistent, path))
        {
            var read = await Record.ExceptionAsync(async () =>
                TestContext.Current.TestOutputHelper?.WriteLine($"Read: {(await ((IClusterConfigurationStorage<HttpEndPoint>)storage).ReadConfigurationAsync(new BinaryTransferObject(payload), TestToken)).Members.Count} members"));
            var save = await Record.ExceptionAsync(async () =>
                TestContext.Current.TestOutputHelper?.WriteLine($"Save: {await ((IClusterConfigurationStorage)storage).SaveConfigurationAsync(new BinaryTransferObject(payload), 2L, TestToken)}"));

            await AssertSeededAsync(storage);
            NotNull(read);
            NotNull(save);
        }

        if (persistent)
        {
            using var reopened = new PersistentStorage(path);
            await AssertSeededAsync(reopened);
        }
    }

    // A member count beyond the payload is rejected before anything is persisted,
    // so the applied configuration stays loadable, and the count does not drive allocation.
    [Theory(Timeout = TestTimeouts.Default)]
    [InlineData(false, 1000)]
    [InlineData(true, 1000)]
    [InlineData(false, int.MaxValue)]
    [InlineData(true, int.MaxValue)]
    public static async Task MemberCountBeyondPayloadIsRejectedBeforePersisting(bool persistent, int count)
    {
        var path = GetTempPath();
        var payload = new byte[sizeof(int)];
        BitConverter.TryWriteBytes(payload, count);

        using (var storage = await CreateSeededStorageAsync(persistent, path))
        {
            var before = GC.GetTotalAllocatedBytes(precise: true);
            var save = await Record.ExceptionAsync(async () =>
                await ((IClusterConfigurationStorage)storage).SaveConfigurationAsync(new BinaryTransferObject(payload), 2L, TestToken));
            var allocated = GC.GetTotalAllocatedBytes(precise: true) - before;
            TestContext.Current.TestOutputHelper?.WriteLine($"Save: {save?.GetType().Name ?? "no exception"}, allocated {allocated} bytes");

            NotNull(save);
            True(allocated < AllocationBudget, $"Allocated {allocated} bytes for a {payload.Length} byte payload");
            await AssertSeededAsync(storage);
        }

        if (persistent)
        {
            using var reopened = new PersistentStorage(path);
            await AssertSeededAsync(reopened);
        }
    }

    // Control: an empty configuration (a zero member count) is valid and replaces the applied configuration, as before.
    [Theory(Timeout = TestTimeouts.Default)]
    [InlineData(false)]
    [InlineData(true)]
    public static async Task EmptyConfigurationIsApplied(bool persistent)
    {
        var path = GetTempPath();
        byte[] payload = [0, 0, 0, 0];

        using (var storage = await CreateSeededStorageAsync(persistent, path))
        {
            True(await ((IClusterConfigurationStorage)storage).SaveConfigurationAsync(new BinaryTransferObject(payload), 2L, TestToken));
            await AssertEmptyAsync(storage);
        }

        if (persistent)
        {
            using var reopened = new PersistentStorage(path);
            await AssertEmptyAsync(reopened);
        }

        static async Task AssertEmptyAsync(IClusterConfigurationStorage<HttpEndPoint> storage)
        {
            Empty((await storage.LoadConfigurationAsync(TestToken)).Members);
            Equal(2L, (await ((IClusterConfigurationStorage)storage).LoadConfigurationAsync(TestToken)).Version);
        }
    }

    private static async Task<ClusterConfigurationStorage<HttpEndPoint>> CreateSeededStorageAsync(bool persistent, string path)
    {
        ClusterConfigurationStorage<HttpEndPoint> storage = persistent ? new PersistentStorage(path) : new InMemoryStorage();
        IClusterConfigurationStorage<HttpEndPoint> typed = storage;
        var configuration = await typed.LoadConfigurationAsync(TestToken);
        await typed.SaveConfigurationAsync(configuration.Add(Member), 1L, TestToken);
        return storage;
    }

    private static async Task AssertSeededAsync(IClusterConfigurationStorage<HttpEndPoint> storage)
    {
        var configuration = await storage.LoadConfigurationAsync(TestToken);
        Equal(Member, Single(configuration.Members));

        var (_, version) = await ((IClusterConfigurationStorage)storage).LoadConfigurationAsync(TestToken);
        Equal(1L, version);
    }

    private sealed class InMemoryStorage : InMemoryClusterConfigurationStorage<HttpEndPoint>
    {
        protected override HttpEndPoint Decode(ref SequenceReader reader)
            => (HttpEndPoint)reader.ReadEndPoint();

        protected override void Encode(HttpEndPoint address, ref BufferWriterSlim<byte> writer)
            => writer.WriteEndPoint(address);
    }

    private sealed class PersistentStorage(string fileName) : PersistentClusterConfigurationStorage<HttpEndPoint>(fileName)
    {
        protected override HttpEndPoint Decode(ref SequenceReader reader)
            => (HttpEndPoint)reader.ReadEndPoint();

        protected override void Encode(HttpEndPoint address, ref BufferWriterSlim<byte> writer)
            => writer.WriteEndPoint(address);
    }
}
