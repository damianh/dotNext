using System.Buffers.Binary;
using static System.Threading.Timeout;

namespace DotNext.Net.Cluster.Consensus.Raft.StateMachine;

using IO;

[Collection(TestCollections.WriteAheadLog)]
public sealed class RegressionIssue14 : Test
{
    private const long SnapshotIndex = 1000L;
    private const long SnapshotTerm = 7L;

    [Fact]
    public static async Task ExplicitFlushAfterSnapshotInstallIntoEmptyLog()
    {
        var options = CreateOptions(InfiniteTimeSpan);
        var machineLocation = new DirectoryInfo(GetTempPath());
        byte[] state = [1, 2, 3, 4];

        await using (var machine = new ByteArrayStateMachine(machineLocation))
        {
            await machine.RestoreAsync(TestToken);
            await using var wal = new WriteAheadLog(options, machine);

            await wal.AppendAsync(new SnapshotEntry(state, SnapshotTerm), SnapshotIndex, TestToken);
            Equal(SnapshotIndex, wal.LastCommittedEntryIndex);
            Equal(SnapshotIndex, wal.LastEntryIndex);

            await wal.FlushAsync(TestToken);
            Equal(SnapshotIndex, ReadCheckpoint(options.Location));
        }

        await using (var machine = new ByteArrayStateMachine(machineLocation))
        {
            await machine.RestoreAsync(TestToken);
            await using var wal = new WriteAheadLog(options, machine);
            await wal.InitializeAsync(TestToken);

            Equal(SnapshotIndex, wal.LastCommittedEntryIndex);
            Equal(SnapshotIndex, wal.LastEntryIndex);
            Equal(state, machine.State);
        }
    }

    private static WriteAheadLog.Options CreateOptions(TimeSpan interval)
        => new()
        {
            Location = GetTempPath(),
            MemoryManagement = WriteAheadLog.MemoryManagementStrategy.PrivateMemory,
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

    private sealed class ByteArrayStateMachine(DirectoryInfo location) : SimpleStateMachine(location)
    {
        internal byte[] State { get; private set; } = [];

        protected override async ValueTask RestoreAsync(FileInfo snapshotFile, CancellationToken token)
            => State = await File.ReadAllBytesAsync(snapshotFile.FullName, token);

        protected override ValueTask PersistAsync(IAsyncBinaryWriter writer, CancellationToken token)
            => writer.Invoke(State, token);

        protected override ValueTask<bool> ApplyAsync(LogEntry entry, CancellationToken token)
            => ValueTask.FromResult(false);
    }

    private sealed class SnapshotEntry(byte[] content, long term) : IRaftLogEntry
    {
        public long Term => term;

        public bool IsSnapshot => true;

        public long? Length => content.LongLength;

        public bool IsReusable => true;

        public ValueTask WriteToAsync<TWriter>(TWriter writer, CancellationToken token)
            where TWriter : IAsyncBinaryWriter
            => writer.Invoke(content, token);
    }
}
