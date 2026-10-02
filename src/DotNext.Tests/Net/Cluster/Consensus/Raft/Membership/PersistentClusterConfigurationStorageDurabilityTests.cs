using System.Net;
using static System.Buffers.Binary.BinaryPrimitives;

namespace DotNext.Net.Cluster.Consensus.Raft.Membership;

using Buffers;
using IO;
using IO.Log;
using HttpEndPoint = Net.Http.HttpEndPoint;

public sealed class PersistentClusterConfigurationStorageDurabilityTests : Test
{
    private sealed class Storage(string fileName, Action<DirectoryInfo> flushDirectory)
        : PersistentClusterConfigurationStorage<HttpEndPoint>(fileName, flushDirectory)
    {
        protected override HttpEndPoint Decode(ref SequenceReader reader)
            => (HttpEndPoint)reader.ReadEndPoint();

        protected override void Encode(HttpEndPoint address, ref BufferWriterSlim<byte> writer)
            => writer.WriteEndPoint(address);
    }

    [Theory(Timeout = TestTimeouts.Default)]
    [InlineData(0)]
    [InlineData(1)]
    [InlineData(7)]
    public static async Task ShortFileFailsClosedOnLoad(int length)
    {
        var path = GetTempPath();
        var content = new byte[length];
        await File.WriteAllBytesAsync(path, content, TestToken);

        using var storage = new Storage(path, static _ => { });
        var error = await ThrowsAsync<IntegrityException>(
            () => storage.As<IClusterConfigurationStorage>().LoadConfigurationAsync(TestToken).AsTask());

        Contains(path, error.Message);
        Equal(content, await File.ReadAllBytesAsync(path, TestToken));
    }

    [Theory(Timeout = TestTimeouts.Default)]
    [InlineData(0)]
    [InlineData(1)]
    [InlineData(7)]
    public static async Task ShortFileFailsClosedOnSave(int length)
    {
        var path = GetTempPath();
        var content = new byte[length];
        await File.WriteAllBytesAsync(path, content, TestToken);

        using var storage = new Storage(path, static _ => { });
        var error = await ThrowsAsync<IntegrityException>(
            () => storage.As<IClusterConfigurationStorage>()
                .SaveConfigurationAsync(new BinaryTransferObject(new byte[sizeof(int)]), 1L, TestToken)
                .AsTask());

        Contains(path, error.Message);
        Equal(content, await File.ReadAllBytesAsync(path, TestToken));
        Empty(Directory.EnumerateFiles(Path.GetDirectoryName(path)!, $"{Path.GetFileName(path)}.*.tmp"));
    }

    [Fact(Timeout = TestTimeouts.Default)]
    public static async Task FreshSaveFlushesDirectoryAfterPublish()
    {
        var path = GetTempPath();
        var expectedDirectory = Path.GetDirectoryName(path);
        var barrierCount = 0;

        void FlushDirectory(DirectoryInfo directory)
        {
            Equal(expectedDirectory, directory.FullName);
            True(File.Exists(path));
            True(new FileInfo(path).Length >= sizeof(long));
            barrierCount++;
        }

        using var storage = new Storage(path, FlushDirectory);
        var typedStorage = storage.As<IClusterConfigurationStorage<HttpEndPoint>>();
        var configuration = await typedStorage.LoadConfigurationAsync(TestToken);
        True(await typedStorage.SaveConfigurationAsync(configuration, 1L, TestToken));

        Equal(1, barrierCount);
    }

    [Fact(Timeout = TestTimeouts.Default)]
    public static async Task RewriteFlushesDirectoryAfterRename()
    {
        var path = GetTempPath();
        var barrierCount = 0;
        var expectedVersion = 1L;

        void FlushDirectory(DirectoryInfo _)
        {
            Equal(expectedVersion, ReadInt64LittleEndian(File.ReadAllBytes(path)));
            barrierCount++;
        }

        using var storage = new Storage(path, FlushDirectory);
        var typedStorage = storage.As<IClusterConfigurationStorage<HttpEndPoint>>();
        var configuration = await typedStorage.LoadConfigurationAsync(TestToken);
        True(await typedStorage.SaveConfigurationAsync(configuration, 1L, TestToken));
        barrierCount = 0;
        expectedVersion = 2L;

        True(await typedStorage.SaveConfigurationAsync(configuration, 2L, TestToken));

        Equal(1, barrierCount);
    }

    [Fact(Timeout = TestTimeouts.Default)]
    public static async Task DirectoryBarrierFailureIsRetriedAfterReopen()
    {
        var path = GetTempPath();
        var barrierCount = 0;

        void FlushDirectory(DirectoryInfo _)
        {
            if (++barrierCount is 1)
                throw new IOException("Injected directory barrier failure.");
        }

        using (var storage = new Storage(path, FlushDirectory))
        {
            var typedStorage = storage.As<IClusterConfigurationStorage<HttpEndPoint>>();
            var configuration = await typedStorage.LoadConfigurationAsync(TestToken);
            await ThrowsAsync<IOException>(
                () => typedStorage.SaveConfigurationAsync(configuration, 1L, TestToken).AsTask());
        }

        True(File.Exists(path));
        using var reopened = new Storage(path, FlushDirectory);
        Equal(2, barrierCount);
    }

    [Fact(Timeout = TestTimeouts.Default)]
    public static async Task StaleTemporaryFilesAreRemoved()
    {
        var path = GetTempPath();
        var stale = $"{path}.stale.tmp";
        var unrelated = Path.Combine(Path.GetDirectoryName(path)!, $"{Path.GetRandomFileName()}.tmp");
        await File.WriteAllBytesAsync(stale, [], TestToken);
        await File.WriteAllBytesAsync(unrelated, [], TestToken);

        using var storage = new Storage(path, static _ => { });

        False(File.Exists(stale));
        True(File.Exists(unrelated));
    }
}
