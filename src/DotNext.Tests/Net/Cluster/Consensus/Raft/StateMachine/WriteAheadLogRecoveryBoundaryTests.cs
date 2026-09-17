using System.Buffers.Binary;
using System.IO.Hashing;
using System.Numerics;
using System.Text;

namespace DotNext.Net.Cluster.Consensus.Raft.StateMachine;

using IO;

[Collection(TestCollections.WriteAheadLog)]
public sealed class WriteAheadLogRecoveryBoundaryTests : Test
{
    private const int BlockSize = 64 * 1024;
    private const long SnapshotIndex = 2L;

    public enum BoundaryCorruption
    {
        CheckpointWritePosition,
        MissingMetadata,
        NegativeLength,
        OverflowingEnd,
    }

    [Theory]
    [InlineData(BoundaryCorruption.CheckpointWritePosition)]
    [InlineData(BoundaryCorruption.MissingMetadata)]
    [InlineData(BoundaryCorruption.NegativeLength)]
    [InlineData(BoundaryCorruption.OverflowingEnd)]
    public static async Task SnapshotAtDurableTailRejectsInvalidBoundary(BoundaryCorruption corruption)
    {
        var options = CreateOptions();
        await SeedSnapshotBoundaryAsync(options);
        switch (corruption)
        {
            case BoundaryCorruption.CheckpointWritePosition:
                var path = Path.Combine(options.Location, "checkpoint");
                var bytes = File.ReadAllBytes(path);
                var firstGeneration = BinaryPrimitives.ReadInt64LittleEndian(bytes.AsSpan(BlockSize + 64));
                var secondGeneration = BinaryPrimitives.ReadInt64LittleEndian(bytes.AsSpan(2 * BlockSize + 64));
                var slotOffset = firstGeneration > secondGeneration ? BlockSize : 2 * BlockSize;
                var position = BinaryPrimitives.ReadUInt64LittleEndian(bytes.AsSpan(slotOffset + 48));
                BinaryPrimitives.WriteUInt64LittleEndian(bytes.AsSpan(slotOffset + 48), position + 1UL);
                BinaryPrimitives.WriteUInt64LittleEndian(bytes.AsSpan(slotOffset + BlockSize - 8),
                    Crc64.HashToUInt64(bytes.AsSpan(slotOffset, BlockSize - 8)));
                File.WriteAllBytes(path, bytes);
                break;
            case BoundaryCorruption.MissingMetadata:
                File.Delete(Path.Combine(options.Location, "metadata", "0"));
                break;
            default:
                var metadataPath = Path.Combine(options.Location, "metadata", "0");
                var metadataBytes = File.ReadAllBytes(metadataPath);
                var recordSize = checked((int)BitOperations.RoundUpToPowerOf2((uint)LogEntryMetadata.Size));
                var invalid = corruption is BoundaryCorruption.NegativeLength
                    ? LogEntryMetadata.Create(new TestLogEntry("") { Term = 1L }, 1UL, -1L)
                    : LogEntryMetadata.Create(new TestLogEntry("") { Term = 1L }, ulong.MaxValue, 1L);
                invalid.Format(metadataBytes.AsSpan(checked((int)SnapshotIndex) * recordSize));
                File.WriteAllBytes(metadataPath, metadataBytes);
                break;
        }

        Throws<InvalidDataException>(() =>
        {
            using var wal = new WriteAheadLog(options, new SnapshotStateMachine(SnapshotIndex));
        });
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public static async Task ValidSnapshotBoundaryAllowsAppendAndRestart(bool snapshotAhead)
    {
        var options = CreateOptions();
        await SeedSnapshotBoundaryAsync(options);
        var recoveredSnapshot = SnapshotIndex;
        if (snapshotAhead)
        {
            recoveredSnapshot++;
            // A newer state-machine snapshot can outlive the older checkpoint's reclaimed metadata.
            File.Delete(Path.Combine(options.Location, "metadata", "0"));
        }

        await using (var wal = new WriteAheadLog(options, new SnapshotStateMachine(recoveredSnapshot)))
        {
            await wal.InitializeAsync(TestToken);
            Equal(recoveredSnapshot, wal.LastEntryIndex);
            Equal(recoveredSnapshot, wal.LastCommittedEntryIndex);
            Equal(recoveredSnapshot, wal.LastAppliedIndex);
            Equal(recoveredSnapshot + 1L,
                await wal.AppendAsync(new TestLogEntry("after snapshot") { Term = 1L }, TestToken));
        }

        await using var reopened = new WriteAheadLog(options, new SnapshotStateMachine(recoveredSnapshot));
        await reopened.InitializeAsync(TestToken);
        Equal(recoveredSnapshot + 1L, reopened.LastEntryIndex);
        Equal(recoveredSnapshot, reopened.LastCommittedEntryIndex);
        using var entries = await reopened.ReadAsync(recoveredSnapshot + 1L, recoveredSnapshot + 1L, TestToken);
        Equal("after snapshot", await entries[0].ToStringAsync(Encoding.UTF8, token: TestToken));
    }

    [Fact]
    public static async Task EmptyStoreDoesNotRequireMetadataForIndexZero()
    {
        var options = CreateOptions();
        await using (var wal = new WriteAheadLog(options, IStateMachine.CreateNoOp()))
        {
            await wal.InitializeAsync(TestToken);
            Equal(0L, wal.LastEntryIndex);
        }

        await using var reopened = new WriteAheadLog(options, IStateMachine.CreateNoOp());
        await reopened.InitializeAsync(TestToken);
        Equal(0L, reopened.LastEntryIndex);
        Equal(1L, await reopened.AppendAsync(new TestLogEntry("first") { Term = 1L }, TestToken));
    }

    private static WriteAheadLog.Options CreateOptions()
        => WriteAheadLogDurabilityTests.CreateOptions(GetTempPath(),
            WriteAheadLog.MemoryManagementStrategy.PrivateMemory, false, 0,
            WriteAheadLog.IntegrityHashAlgorithm.None);

    private static async Task SeedSnapshotBoundaryAsync(WriteAheadLog.Options options)
    {
        await WriteAheadLogDurabilityTests.SeedPrefixAsync(options);
        await using var wal = new WriteAheadLog(options, new SnapshotStateMachine());
        await wal.InitializeAsync(TestToken);
        await wal.AppendAsync(new EmptySnapshot(SnapshotIndex), SnapshotIndex, TestToken);
        Equal(SnapshotIndex, wal.LastEntryIndex);
        Equal(SnapshotIndex, wal.LastCommittedEntryIndex);
    }

    private sealed class SnapshotStateMachine(long snapshotIndex = 0L) : IStateMachine
    {
        private ISnapshot snapshot = snapshotIndex > 0L ? new EmptySnapshot(snapshotIndex) : null;

        ISnapshot ISnapshotManager.Snapshot => snapshot;

        ValueTask<long> IStateMachine.ApplyAsync(LogEntry entry, CancellationToken token)
        {
            if (entry.IsSnapshot)
                snapshot = new EmptySnapshot(entry.Index);

            return ValueTask.FromResult(entry.Index);
        }

        ValueTask ISnapshotManager.ReclaimGarbageAsync(long watermark, CancellationToken token)
            => ValueTask.CompletedTask;
    }

    private sealed class EmptySnapshot(long index) : ISnapshot
    {
        long ISnapshot.Index => index;

        long IRaftLogEntry.Term => 1L;

        bool IDataTransferObject.IsReusable => true;

        long? IDataTransferObject.Length => 0L;

        ValueTask IDataTransferObject.WriteToAsync<TWriter>(TWriter writer, CancellationToken token)
            => ValueTask.CompletedTask;
    }
}
