using System.Buffers.Binary;
using static System.Threading.Timeout;

namespace DotNext.Net.Cluster.Consensus.Raft.StateMachine;

using IO;

/// <summary>
/// Covers issue #16: the constructor captured the no-op state machine's snapshot index before reconstructing
/// it from the checkpoint, so a compacted log reopened with a fresh no-op machine started replay at index 1
/// and touched metadata pages that reclamation had already deleted. The applier's live snapshot clamp from #15
/// keeps replay past the restored boundary; this test guards that behavior for the production no-op machine.
/// </summary>
[Collection(TestCollections.WriteAheadLog)]
public sealed class RegressionIssue16 : Test
{
    private const long SnapshotDepth = 10L;

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public static async Task CompactedNoOpLogReopensFromRestoredSnapshot(bool flushOnCommit)
    {
        var options = CreateOptions(flushOnCommit ? TimeSpan.Zero : InfiniteTimeSpan);
        var firstMetadataPage = Path.Combine(options.Location, "metadata", "0");

        // Two full metadata pages plus a partial third one, so reclamation has whole pages to delete.
        var entryCount = GetMetadataEntriesPerPage() * 2L + SnapshotDepth / 2L;
        var restoredSnapshot = (entryCount - 1L) / SnapshotDepth * SnapshotDepth;

        await using (var wal = new WriteAheadLog(options, IStateMachine.CreateNoOp(SnapshotDepth)))
        {
            for (var i = 1L; i <= entryCount; i++)
                Equal(i, await wal.AppendAsync(new BinaryLogEntry { Content = BitConverter.GetBytes(i), Term = 1L }, TestToken));

            True(File.Exists(firstMetadataPage));

            // Reclamation piggybacks on flush passes. Committing in two steps guarantees that, with flush-on-commit,
            // at least one pass runs after the no-op snapshot has advanced past the first metadata page.
            await wal.CommitAsync(entryCount - 1L, TestToken);
            await wal.WaitForApplyAsync(entryCount - 1L, TestToken);
            await wal.CommitAsync(entryCount, TestToken);
            await wal.WaitForApplyAsync(entryCount, TestToken);
            await wal.FlushAsync(TestToken);
            Equal(entryCount, ReadCheckpoint(options.Location));

            // Real compaction: the squashed metadata page is deleted by the cleanup the flusher scheduled.
            await SpinWaitAsync(() => !File.Exists(firstMetadataPage));
        }

        await using (var reopened = new WriteAheadLog(options, IStateMachine.CreateNoOp(SnapshotDepth)))
        {
            await reopened.InitializeAsync(TestToken);

            Equal(entryCount, reopened.LastCommittedEntryIndex);
            Equal(entryCount, reopened.LastEntryIndex);
            Equal(entryCount, reopened.LastAppliedIndex);
            False(File.Exists(firstMetadataPage));

            using (var entries = await reopened.ReadAsync(restoredSnapshot + 1L, entryCount, TestToken))
            {
                Equal(entryCount - restoredSnapshot, entries.Count);
                Equal(entryCount, BinaryPrimitives.ReadInt64LittleEndian(await entries[^1].ToByteArrayAsync(token: TestToken)));
            }

            Equal(entryCount + 1L, await reopened.AppendAsync(new BinaryLogEntry { Content = BitConverter.GetBytes(entryCount + 1L), Term = 1L }, TestToken));
            await reopened.CommitAsync(entryCount + 1L, TestToken);
            await reopened.WaitForApplyAsync(entryCount + 1L, TestToken);
            await reopened.FlushAsync(TestToken);
            Equal(entryCount + 1L, ReadCheckpoint(options.Location));
        }
    }

    private static long GetMetadataEntriesPerPage()
    {
        var pageSize = int.Max(4096, Environment.SystemPageSize);
        return pageSize / GetAlignedSize(LogEntryMetadata.Size, pageSize);
    }

    // Mirrors MetadataPageManager.GetAlignedSize for a log without integrity hashes.
    private static int GetAlignedSize(int headerSize, int containerSize)
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

    private static async Task SpinWaitAsync(Func<bool> condition)
    {
        for (var attempt = 0; attempt < 200; attempt++)
        {
            if (condition())
                return;

            await Task.Delay(50, TestToken);
        }

        True(condition());
    }

    private static WriteAheadLog.Options CreateOptions(TimeSpan interval)
        => new()
        {
            Location = GetTempPath(),
            // Memory-mapped pages exist on disk before commits can start reclamation.
            MemoryManagement = WriteAheadLog.MemoryManagementStrategy.SharedMemory,
            FlushInterval = interval,
        };

    private static long ReadCheckpoint(string location)
    {
        // The log keeps the checkpoint file open for writing.
        using var handle = File.OpenHandle(
            Path.Combine(location, "checkpoint"),
            access: FileAccess.Read,
            share: FileShare.ReadWrite);

        Span<byte> content = stackalloc byte[sizeof(uint) + sizeof(long)];
        return RandomAccess.Read(handle, content, fileOffset: 0L) switch
        {
            0 => 0L,
            sizeof(long) => BinaryPrimitives.ReadInt64LittleEndian(content),
            _ => BinaryPrimitives.ReadInt64LittleEndian(content.Slice(sizeof(uint))),
        };
    }
}
