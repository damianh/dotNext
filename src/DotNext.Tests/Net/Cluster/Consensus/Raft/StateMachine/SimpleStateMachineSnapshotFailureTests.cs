using System.Buffers.Binary;
using System.Collections.Concurrent;

namespace DotNext.Net.Cluster.Consensus.Raft.StateMachine;

using IO;

[Collection(TestCollections.WriteAheadLog)]
public sealed class SimpleStateMachineSnapshotFailureTests : Test
{
    public enum FailureKind
    {
        Faulted,
        Cancelled,
    }

    private const long IncomingIndex = 10L;
    private const long IncomingTerm = 2L;

    [Theory(Timeout = TestTimeouts.Default)]
    [InlineData(FailureKind.Faulted)]
    [InlineData(FailureKind.Cancelled)]
    public static async Task FailedBackgroundSnapshotDoesNotFaultLog(FailureKind kind)
    {
        var location = new DirectoryInfo(GetTempPath());
        await using var machine = new CountingStateMachine(location, FailOnCall(1, kind));
        await machine.RestoreAsync(TestToken);
        await using var wal = new WriteAheadLog(CreateOptions(), machine);
        await wal.InitializeAsync(TestToken);

        // the snapshot started by entry 2 fails, and entry 3 is the first apply that observes it
        for (var index = 1L; index <= 3L; index++)
            await AppendAndApplyAsync(wal, index);

        Equal(3L, wal.LastAppliedIndex);
        Equal(3L, machine.Count);
        Null(machine.As<IStateMachine>().Snapshot);
        False(File.Exists(Path.Combine(location.FullName, "2-1")));
        Empty(location.EnumerateFiles("*.tmp"));
        AssertReported(machine, kind);
    }

    [Theory(Timeout = TestTimeouts.Default)]
    [InlineData(FailureKind.Faulted)]
    [InlineData(FailureKind.Cancelled)]
    public static async Task SnapshotAfterFailedAttemptIsPublished(FailureKind kind)
    {
        var location = new DirectoryInfo(GetTempPath());
        await using var machine = new CountingStateMachine(location, FailOnCall(1, kind));
        await machine.RestoreAsync(TestToken);
        await using var wal = new WriteAheadLog(CreateOptions(), machine);
        await wal.InitializeAsync(TestToken);

        // entry 2 starts the failing snapshot, entry 4 starts the good one, and entry 5 publishes it
        for (var index = 1L; index <= 5L; index++)
            await AppendAndApplyAsync(wal, index);

        var snapshot = machine.As<IStateMachine>().Snapshot;
        Equal(4L, snapshot.Index);
        Equal(1L, snapshot.Term);
        Equal(4L, BinaryPrimitives.ReadInt64LittleEndian(await File.ReadAllBytesAsync(
            Path.Combine(location.FullName, "4-1"), TestToken)));
        False(File.Exists(Path.Combine(location.FullName, "2-1")));
        Empty(location.EnumerateFiles("*.tmp"));
        AssertReported(machine, kind);
    }

    [Theory(Timeout = TestTimeouts.Default)]
    [InlineData(FailureKind.Faulted)]
    [InlineData(FailureKind.Cancelled)]
    public static async Task IncomingSnapshotIsInstalledAfterFailedBackgroundSnapshot(FailureKind kind)
    {
        var location = new DirectoryInfo(GetTempPath());
        await using var machine = new CountingStateMachine(location, FailOnCall(1, kind));
        await machine.RestoreAsync(TestToken);
        await using var wal = new WriteAheadLog(CreateOptions(), machine);
        await wal.InitializeAsync(TestToken);

        // the snapshot started by entry 2 fails, and the leader sends its snapshot before the next apply
        await wal.AppendAsync(new TestLogEntry("one") { Term = 1L }, TestToken);
        await wal.AppendAsync(new TestLogEntry("two") { Term = 1L }, TestToken);
        await wal.CommitAsync(2L, TestToken);
        await wal.WaitForApplyAsync(2L, TestToken);

        await wal.AppendAsync(new ByteSnapshotEntry(BitConverter.GetBytes(IncomingIndex), IncomingTerm), IncomingIndex, TestToken);

        Equal(IncomingIndex, wal.LastAppliedIndex);
        Equal(IncomingIndex, machine.Count);
        Equal(IncomingIndex, machine.As<IStateMachine>().Snapshot.Index);
        Equal(IncomingTerm, machine.As<IStateMachine>().Snapshot.Term);
        True(File.Exists(Path.Combine(location.FullName, $"{IncomingIndex}-{IncomingTerm}")));

        await AppendAndApplyAsync(wal, IncomingIndex + 1L, IncomingTerm);
        Equal(IncomingIndex + 1L, machine.Count);
        Empty(location.EnumerateFiles("*.tmp"));
        AssertReported(machine, kind);
    }

