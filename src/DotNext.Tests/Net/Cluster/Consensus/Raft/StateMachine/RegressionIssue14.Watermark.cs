using System.Runtime.CompilerServices;

namespace DotNext.Net.Cluster.Consensus.Raft.StateMachine;

using Threading;

public sealed partial class RegressionIssue14 : Test
{
    [Fact]
    public static async Task SnapshotInstallationDoesNotPublishFlushWatermarkBeforeCheckpoint()
    {
        var startup = new PausedFlusherContext();
        using var passes = new WriteAheadLogFlushTests.FlushPasses();
        var options = CreateOptions(TimeSpan.Zero, tags: passes.Tags);
        var machineLocation = new DirectoryInfo(GetTempPath());
        byte[] state = [1, 3, 5, 7];

        await using var machine = new ByteArrayStateMachine(machineLocation);
        await machine.RestoreAsync(TestToken);
        await using var wal = startup.CreateLog(options, machine);

        Task flush = Task.CompletedTask, installation = Task.CompletedTask, installedFlush = Task.CompletedTask;
        try
        {
            for (var i = 1; i <= 7; i++)
            {
                await wal.AppendAsync(
                    new BinaryLogEntry { Content = BitConverter.GetBytes(i), Term = 1L },
                    TestToken);
            }

            await wal.CommitAsync(7L, TestToken);
            Equal(0L, ReadCheckpoint(options.Location));

            flush = wal.FlushAsync(TestToken);
            False(flush.IsCompleted);

            QuiesceApplier(wal);
            passes.Enable();
            installation = Task.Run(
                () => wal.AppendAsync(new SnapshotEntry(state, SnapshotTerm), SnapshotIndex, TestToken).AsTask(),
                TestToken);
            await passes.First.Entered.Task.WaitAsync(TestToken);
            Equal(SnapshotIndex, wal.LastCommittedEntryIndex);
            Equal(SnapshotIndex, wal.LastAppliedIndex);
            Equal(0L, ReadCheckpoint(options.Location));
            False(installation.IsCompleted);

            var flushCompleted = WatermarkFlushCompleted(wal);
            NotNull(flushCompleted);
            flushCompleted.Signal(resumeAll: true);

            // The metric gate is before checkpoint persistence. Check the watermark directly
            // and issue a fresh public probe so no delay is needed to detect premature completion.
            Equal(1L, Volatile.Read(ref WatermarkNextUnflushedIndex(wal)));
            installedFlush = wal.FlushAsync(TestToken);
            False(installedFlush.IsCompleted);
            False(flush.IsCompleted);
            Equal(0L, ReadCheckpoint(options.Location));

            passes.First.Release();
            await installation.WaitAsync(TestToken);
            await Task.WhenAll(flush, installedFlush).WaitAsync(TestToken);
            Equal(SnapshotIndex, ReadCheckpoint(options.Location));

            startup.Resume();
            False(FlusherTask(wal).IsCompleted);
        }
        finally
        {
            passes.First.Release();
            passes.Second.Release();
            startup.Resume();
            await Task.WhenAll(installation, flush, installedFlush).ConfigureAwait(ConfigureAwaitOptions.SuppressThrowing);
        }
    }

    [UnsafeAccessor(UnsafeAccessorKind.Field, Name = "flushCompleted")]
    private static extern ref AsyncTrigger? WatermarkFlushCompleted(WriteAheadLog wal);

    [UnsafeAccessor(UnsafeAccessorKind.Field, Name = "nextUnflushedIndex")]
    private static extern ref long WatermarkNextUnflushedIndex(WriteAheadLog wal);
}
