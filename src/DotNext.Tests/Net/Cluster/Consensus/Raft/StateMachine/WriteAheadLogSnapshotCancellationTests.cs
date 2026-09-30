using System.Text;

namespace DotNext.Net.Cluster.Consensus.Raft.StateMachine;

using IO;

[Collection(TestCollections.WriteAheadLog)]
public sealed class WriteAheadLogSnapshotCancellationTests : Test
{
    private const long SnapshotIndex = 10L;
    private const long SnapshotTerm = 2L;
    private static readonly byte[] SnapshotState = [1, 2, 3, 4, 5, 6, 7, 8];

    [Theory(Timeout = TestTimeouts.Default)]
    [InlineData(false, false)]
    [InlineData(false, true)]
    [InlineData(true, false)]
    [InlineData(true, true)]
    public static async Task CancellationDuringSnapshotTransferKeepsLogUsable(bool blocked, bool leadershipLoss)
    {
        var options = CreateOptions();
        var location = new DirectoryInfo(GetTempPath());

        await using (var machine = new ByteStateMachine(location))
        {
            await machine.RestoreAsync(TestToken);
            await using var wal = new WriteAheadLog(options, machine);
            await wal.InitializeAsync(TestToken);
            await SeedAsync(wal);

            using var caller = new CancellationTokenSource();
            using var leadership = new CancellationTokenSource();
            using var request = CancellationTokenSource.CreateLinkedTokenSource(caller.Token, leadership.Token);
            var source = leadershipLoss ? leadership : caller;
            var started = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            var entry = new ByteSnapshotEntry(SnapshotState, SnapshotTerm, async token =>
            {
                started.TrySetResult();
                if (blocked)
                {
                    await Task.Delay(Timeout.InfiniteTimeSpan, token);
                }
                else
                {
                    await source.CancelAsync();
                    token.ThrowIfCancellationRequested();
                }
            });

            var install = wal.AppendAsync(entry, SnapshotIndex, request.Token).AsTask();
            if (blocked)
            {
                await started.Task.WaitAsync(TestToken);
                await source.CancelAsync();
            }

            var error = await ThrowsAnyAsync<OperationCanceledException>(() => install);
            Equal(request.Token, error.CancellationToken);

            await AssertUntouchedAsync(wal, machine, location);

            // the leader retransmits the same snapshot
            await wal.AppendAsync(new ByteSnapshotEntry(SnapshotState, SnapshotTerm), SnapshotIndex, TestToken);
            AssertInstalled(wal, machine);
            await AssertProgressAsync(wal, SnapshotIndex + 1L, "after snapshot");
        }

        await using (var machine = new ByteStateMachine(location))
        {
            await machine.RestoreAsync(TestToken);
            Equal(SnapshotState, machine.State);
            await using var wal = new WriteAheadLog(options, machine);
            await wal.InitializeAsync(TestToken);

            Equal(SnapshotIndex + 1L, wal.LastEntryIndex);
            Equal(SnapshotIndex + 1L, wal.LastCommittedEntryIndex);
            await AssertProgressAsync(wal, SnapshotIndex + 2L, "after reopen");
        }
    }

    [Fact(Timeout = TestTimeouts.Default)]
    public static async Task CancellationAfterRestoreStartsDoesNotInterruptInstall()
    {
        var options = CreateOptions();
        var location = new DirectoryInfo(GetTempPath());
        var restoring = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);

        await using (var machine = new ByteStateMachine(location))
        {
            await machine.RestoreAsync(TestToken);
            machine.BeforeRestore = async token =>
            {
                restoring.TrySetResult();
                await release.Task.WaitAsync(token);
            };

            await using var wal = new WriteAheadLog(options, machine);
            await wal.InitializeAsync(TestToken);
            await SeedAsync(wal);

            using var request = new CancellationTokenSource();
            var install = wal.AppendAsync(new ByteSnapshotEntry(SnapshotState, SnapshotTerm), SnapshotIndex, request.Token).AsTask();
            await restoring.Task.WaitAsync(TestToken);
            await request.CancelAsync();
            release.SetResult();
            await install.WaitAsync(TestToken);

            AssertInstalled(wal, machine);
            await AssertProgressAsync(wal, SnapshotIndex + 1L, "after snapshot");
        }

