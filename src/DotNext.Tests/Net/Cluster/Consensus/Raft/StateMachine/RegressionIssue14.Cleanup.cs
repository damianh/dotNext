namespace DotNext.Net.Cluster.Consensus.Raft.StateMachine;

using static System.Threading.Timeout;

public sealed partial class RegressionIssue14 : Test
{
    [Fact]
    public static async Task StartupSnapshotInstallSchedulesCleanupForSquashedPages()
    {
        var startup = new PausedFlusherContext();
        var seedOptions = CreateOptions(InfiniteTimeSpan);
        var options = new WriteAheadLog.Options
        {
            Location = seedOptions.Location,
            MemoryManagement = WriteAheadLog.MemoryManagementStrategy.PrivateMemory,
            FlushInterval = TimeSpan.Zero,
        };
        var machineLocation = new DirectoryInfo(GetTempPath());
        var metadataPages = new DirectoryInfo(Path.Combine(options.Location, "metadata"));
        byte[] state = [1, 2, 3, 4];

        await using (var seedMachine = new ByteArrayStateMachine(machineLocation))
        {
            await seedMachine.RestoreAsync(TestToken);
            await using var seedWal = new WriteAheadLog(seedOptions, seedMachine);

            for (var i = 1; i <= 5; i++)
            {
                await seedWal.AppendAsync(
                    new BinaryLogEntry { Content = BitConverter.GetBytes(i), Term = 1L },
                    TestToken);
            }

            await seedWal.CommitAsync(5L, TestToken);
            await seedWal.FlushAsync(TestToken);
            await seedWal.WaitForApplyAsync(5L, TestToken);
            True(File.Exists(Path.Combine(metadataPages.FullName, "0")));
        }

        await using var machine = new ByteArrayStateMachine(machineLocation);
        await machine.RestoreAsync(TestToken);
        await using var wal = startup.CreateLog(options, machine);
        await wal.InitializeAsync(TestToken);

        for (var i = 6; i <= 10; i++)
        {
            await wal.AppendAsync(
                new BinaryLogEntry { Content = BitConverter.GetBytes(i), Term = 1L },
                TestToken);
        }

        await wal.CommitAsync(10L, TestToken);
        await wal.WaitForApplyAsync(10L, TestToken);
        True(File.Exists(Path.Combine(metadataPages.FullName, "0")));

        QuiesceApplier(wal);
        await wal.AppendAsync(new SnapshotEntry(state, SnapshotTerm), SnapshotIndex, TestToken);
        startup.Resume();

        await wal.FlushAsync(TestToken);
        Equal(SnapshotIndex, ReadCheckpoint(options.Location));
        await SpinWaitAsync(() => !File.Exists(Path.Combine(metadataPages.FullName, "0")));
        False(FlusherTask(wal).IsCompleted);
    }
}
