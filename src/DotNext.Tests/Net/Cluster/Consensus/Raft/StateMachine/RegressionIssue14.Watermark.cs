using System.Runtime.CompilerServices;

namespace DotNext.Net.Cluster.Consensus.Raft.StateMachine;

using Threading;

public sealed partial class RegressionIssue14 : Test
{
    [Fact]
    public static async Task SnapshotInstallationDoesNotPublishFlushWatermarkBeforeCheckpoint()
    {
        var startup = new PausedFlusherContext();
        var options = CreateOptions(TimeSpan.Zero);
        var machineLocation = new DirectoryInfo(GetTempPath());
        byte[] state = [1, 3, 5, 7];

        await using var machine = new ByteArrayStateMachine(machineLocation);
        await machine.RestoreAsync(TestToken);
        await using var wal = startup.CreateLog(options, machine);

        Task flush = Task.CompletedTask;
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
            await wal.AppendAsync(new SnapshotEntry(state, SnapshotTerm), SnapshotIndex, TestToken);
            Equal(0L, ReadCheckpoint(options.Location));

            var flushCompleted = WatermarkFlushCompleted(wal);
            NotNull(flushCompleted);
            flushCompleted.Signal(resumeAll: true);

            var completed = await Task.WhenAny(flush, Task.Delay(200, TestToken));
            NotSame(flush, completed);
            Equal(0L, ReadCheckpoint(options.Location));

            startup.Resume();
            await flush.WaitAsync(TestToken);
            Equal(SnapshotIndex, ReadCheckpoint(options.Location));
            False(FlusherTask(wal).IsCompleted);
        }
        finally
        {
            startup.Resume();
        }
    }

    [UnsafeAccessor(UnsafeAccessorKind.Field, Name = "flushCompleted")]
    private static extern ref AsyncTrigger? WatermarkFlushCompleted(WriteAheadLog wal);
}