    [Theory(Timeout = TestTimeouts.Default)]
    [InlineData(FailureKind.Faulted)]
    [InlineData(FailureKind.Cancelled)]
    public static async Task RestartRestoresLastGoodSnapshot(FailureKind kind)
    {
        var options = CreateOptions();
        var location = new DirectoryInfo(GetTempPath());

        // the snapshot of entry 2 is published by entry 3, the snapshot of entry 4 fails, and entry 5 drops it
        await using (var machine = new CountingStateMachine(location, FailOnCall(2, kind)))
        {
            await machine.RestoreAsync(TestToken);
            await using var wal = new WriteAheadLog(options, machine);
            await wal.InitializeAsync(TestToken);
            for (var index = 1L; index <= 5L; index++)
                await AppendAndApplyAsync(wal, index);

            await wal.FlushAsync(TestToken);
            Equal(2L, machine.As<IStateMachine>().Snapshot.Index);
            AssertReported(machine, kind);
        }

        Empty(location.EnumerateFiles("*.tmp"));
        False(File.Exists(Path.Combine(location.FullName, "4-1")));

        await using (var machine = new CountingStateMachine(location, null))
        {
            await machine.RestoreAsync(TestToken);
            Equal(2L, machine.As<IStateMachine>().Snapshot.Index);
            Equal(2L, machine.Count);

            await using var wal = new WriteAheadLog(options, machine);
            await wal.InitializeAsync(TestToken);
            await wal.WaitForApplyAsync(5L, TestToken);
            Equal(5L, machine.Count);
        }
    }

    [Fact(Timeout = TestTimeouts.Default)]
    public static async Task DisposalDuringBackgroundSnapshotIsUnchanged()
    {
        var location = new DirectoryInfo(GetTempPath());
        var persisting = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var machine = new CountingStateMachine(location, async (_, token) =>
        {
            persisting.SetResult();
            await Task.Delay(Timeout.InfiniteTimeSpan, token);
        });

        await machine.As<IStateMachine>().ApplyAsync(new LogEntry(term: 1L, index: 2L), TestToken);
        await persisting.Task.WaitAsync(TestToken);

        await machine.DisposeAsync().AsTask().WaitAsync(TestToken);

        Empty(location.EnumerateFileSystemInfos());
        Empty(machine.Failures);
        await ThrowsAsync<ObjectDisposedException>(
            machine.As<IStateMachine>().ApplyAsync(new LogEntry(term: 1L, index: 3L), TestToken).AsTask());
        await ThrowsAsync<ObjectDisposedException>(
            machine.As<IStateMachine>().ApplyAsync(
                new LogEntry(new ByteSnapshotEntry(BitConverter.GetBytes(IncomingIndex), IncomingTerm), IncomingIndex),
                TestToken).AsTask());
        Empty(machine.Failures);
    }

    private static void AssertReported(CountingStateMachine machine, FailureKind kind)
    {
        var failure = Single(machine.Failures);
        if (kind is FailureKind.Faulted)
            IsType<IOException>(failure);
        else
            IsAssignableFrom<OperationCanceledException>(failure);
    }

    private static Func<int, CancellationToken, ValueTask> FailOnCall(int call, FailureKind kind)
        => async (current, _) =>
        {
            if (current != call)
                return;

            await Task.Yield();
            throw kind is FailureKind.Faulted
                ? new IOException("Injected background snapshot failure.")
                : new OperationCanceledException();
        };

    private static WriteAheadLog.Options CreateOptions()
        => WriteAheadLogDurabilityTests.CreateOptions(GetTempPath(),
            WriteAheadLog.MemoryManagementStrategy.PrivateMemory, false, 0, WriteAheadLog.IntegrityHashAlgorithm.Crc64);

    private static async Task AppendAndApplyAsync(WriteAheadLog wal, long index, long term = 1L)
    {
        Equal(index, await wal.AppendAsync(new TestLogEntry($"entry {index}") { Term = term }, TestToken));
        await wal.CommitAsync(index, TestToken);
        await wal.WaitForApplyAsync(index, TestToken);
    }

    // counts applied entries, and takes a snapshot at every even index
    private sealed class CountingStateMachine(
        DirectoryInfo location,
        Func<int, CancellationToken, ValueTask> beforePersist)
        : SimpleStateMachine(location)
    {
        private int persistCalls;

        internal long Count { get; private set; }

        internal ConcurrentQueue<Exception> Failures { get; } = new();

        protected override void OnSnapshotFailed(Exception failure) => Failures.Enqueue(failure);

        protected override async ValueTask RestoreAsync(FileInfo snapshotFile, CancellationToken token)
            => Count = BinaryPrimitives.ReadInt64LittleEndian(await File.ReadAllBytesAsync(snapshotFile.FullName, token));

        protected override async ValueTask PersistAsync(IAsyncBinaryWriter writer, CancellationToken token)
        {
            ReadOnlyMemory<byte> bytes = BitConverter.GetBytes(Count);
            var call = Interlocked.Increment(ref persistCalls);
            if (beforePersist is not null)
                await beforePersist(call, token);

            await writer.Invoke(bytes, token);
        }

        protected override ValueTask<bool> ApplyAsync(LogEntry entry, CancellationToken token)
        {
            Count++;
            return ValueTask.FromResult(entry.Index % 2L is 0L);
        }
    }
}
