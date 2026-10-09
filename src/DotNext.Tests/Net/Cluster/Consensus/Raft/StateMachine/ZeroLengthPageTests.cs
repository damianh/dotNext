namespace DotNext.Net.Cluster.Consensus.Raft.StateMachine;

using IO;

/// <summary>
/// A process killed inside <c>AnonymousPage.FlushAsync</c>, between <c>File.OpenHandle(FileMode.OpenOrCreate)</c> and
/// <c>RandomAccess.SetLength</c>, leaves a zero-length data page file. The WAL must still open after the restart.
/// </summary>
[Collection(TestCollections.WriteAheadLog)]
public sealed class ZeroLengthPageTests : Test
{
    private const int ChunkSize = 4096;

    [Fact(Timeout = TestTimeouts.Default)]
    public static async Task OpensAfterCrashBetweenPageCreateAndResize()
    {
        var location = GetTempPath();
        ReadOnlyMemory<byte> payload = Enumerable.Repeat((byte)0x5A, 100).ToArray();

        await using (var wal = new WriteAheadLog(new() { Location = location, ChunkSize = ChunkSize }, IStateMachine.CreateNoOp()))
        {
            Equal(1L, await wal.AppendAsync(new BinaryLogEntry { Content = payload, Term = 1L }, TestToken));
            await wal.CommitAsync(1L, TestToken);
            await wal.FlushAsync(TestToken);
        }

        // The crash state: the next page file exists, but its length was never set.
        var data = new DirectoryInfo(Path.Combine(location, "data"));
        var last = data.EnumerateFiles().Select(static f => uint.Parse(f.Name)).Max();
        File.Create(Path.Combine(data.FullName, (last + 1U).ToString())).Dispose();

        await using (var wal = new WriteAheadLog(new() { Location = location, ChunkSize = ChunkSize }, IStateMachine.CreateNoOp()))
        {
            Equal(1L, wal.LastEntryIndex);
            using var entries = await wal.ReadAsync(1L, 1L, TestToken);
            Equal(payload.ToArray(), await entries[0].ToByteArrayAsync(token: TestToken));
        }
    }
}