        await using (var machine = new ByteStateMachine(location))
        {
            await machine.RestoreAsync(TestToken);
            Equal(SnapshotState, machine.State);
            await using var wal = new WriteAheadLog(options, machine);
            await wal.InitializeAsync(TestToken);
            Equal(SnapshotIndex + 1L, wal.LastCommittedEntryIndex);
        }
    }

    [Fact(Timeout = TestTimeouts.Default)]
    public static async Task RestoreFailureFaultsLog()
    {
        var options = CreateOptions();
        var failure = new IOException("Injected restore failure.");
        await using var machine = new ByteStateMachine(new(GetTempPath()))
        {
            BeforeRestore = _ => ValueTask.FromException(failure),
        };
        await machine.RestoreAsync(TestToken);
        await using var wal = new WriteAheadLog(options, machine);
        await wal.InitializeAsync(TestToken);
        await SeedAsync(wal);

        Same(failure, await ThrowsAsync<IOException>(
            () => wal.AppendAsync(new ByteSnapshotEntry(SnapshotState, SnapshotTerm), SnapshotIndex, TestToken).AsTask()));
        await AssertFaultedAsync(wal, failure);
    }

    [Fact(Timeout = TestTimeouts.Default)]
    public static async Task CancellationThrownByRestoreFaultsLog()
    {
        // The request is canceled as well, so the failure must not be mistaken for routine transfer cancellation.
        using var request = new CancellationTokenSource();
        var options = CreateOptions();
        await using var machine = new ByteStateMachine(new(GetTempPath()))
        {
            BeforeRestore = async _ =>
            {
                await request.CancelAsync();
                throw new OperationCanceledException(request.Token);
            },
        };
        await machine.RestoreAsync(TestToken);
        await using var wal = new WriteAheadLog(options, machine);
        await wal.InitializeAsync(TestToken);
        await SeedAsync(wal);

        var error = await ThrowsAsync<InvalidOperationException>(
            () => wal.AppendAsync(new ByteSnapshotEntry(SnapshotState, SnapshotTerm), SnapshotIndex, request.Token).AsTask());
        IsType<OperationCanceledException>(error.InnerException);
        await AssertFaultedAsync(wal, error);
    }

    private static WriteAheadLog.Options CreateOptions()
        => WriteAheadLogDurabilityTests.CreateOptions(GetTempPath(),
            WriteAheadLog.MemoryManagementStrategy.PrivateMemory, false, 0, WriteAheadLog.IntegrityHashAlgorithm.Crc64);

    // entries 1 (committed) and 2 (uncommitted), as on a follower that is behind
    private static async Task SeedAsync(WriteAheadLog wal)
    {
        await wal.AppendAsync(new TestLogEntry("one") { Term = 1L }, TestToken);
        await wal.AppendAsync(new TestLogEntry("two") { Term = 1L }, TestToken);
        await wal.CommitAsync(1L, TestToken);
        await wal.WaitForApplyAsync(1L, TestToken);
    }

    private static async Task AssertUntouchedAsync(WriteAheadLog wal, ByteStateMachine machine, DirectoryInfo location)
    {
        Equal(2L, wal.LastEntryIndex);
        Equal(1L, wal.LastCommittedEntryIndex);
        Equal(1L, wal.LastAppliedIndex);
        Empty(machine.State);
        Null(machine.As<IStateMachine>().Snapshot);
        Empty(location.EnumerateFileSystemInfos());
        using var entries = await wal.ReadAsync(1L, 2L, TestToken);
        Equal("one", await entries[0].ToStringAsync(Encoding.UTF8, token: TestToken));
        Equal("two", await entries[1].ToStringAsync(Encoding.UTF8, token: TestToken));
    }

    private static void AssertInstalled(WriteAheadLog wal, ByteStateMachine machine)
    {
        Equal(SnapshotIndex, wal.LastEntryIndex);
        Equal(SnapshotIndex, wal.LastCommittedEntryIndex);
        Equal(SnapshotIndex, wal.LastAppliedIndex);
        Equal(SnapshotState, machine.State);
        Equal(SnapshotIndex, machine.As<IStateMachine>().Snapshot.Index);
        Equal(SnapshotTerm, machine.As<IStateMachine>().Snapshot.Term);
    }

    private static async Task AssertProgressAsync(WriteAheadLog wal, long index, string payload)
    {
        Equal(index, await wal.AppendAsync(new TestLogEntry(payload) { Term = SnapshotTerm }, TestToken));
        await wal.CommitAsync(index, TestToken);
        await wal.WaitForApplyAsync(index, TestToken);
        await wal.FlushAsync(TestToken);
        using var entries = await wal.ReadAsync(index, index, TestToken);
        Equal(payload, await entries[0].ToStringAsync(Encoding.UTF8, token: TestToken));
    }

    private static async Task AssertFaultedAsync(WriteAheadLog wal, Exception failure)
    {
        Same(failure, (await ThrowsAsync<WriteAheadLog.InternalException>(
            () => wal.AppendAsync(new TestLogEntry("rejected") { Term = SnapshotTerm }, TestToken).AsTask())).InnerException);
        await ThrowsAsync<WriteAheadLog.InternalException>(() => wal.CommitAsync(2L, TestToken).AsTask());
    }
}
