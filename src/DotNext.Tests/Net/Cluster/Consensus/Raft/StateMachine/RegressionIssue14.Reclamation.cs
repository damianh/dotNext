using System.IO.Hashing;
using System.Text;
using DotNext.IO;
using static System.Threading.Timeout;

namespace DotNext.Net.Cluster.Consensus.Raft.StateMachine;

public sealed partial class RegressionIssue14 : Test
{
    [Fact]
    public static async Task SnapshotBoundaryInLastMetadataSlotSurvivesReclamation()
    {
        const WriteAheadLog.IntegrityHashAlgorithm hashAlgorithm = WriteAheadLog.IntegrityHashAlgorithm.None;
        var options = CreateOptions(InfiniteTimeSpan, hashAlgorithm);
        var metadataPages = new DirectoryInfo(Path.Combine(options.Location, "metadata"));
        var dataPage = new FileInfo(Path.Combine(options.Location, "data", "0"));
        var machineLocation = new DirectoryInfo(GetTempPath());
        var snapshotIndex = GetReclamationPageTerminalIndex(hashAlgorithm, pageIndex: 1);
        byte[] state = [1, 2, 3, 4];
        var retainedPayload = Encoding.UTF8.GetBytes("retained payload before the snapshot boundary");
        var postSnapshotPayload = Encoding.UTF8.GetBytes("post-snapshot entry");

        await using (var machine = new ByteArrayStateMachine(machineLocation))
        {
            await machine.RestoreAsync(TestToken);
            await using var wal = new WriteAheadLog(options, machine);

            Equal(1L, await wal.AppendAsync(new BinaryLogEntry { Content = retainedPayload, Term = 1L }, TestToken));
            await wal.CommitAsync(1L, TestToken);
            await wal.FlushAsync(TestToken);
            await wal.WaitForApplyAsync(1L, TestToken);
            True(File.Exists(Path.Combine(metadataPages.FullName, "0")));
            Equal(retainedPayload, File.ReadAllBytes(dataPage.FullName).AsSpan(0, retainedPayload.Length).ToArray());

            QuiesceApplier(wal);
            await wal.AppendAsync(new SnapshotEntry(state, SnapshotTerm), snapshotIndex, TestToken);
            await wal.FlushAsync(TestToken);
            Equal(snapshotIndex, ReadCheckpoint(options.Location));

            await SpinWaitAsync(() => !File.Exists(Path.Combine(metadataPages.FullName, "0")));
        }

        await using (var machine = new ByteArrayStateMachine(machineLocation))
        {
            await machine.RestoreAsync(TestToken);
            await using var wal = new WriteAheadLog(options, machine);
            await wal.InitializeAsync(TestToken);

            Equal(snapshotIndex, wal.LastCommittedEntryIndex);
            Equal(snapshotIndex, wal.LastEntryIndex);
            Equal(state, machine.State);

            Equal(
                snapshotIndex + 1L,
                await wal.AppendAsync(
                    new BinaryLogEntry { Content = postSnapshotPayload, Term = SnapshotTerm },
                    TestToken));
            await wal.CommitAsync(snapshotIndex + 1L, TestToken);
            await wal.FlushAsync(TestToken);
            Equal(snapshotIndex + 1L, ReadCheckpoint(options.Location));
        }

        Equal(retainedPayload, File.ReadAllBytes(dataPage.FullName).AsSpan(0, retainedPayload.Length).ToArray());

        await using (var machine = new ByteArrayStateMachine(machineLocation))
        {
            await machine.RestoreAsync(TestToken);
            await using var wal = new WriteAheadLog(options, machine);
            await wal.InitializeAsync(TestToken);

            Equal(snapshotIndex + 1L, wal.LastCommittedEntryIndex);
            Equal(snapshotIndex + 1L, wal.LastEntryIndex);
            Equal(state, machine.State);

            using var entries = await wal.ReadAsync(snapshotIndex + 1L, snapshotIndex + 1L, TestToken);
            Equal(postSnapshotPayload, await entries[^1].ToByteArrayAsync(token: TestToken));
        }
    }

    private static long GetReclamationPageTerminalIndex(WriteAheadLog.IntegrityHashAlgorithm hashAlgorithm, int pageIndex)
    {
        var pageSize = int.Max(4096, Environment.SystemPageSize);
        var recordSize = GetReclamationAlignedSize(LogEntryMetadata.Size + GetReclamationHashSize(hashAlgorithm), pageSize);
        return ((long)pageIndex + 1L) * (pageSize / recordSize) - 1L;
    }

    private static int GetReclamationAlignedSize(int headerSize, int containerSize)
    {
        var best = int.MaxValue;

        for (var i = 1; i * i <= containerSize; i++)
        {
            if (containerSize % i is not 0)
                continue;

            var d2 = containerSize / i;

            if (i >= headerSize && i < best)
                best = i;

            if (d2 >= headerSize && d2 < best)
                best = d2;
        }

        return best is int.MaxValue
            ? throw new OverflowException()
            : best;
    }

    private static int GetReclamationHashSize(WriteAheadLog.IntegrityHashAlgorithm hashAlgorithm)
        => hashAlgorithm switch
        {
            WriteAheadLog.IntegrityHashAlgorithm.Crc32 => new Crc32().HashLengthInBytes,
            WriteAheadLog.IntegrityHashAlgorithm.Crc64 => new Crc64().HashLengthInBytes,
            WriteAheadLog.IntegrityHashAlgorithm.XxHash32 => new XxHash32().HashLengthInBytes,
            WriteAheadLog.IntegrityHashAlgorithm.XxHash64 => new XxHash64().HashLengthInBytes,
            WriteAheadLog.IntegrityHashAlgorithm.XxHash3 => new XxHash3().HashLengthInBytes,
            _ => 0,
        };
}
