using System.Collections.Concurrent;
using System.Diagnostics;
using System.Runtime.CompilerServices;
using System.Text;
using static System.Threading.Timeout;

namespace DotNext.Net.Cluster.Consensus.Raft.StateMachine;

using IO;
using AsyncAutoResetEventSlim = Threading.AsyncAutoResetEventSlim;

[Collection(TestCollections.WriteAheadLog)]
public sealed partial class RegressionIssue14 : Test
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

            QuiesceApplier(wal);
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

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public static async Task BackgroundFlushAfterSnapshotInstallIntoEmptyLog(bool flushOnCommit)
    {
        // Snapshot append persists in the foreground even before the worker observes any boundary.
        var startup = new PausedFlusherContext();
        var options = CreateOptions(flushOnCommit ? TimeSpan.Zero : TimeSpan.FromMilliseconds(50));
        var machineLocation = new DirectoryInfo(GetTempPath());
        byte[] state = [1, 2, 3, 4];

        await using var machine = new ByteArrayStateMachine(machineLocation);
        await machine.RestoreAsync(TestToken);
        await using var wal = startup.CreateLog(options, machine);

        QuiesceApplier(wal);
        await wal.AppendAsync(new SnapshotEntry(state, SnapshotTerm), SnapshotIndex, TestToken);
        Equal(SnapshotIndex, ReadCheckpoint(options.Location));
        startup.Resume();

        await wal.FlushAsync(TestToken);
        Equal(SnapshotIndex, ReadCheckpoint(options.Location));
        False(FlusherTask(wal).IsCompleted);

        // the worker keeps making progress on ordinary entries appended after the snapshot
        await wal.AppendAsync(new BinaryLogEntry { Content = new byte[] { 9 }, Term = SnapshotTerm }, TestToken);
        await wal.CommitAsync(SnapshotIndex + 1L, TestToken);
        await wal.FlushAsync(TestToken);
        Equal(SnapshotIndex + 1L, ReadCheckpoint(options.Location));
        False(FlusherTask(wal).IsCompleted);
    }

    [Theory]
    [InlineData(0, WriteAheadLog.IntegrityHashAlgorithm.None)]
    [InlineData(1, WriteAheadLog.IntegrityHashAlgorithm.None)]
    [InlineData(63, WriteAheadLog.IntegrityHashAlgorithm.None)]
    [InlineData(64, WriteAheadLog.IntegrityHashAlgorithm.None)]
    [InlineData(65, WriteAheadLog.IntegrityHashAlgorithm.None)]
    [InlineData(127, WriteAheadLog.IntegrityHashAlgorithm.None)]
    [InlineData(128, WriteAheadLog.IntegrityHashAlgorithm.None)]
    [InlineData(5, WriteAheadLog.IntegrityHashAlgorithm.Crc32)]
    [InlineData(128, WriteAheadLog.IntegrityHashAlgorithm.Crc64)]
    public static async Task SnapshotInstallAfterTailSurvivesRestart(
        int tailLength,
        WriteAheadLog.IntegrityHashAlgorithm hashAlgorithm)
    {
        var options = CreateOptions(InfiniteTimeSpan, hashAlgorithm);
        var machineLocation = new DirectoryInfo(GetTempPath());
        byte[] state = [1, 2, 3, 4];
        var postSnapshotPayload = Encoding.UTF8.GetBytes("written after the snapshot");

        await using (var machine = new ByteArrayStateMachine(machineLocation))
        {
            await machine.RestoreAsync(TestToken);
            await using var wal = new WriteAheadLog(options, machine);

            for (var i = 1; i <= tailLength; i++)
            {
                await wal.AppendAsync(
                    new BinaryLogEntry { Content = BitConverter.GetBytes(i), Term = 1L },
                    TestToken);
            }

            if (tailLength > 0)
            {
                await wal.CommitAsync(tailLength, TestToken);
                await wal.FlushAsync(TestToken);
                await wal.WaitForApplyAsync(tailLength, TestToken);
            }

            QuiesceApplier(wal);
            await wal.AppendAsync(new SnapshotEntry(state, SnapshotTerm), SnapshotIndex, TestToken);
            await wal.FlushAsync(TestToken);

            // an ordinary entry appended right after the snapshot must not overlap the squashed data pages
            Equal(
                SnapshotIndex + 1L,
                await wal.AppendAsync(
                    new BinaryLogEntry { Content = postSnapshotPayload, Term = SnapshotTerm },
                    TestToken));
            await wal.CommitAsync(SnapshotIndex + 1L, TestToken);
            await wal.FlushAsync(TestToken);
            Equal(SnapshotIndex + 1L, ReadCheckpoint(options.Location));
        }

        await using (var machine = new ByteArrayStateMachine(machineLocation))
        {
            await machine.RestoreAsync(TestToken);
            await using var wal = new WriteAheadLog(options, machine);
            await wal.InitializeAsync(TestToken);

            Equal(SnapshotIndex + 1L, wal.LastCommittedEntryIndex);
            Equal(SnapshotIndex + 1L, wal.LastEntryIndex);
            Equal(state, machine.State);

            using var entries = await wal.ReadAsync(SnapshotIndex + 1L, SnapshotIndex + 1L, TestToken);
            Equal(postSnapshotPayload, await entries[^1].ToByteArrayAsync(token: TestToken));
        }
    }

    [Fact]
    public static async Task SquashedPagesAreReclaimedAndTheLogStillRecovers()
    {
        var options = CreateOptions(InfiniteTimeSpan);
        var machineLocation = new DirectoryInfo(GetTempPath());
        var metadataPages = new DirectoryInfo(Path.Combine(options.Location, "metadata"));
        byte[] state = [1, 2, 3, 4];

        await using (var machine = new ByteArrayStateMachine(machineLocation))
        {
            await machine.RestoreAsync(TestToken);
            await using var wal = new WriteAheadLog(options, machine);

            for (var i = 1; i <= 5; i++)
            {
                await wal.AppendAsync(
                    new BinaryLogEntry { Content = BitConverter.GetBytes(i), Term = 1L },
                    TestToken);
            }

            await wal.CommitAsync(5L, TestToken);
            await wal.FlushAsync(TestToken);
            await wal.WaitForApplyAsync(5L, TestToken);
            True(File.Exists(Path.Combine(metadataPages.FullName, "0")));

            QuiesceApplier(wal);
            await wal.AppendAsync(new SnapshotEntry(state, SnapshotTerm), SnapshotIndex, TestToken);
            await wal.FlushAsync(TestToken);

            // The cleaner runs detached from the flush pass, so wait for the squashed metadata page to disappear.
            await SpinWaitAsync(() => !File.Exists(Path.Combine(metadataPages.FullName, "0")));
        }

        await using (var machine = new ByteArrayStateMachine(machineLocation))
        {
            await machine.RestoreAsync(TestToken);
            await using var wal = new WriteAheadLog(options, machine);
            await wal.InitializeAsync(TestToken);

            Equal(SnapshotIndex, wal.LastCommittedEntryIndex);
            Equal(state, machine.State);
        }
    }

    [Fact]
    public static async Task InstallationRacingAFlushPassKeepsTheCheckpointConsistent()
    {
        var startup = new PausedFlusherContext();
        var options = CreateOptions(TimeSpan.Zero);
        var machineLocation = new DirectoryInfo(GetTempPath());
        byte[] state = [1, 2, 3, 4];

        await using var inner = new ByteArrayStateMachine(machineLocation);
        await inner.RestoreAsync(TestToken);
        var machine = new GatedSnapshotStateMachine(inner);
        await using var wal = startup.CreateLog(options, machine);

        for (var i = 1; i <= 5; i++)
        {
            await wal.AppendAsync(
                new BinaryLogEntry { Content = BitConverter.GetBytes(i), Term = 1L },
                TestToken);
        }

        // arms the flush trigger while the worker is still parked at its initial yield
        await wal.CommitAsync(5L, TestToken);
        await wal.WaitForApplyAsync(5L, TestToken);
        QuiesceApplier(wal);

        var installation = wal.AppendAsync(new SnapshotEntry(state, SnapshotTerm), SnapshotIndex, TestToken).AsTask();
        await machine.Entered.Task.WaitAsync(TestToken);

        // Resume() runs the worker inline until it suspends, and the installation holds the overwrite lock,
        // so the worker is parked at the read lock with the pre-installation boundaries in sight.
        startup.Resume();
        Equal(0L, ReadCheckpoint(options.Location));

        machine.Release.SetResult();
        await installation;

        await wal.FlushAsync(TestToken);
        Equal(SnapshotIndex, ReadCheckpoint(options.Location));
        False(FlusherTask(wal).IsCompleted);
    }

    [UnsafeAccessor(UnsafeAccessorKind.Field, Name = "flusherTask")]
    private static extern ref Task FlusherTask(WriteAheadLog wal);

    [UnsafeAccessor(UnsafeAccessorKind.Field, Name = "applyTrigger")]
    private static extern ref AsyncAutoResetEventSlim ApplyTrigger(WriteAheadLog wal);

    [UnsafeAccessor(UnsafeAccessorKind.Field, Name = "state")]
    private static extern ref int TriggerState(AsyncAutoResetEventSlim trigger);

    // Park the applier first so that this suite stays focused on the flushing boundary and does not depend on
    // the applier's progress.
    private static void QuiesceApplier(WriteAheadLog wal)
    {
        // Read the trigger's CallbackAttachedState without signaling or replacing it.
        True(SpinWait.SpinUntil(
            () => Volatile.Read(ref TriggerState(ApplyTrigger(wal))) is 2,
            TimeSpan.FromSeconds(10)));
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

    private static WriteAheadLog.Options CreateOptions(
        TimeSpan interval,
        WriteAheadLog.IntegrityHashAlgorithm hashAlgorithm = WriteAheadLog.IntegrityHashAlgorithm.None,
        TagList tags = default)
        => new()
        {
            Location = GetTempPath(),
            MemoryManagement = WriteAheadLog.MemoryManagementStrategy.PrivateMemory,
            FlushInterval = interval,
            HashAlgorithm = hashAlgorithm,
            MeasurementTags = tags,
        };

    private static long ReadCheckpoint(string location)
        => WalCheckpointAssertions.ReadCommittedCheckpoint(location);

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

    private sealed class GatedSnapshotStateMachine(ByteArrayStateMachine inner) : IStateMachine
    {
        internal readonly TaskCompletionSource Entered = new(TaskCreationOptions.RunContinuationsAsynchronously);
        internal readonly TaskCompletionSource Release = new(TaskCreationOptions.RunContinuationsAsynchronously);

        public ISnapshot Snapshot => inner.As<IStateMachine>().Snapshot;

        public async ValueTask<long> ApplyAsync(LogEntry entry, CancellationToken token)
        {
            if (entry.IsSnapshot)
            {
                Entered.TrySetResult();
                await Release.Task.WaitAsync(token);
            }

            return await inner.As<IStateMachine>().ApplyAsync(entry, token);
        }

        public ValueTask ReclaimGarbageAsync(long watermark, CancellationToken token)
            => inner.As<IStateMachine>().ReclaimGarbageAsync(watermark, token);
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

    private sealed class PausedFlusherContext : SynchronizationContext
    {
        // Even the periodic worker yields once before its initial, untimed pass.
        private readonly ConcurrentQueue<(SendOrPostCallback Callback, object State)> callbacks = new();

        public override void Post(SendOrPostCallback d, object state)
            => callbacks.Enqueue((d, state));

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
}
