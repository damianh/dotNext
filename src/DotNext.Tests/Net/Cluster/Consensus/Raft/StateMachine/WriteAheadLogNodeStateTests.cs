namespace DotNext.Net.Cluster.Consensus.Raft.StateMachine;

using IO.Log;

[Collection(TestCollections.WriteAheadLog)]
public sealed class WriteAheadLogNodeStateTests : Test
{
    private const int RecordSize = 37;

    [Fact]
    public static void DirectoryBarrierFailureIsRetriedAfterReopen()
    {
        var location = GetTempPath();
        var options = new WriteAheadLog.Options { Location = location };
        var statePath = Path.Combine(location, "state");
        var barrierCount = 0;

        void FlushDirectory(DirectoryInfo _)
        {
            if (++barrierCount is 1)
                throw new IOException("Injected directory barrier failure.");
        }

        Throws<IOException>(() => new WriteAheadLog(options, IStateMachine.CreateNoOp(), FlushDirectory));
        True(File.Exists(statePath));
        Equal(RecordSize, new FileInfo(statePath).Length);

        using var wal = new WriteAheadLog(options, IStateMachine.CreateNoOp(), FlushDirectory);
        Equal(2, barrierCount);
        True(File.Exists(statePath));
    }

    [Theory(Timeout = TestTimeouts.Default)]
    [InlineData(1)]
    [InlineData(18)]
    [InlineData(RecordSize - 1)]
    public static async Task TruncatedStateFileFailsClosed(int length)
    {
        var options = CreateOptions();
        var statePath = Path.Combine(options.Location, "state");
        var truncated = (await SeedStateAsync(options, statePath))[..length];
        await File.WriteAllBytesAsync(statePath, truncated, TestToken);

        var error = Throws<IntegrityException>(() => new WriteAheadLog(options, IStateMachine.CreateNoOp()));

        Contains(statePath, error.Message);
        Equal(truncated, await File.ReadAllBytesAsync(statePath, TestToken));
    }

    [Fact(Timeout = TestTimeouts.Default)]
    public static async Task EmptyStateFileIsInitialized()
    {
        var options = CreateOptions();
        var statePath = Path.Combine(options.Location, "state");
        await SeedStateAsync(options, statePath);
        await File.WriteAllBytesAsync(statePath, [], TestToken);

        await using (var wal = new WriteAheadLog(options, IStateMachine.CreateNoOp()))
        {
            Equal(0L, wal.As<IPersistentState>().Term);
        }

        Equal(new byte[RecordSize], await File.ReadAllBytesAsync(statePath, TestToken));
    }

    [Fact(Timeout = TestTimeouts.Default)]
    public static async Task CompleteStateFileIsUnchanged()
    {
        var options = CreateOptions();
        var statePath = Path.Combine(options.Location, "state");
        var expected = await SeedStateAsync(options, statePath);

        await using (var wal = new WriteAheadLog(options, IStateMachine.CreateNoOp()))
        {
            Equal(5L, wal.As<IPersistentState>().Term);
        }

        Equal(expected, await File.ReadAllBytesAsync(statePath, TestToken));
    }

    // creates a state file that holds term 5 and returns its content
    private static async Task<byte[]> SeedStateAsync(WriteAheadLog.Options options, string statePath)
    {
        await using (var wal = new WriteAheadLog(options, IStateMachine.CreateNoOp()))
        {
            await wal.As<IPersistentState>().UpdateTermAsync(5L, resetLastVote: true, TestToken);
        }

        var content = await File.ReadAllBytesAsync(statePath, TestToken);
        Equal(RecordSize, content.Length);
        return content;
    }

    private static WriteAheadLog.Options CreateOptions()
        => WriteAheadLogDurabilityTests.CreateOptions(GetTempPath(),
            WriteAheadLog.MemoryManagementStrategy.PrivateMemory, false, 0, WriteAheadLog.IntegrityHashAlgorithm.Crc64);
}
