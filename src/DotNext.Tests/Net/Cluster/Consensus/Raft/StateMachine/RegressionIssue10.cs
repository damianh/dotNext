namespace DotNext.Net.Cluster.Consensus.Raft.StateMachine;

using IO;

[Collection(TestCollections.WriteAheadLog)]
public sealed class RegressionIssue10 : Test
{
    private const int FragmentSize = 4096;

    [Fact]
    public static void RejectUnsupportedChunkSize()
    {
        var location = GetTempPath();

        Throws<ArgumentOutOfRangeException>(() => new WriteAheadLog.Options
        {
            Location = location,
            ChunkSize = FragmentSize * 3,
        });

        False(Directory.Exists(location));
    }

    public static TheoryData<int, bool, WriteAheadLog.IntegrityHashAlgorithm> SupportedConfigurations
    {
        get
        {
            var result = new TheoryData<int, bool, WriteAheadLog.IntegrityHashAlgorithm>();
            foreach (var chunkSize in new[] { FragmentSize, FragmentSize * 2 })
            {
                foreach (var streamed in new[] { false, true })
                {
                    foreach (var hashAlgorithm in Enum.GetValues<WriteAheadLog.IntegrityHashAlgorithm>())
                        result.Add(chunkSize, streamed, hashAlgorithm);
                }
            }

            return result;
        }
    }

    [Theory]
    [MemberData(nameof(SupportedConfigurations))]
    public async Task SupportedChunkSizeRoundtrip(
        int chunkSize,
        bool streamed,
        WriteAheadLog.IntegrityHashAlgorithm hashAlgorithm)
    {
        ReadOnlyMemory<byte> firstFragment = Enumerable.Repeat((byte)0xAA, FragmentSize).ToArray();
        ReadOnlyMemory<byte> secondFragment = Enumerable.Repeat((byte)0x55, FragmentSize).ToArray();
        var expected = firstFragment.ToArray().Concat(secondFragment.ToArray()).ToArray();
        IRaftLogEntry entry = streamed
            ? new FragmentedLogEntry(firstFragment, secondFragment)
            : new BinaryLogEntry { Content = expected, Term = 1L };

        await using var wal = new WriteAheadLog(
            new()
            {
                Location = GetTempPath(),
                ChunkSize = chunkSize,
                HashAlgorithm = hashAlgorithm,
            },
            IStateMachine.CreateNoOp());

        Equal(1L, await wal.AppendAsync(entry, TestToken));

        using var entries = await wal.ReadAsync(1L, 1L, TestToken);
        Equal(expected, await entries[0].ToByteArrayAsync(token: TestToken));
    }

    private readonly struct FragmentedLogEntry(
        ReadOnlyMemory<byte> firstFragment,
        ReadOnlyMemory<byte> secondFragment) : IRaftLogEntry
    {
        public long Term => 1L;

        public bool IsSnapshot => false;

        public long? Length => null;

        public bool IsReusable => true;

        public async ValueTask WriteToAsync<TWriter>(TWriter writer, CancellationToken token)
            where TWriter : IAsyncBinaryWriter
        {
            await writer.WriteAsync(firstFragment, token: token);
            await writer.WriteAsync(secondFragment, token: token);
        }
    }
}
