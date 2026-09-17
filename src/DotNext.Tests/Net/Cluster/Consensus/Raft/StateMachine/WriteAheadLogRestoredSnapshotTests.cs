using System.Collections.Concurrent;
using System.IO.Hashing;
using System.Numerics;
using System.Text;

namespace DotNext.Net.Cluster.Consensus.Raft.StateMachine;

using IO;
using static WalCheckpointAssertions;

[Collection(TestCollections.WriteAheadLog)]
public sealed class WriteAheadLogRestoredSnapshotTests : Test
{
    private const long SnapshotTerm = 7L;

    [Theory]
    [InlineData(false, WriteAheadLog.IntegrityHashAlgorithm.None)]
    [InlineData(true, WriteAheadLog.IntegrityHashAlgorithm.None)]
    [InlineData(false, WriteAheadLog.IntegrityHashAlgorithm.Crc64)]
    [InlineData(true, WriteAheadLog.IntegrityHashAlgorithm.Crc64)]
    public static async Task RestoredSnapshotBoundaryIsPersistedBeforeReturn(bool append,
        WriteAheadLog.IntegrityHashAlgorithm hash)
    {
        var options = CreateOptions(GetTempPath(), 0, hash);
        await WriteAheadLogDurabilityTests.SeedPrefixAsync(options);
        var snapshotIndex = GetSnapshotIndex(hash);
        var boundaryPath = Path.Combine(options.Location, "metadata", "1");
        False(File.Exists(boundaryPath));

        await using (var wal = new WriteAheadLog(options, new SnapshotStateMachine(snapshotIndex)))
        {
            await wal.InitializeAsync(TestToken);
            False(File.Exists(boundaryPath));
            if (append)
                Equal(snapshotIndex + 1L, await wal.AppendAsync(new TestLogEntry("after snapshot") { Term = SnapshotTerm }, TestToken));
            else
                await wal.FlushAsync(TestToken);

            // Inspect disk before disposal, using private pages so neither a shared mapping nor
            // resource teardown can hide a missing flush. The append is on metadata page 2.
            AssertBoundary(options.Location, hash);
            Equal(snapshotIndex, ReadCommittedCheckpoint(options.Location));
            if (append)
                True(File.Exists(Path.Combine(options.Location, "metadata", "2")));
        }

        // A published checkpoint at or beyond the snapshot prevents the constructor from
        // fabricating its boundary again. Recovery must use the record asserted above.
        await using var reopened = new WriteAheadLog(options, new SnapshotStateMachine(snapshotIndex));
        await reopened.InitializeAsync(TestToken);
        Equal(snapshotIndex + (append ? 1L : 0L), reopened.LastEntryIndex);
        Equal(snapshotIndex, reopened.LastCommittedEntryIndex);
        AssertBoundary(options.Location, hash);
        if (append)
        {
            using var entries = await reopened.ReadAsync(snapshotIndex + 1L, snapshotIndex + 1L, TestToken);
            Equal("after snapshot", await entries[0].ToStringAsync(Encoding.UTF8, token: TestToken));
        }
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public static async Task FailedBoundaryFlushDoesNotPublishSnapshot(bool append)
    {
        const WriteAheadLog.IntegrityHashAlgorithm hash = WriteAheadLog.IntegrityHashAlgorithm.None;
        var options = CreateOptions(GetTempPath(), 0, hash);
        await WriteAheadLogDurabilityTests.SeedPrefixAsync(options);
        var checkpoint = ReadCheckpointBytes(options.Location);
        var boundaryPath = Path.Combine(options.Location, "metadata", "1");
        await using (var wal = new WriteAheadLog(options, new SnapshotStateMachine(GetSnapshotIndex(hash))))
        {
            // Fail only the boundary page's write, not the first appended entry's page.
            Directory.CreateDirectory(boundaryPath);
            try
            {
                if (append)
                {
                    var error = await ThrowsAnyAsync<Exception>(() => wal.AppendAsync(new TestLogEntry("after snapshot"), TestToken).AsTask());
                    True(error is IOException or UnauthorizedAccessException);
                }
                else
                    await ThrowsAsync<WriteAheadLog.InternalException>(() => wal.FlushAsync(TestToken));

                Equal(GetSnapshotIndex(hash), wal.LastEntryIndex);
                Equal(checkpoint, ReadCheckpointBytes(options.Location));
                await ThrowsAsync<WriteAheadLog.InternalException>(() => wal.FlushAsync(TestToken));
            }
            finally
            {
                Directory.Delete(boundaryPath);
            }
        }

        await using var reopened = new WriteAheadLog(options, IStateMachine.CreateNoOp());
        await reopened.InitializeAsync(TestToken);
        Equal(1L, reopened.LastEntryIndex);
        Equal(1L, reopened.LastCommittedEntryIndex);
    }

    [Theory]
    [InlineData(1)]
    [InlineData(2)]
    public static async Task BackgroundFlushWaitsForRestoredSnapshotBoundary(int flushMode)
    {
        const WriteAheadLog.IntegrityHashAlgorithm hash = WriteAheadLog.IntegrityHashAlgorithm.None;
        var options = CreateOptions(GetTempPath(), 0, hash);
        await WriteAheadLogDurabilityTests.SeedPrefixAsync(options);
        var snapshotIndex = GetSnapshotIndex(hash);
        var startup = new PausedFlusherContext();
        await using (var wal = startup.CreateLog(CreateOptions(options.Location, flushMode, hash),
            new SnapshotStateMachine(snapshotIndex)))
        {
            try
            {
                var flushing = wal.FlushAsync(TestToken);
                False(flushing.IsCompleted);
                Equal(1L, ReadCommittedCheckpoint(options.Location));
                False(File.Exists(Path.Combine(options.Location, "metadata", "1")));
                startup.Resume();
                await flushing.WaitAsync(DefaultTimeout, TestToken);
                AssertBoundary(options.Location, hash);
                Equal(snapshotIndex, ReadCommittedCheckpoint(options.Location));
            }
            finally
            {
                startup.Resume();
            }
        }

        await using var reopened = new WriteAheadLog(options, new SnapshotStateMachine(snapshotIndex));
        await reopened.InitializeAsync(TestToken);
        Equal(snapshotIndex, reopened.LastEntryIndex);
        Equal(snapshotIndex, reopened.LastCommittedEntryIndex);
    }

    private static WriteAheadLog.Options CreateOptions(string location, int flushMode, WriteAheadLog.IntegrityHashAlgorithm hash)
        => WriteAheadLogDurabilityTests.CreateOptions(location,
            WriteAheadLog.MemoryManagementStrategy.PrivateMemory, false, flushMode, hash);

    private static int MetadataPageSize => int.Max(4096, Environment.SystemPageSize);

    private static int GetRecordSize(WriteAheadLog.IntegrityHashAlgorithm hash)
        => checked((int)BitOperations.RoundUpToPowerOf2((uint)(LogEntryMetadata.Size
            + (hash is WriteAheadLog.IntegrityHashAlgorithm.Crc64 ? sizeof(ulong) : 0))));

    private static long GetSnapshotIndex(WriteAheadLog.IntegrityHashAlgorithm hash)
        => 2L * MetadataPageSize / GetRecordSize(hash) - 1L;

    private static void AssertBoundary(string location, WriteAheadLog.IntegrityHashAlgorithm hash)
    {
        var bytes = File.ReadAllBytes(Path.Combine(location, "metadata", "1"));
        Equal(MetadataPageSize, bytes.Length);
        var record = bytes.AsSpan(MetadataPageSize - GetRecordSize(hash), GetRecordSize(hash));
        var metadata = new LogEntryMetadata(record);
        Equal(SnapshotTerm, metadata.Term);
        Equal(0L, metadata.Length);
        Equal((ulong)Encoding.UTF8.GetByteCount("prefix"), metadata.Offset);
        False(metadata.HasPayload);
        if (hash is WriteAheadLog.IntegrityHashAlgorithm.Crc64)
            Equal(Crc64.Hash(record[..LogEntryMetadata.Size]),
                record.Slice(LogEntryMetadata.Size, sizeof(ulong)).ToArray());
    }

    private sealed class PausedFlusherContext : SynchronizationContext
    {
        private readonly ConcurrentQueue<(SendOrPostCallback Callback, object State)> callbacks = new();

        public override void Post(SendOrPostCallback d, object state) => callbacks.Enqueue((d, state));

        internal WriteAheadLog CreateLog(WriteAheadLog.Options options, IStateMachine machine)
        {
            var previous = Current;
            SetSynchronizationContext(this);
            try
            {
                return new(options, machine);
            }
            finally
            {
                SetSynchronizationContext(previous);
            }
        }

        internal void Resume()
        {
            while (callbacks.TryDequeue(out var callback))
                callback.Callback(callback.State);
        }
    }

    private sealed class SnapshotStateMachine(long index) : IStateMachine
    {
        public ISnapshot Snapshot { get; } = new EmptySnapshot(index);

        public ValueTask<long> ApplyAsync(LogEntry entry, CancellationToken token)
            => ValueTask.FromResult(entry.Index);

        public ValueTask ReclaimGarbageAsync(long watermark, CancellationToken token)
            => ValueTask.CompletedTask;
    }

    private sealed class EmptySnapshot(long index) : ISnapshot
    {
        long ISnapshot.Index => index;

        long IRaftLogEntry.Term => SnapshotTerm;

        bool IDataTransferObject.IsReusable => true;

        long? IDataTransferObject.Length => 0L;

        ValueTask IDataTransferObject.WriteToAsync<TWriter>(TWriter writer, CancellationToken token)
            => ValueTask.CompletedTask;
    }
}
