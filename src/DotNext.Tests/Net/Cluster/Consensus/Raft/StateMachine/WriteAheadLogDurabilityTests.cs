using System.Reflection;
using System.Text;
using System.Diagnostics.Metrics;

namespace DotNext.Net.Cluster.Consensus.Raft.StateMachine;

using IO;
using IO.Log;

[Collection(TestCollections.WriteAheadLog)]
public sealed class WriteAheadLogDurabilityTests : Test
{
    public static TheoryData<WriteAheadLog.MemoryManagementStrategy, bool, int, WriteAheadLog.IntegrityHashAlgorithm> RecoveryModes
    {
        get
        {
            var result = new TheoryData<WriteAheadLog.MemoryManagementStrategy, bool, int, WriteAheadLog.IntegrityHashAlgorithm>();
            foreach (var strategy in Enum.GetValues<WriteAheadLog.MemoryManagementStrategy>())
            foreach (var direct in new[] { false, true })
            foreach (var mode in new[] { 0, 1, 2 })
            foreach (var hash in new[] { WriteAheadLog.IntegrityHashAlgorithm.None, WriteAheadLog.IntegrityHashAlgorithm.Crc64 })
            {
                if (direct && (strategy is WriteAheadLog.MemoryManagementStrategy.SharedMemory
                    || (!OperatingSystem.IsWindows() && !OperatingSystem.IsLinux())))
                    continue;
                result.Add(strategy, direct, mode, hash);
            }

            return result;
        }
    }

    [Theory]
    [MemberData(nameof(RecoveryModes))]
    public static async Task AppendsSurviveOrderlyRestart(WriteAheadLog.MemoryManagementStrategy strategy,
        bool direct, int flushMode, WriteAheadLog.IntegrityHashAlgorithm hash)
    {
        var location = GetTempPath();
        await SeedPrefixAsync(CreateOptions(location, strategy, direct, 0, hash));
        var options = CreateOptions(location, strategy, direct, flushMode, hash);
        await using (var wal = new WriteAheadLog(options, IStateMachine.CreateNoOp()))
        {
            await wal.InitializeAsync(TestToken);
            await wal.AppendAsync(new TestLogEntry("acknowledged tail") { Term = 1L }, TestToken);
            Equal(1L, wal.LastCommittedEntryIndex);
        }

        await AssertRecoveredAsync(options);
    }

    [Theory]
    [MemberData(nameof(RecoveryModes))]
    public static async Task AppendsSurviveProcessTermination(WriteAheadLog.MemoryManagementStrategy strategy,
        bool direct, int flushMode, WriteAheadLog.IntegrityHashAlgorithm hash)
    {
        var location = GetTempPath();
        await WalCrashWorker.KillAfterAcknowledgmentAsync(new(location, strategy, direct, flushMode, hash, "append"));
        await AssertRecoveredAsync(CreateOptions(location, strategy, direct, flushMode, hash));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public static async Task ReplacedTailDoesNotReturnAfterRestart(bool failReplacement)
    {
        var options = CreateOptions(GetTempPath(), WriteAheadLog.MemoryManagementStrategy.PrivateMemory,
            false, 0, WriteAheadLog.IntegrityHashAlgorithm.Crc64);
        await SeedPrefixAsync(options);
        await using (var wal = new WriteAheadLog(options, IStateMachine.CreateNoOp()))
        {
            await wal.AppendAsync(new TestLogEntry("old two") { Term = 1L }, TestToken);
            await wal.AppendAsync(new TestLogEntry("old three") { Term = 1L }, TestToken);
            if (failReplacement)
            {
                await using var entries = new LogEntryProducer<IRaftLogEntry>(
                    new IRaftLogEntry[] { new TestLogEntry("new two") { Term = 2L }, new FailedEntry(wal) });
                await ThrowsAsync<IOException>(() => wal.AppendAsync(entries, 2L, token: TestToken).AsTask());
            }
            else
            {
                await wal.AppendAsync(new TestLogEntry("new two") { Term = 2L }, 2L, TestToken);
            }
        }

        for (var attempt = 0; attempt < 2; attempt++)
        {
            await using var reopened = new WriteAheadLog(options, IStateMachine.CreateNoOp());
            await reopened.InitializeAsync(TestToken);
            Equal(failReplacement ? 3L : 2L, reopened.LastEntryIndex);
            Equal(1L, reopened.LastCommittedEntryIndex);
            using var entries = await reopened.ReadAsync(2L, reopened.LastEntryIndex, TestToken);
            Equal(failReplacement ? "old two" : "new two",
                await entries[0].ToStringAsync(Encoding.UTF8, token: TestToken));
            if (failReplacement)
                Equal("old three", await entries[1].ToStringAsync(Encoding.UTF8, token: TestToken));
        }
    }

    [Theory]
    [InlineData(WriteAheadLog.MemoryManagementStrategy.PrivateMemory)]
    [InlineData(WriteAheadLog.MemoryManagementStrategy.SharedMemory)]
    public static async Task InterruptedReplacementSurvivesProcessTermination(WriteAheadLog.MemoryManagementStrategy strategy)
    {
        var location = GetTempPath();
        await WalCrashWorker.KillAfterAcknowledgmentAsync(new(location, strategy, false, 0,
            WriteAheadLog.IntegrityHashAlgorithm.Crc64, "overwrite"));
        var options = CreateOptions(location, strategy, false, 0, WriteAheadLog.IntegrityHashAlgorithm.Crc64);
        await using var wal = new WriteAheadLog(options, IStateMachine.CreateNoOp());
        await wal.InitializeAsync(TestToken);
        Equal(3L, wal.LastEntryIndex);
        Equal(1L, wal.LastCommittedEntryIndex);
        using var entries = await wal.ReadAsync(2L, 3L, TestToken);
        Equal("old two", await entries[0].ToStringAsync(Encoding.UTF8, token: TestToken));
        Equal("old three", await entries[1].ToStringAsync(Encoding.UTF8, token: TestToken));
    }

    internal static async Task InterruptReplacementAsync(WriteAheadLog wal, Func<Task> ready)
    {
        await wal.AppendAsync(new TestLogEntry("old two") { Term = 1L }, TestToken);
        await wal.AppendAsync(new TestLogEntry("old three") { Term = 1L }, TestToken);
        await using var entries = new LogEntryProducer<IRaftLogEntry>(
            new IRaftLogEntry[] { new TestLogEntry("new two") { Term = 2L }, new FailedEntry(wal, ready) });
        await wal.AppendAsync(entries, 2L, token: TestToken);
    }

    [Fact]
    public static async Task RecoveredTailCanBeCommittedWithoutAnotherAppend()
    {
        var options = CreateOptions(GetTempPath(), WriteAheadLog.MemoryManagementStrategy.PrivateMemory,
            false, 0, WriteAheadLog.IntegrityHashAlgorithm.None);
        await SeedPrefixAsync(options);
        await using (var wal = new WriteAheadLog(options, IStateMachine.CreateNoOp()))
            await wal.AppendAsync(new TestLogEntry("acknowledged tail") { Term = 1L }, TestToken);
        await using (var wal = new WriteAheadLog(options, IStateMachine.CreateNoOp()))
        {
            await wal.InitializeAsync(TestToken);
            Equal(1L, wal.LastAppliedIndex);
            await wal.CommitAsync(2L, TestToken);
            await wal.WaitForApplyAsync(2L, TestToken);
            await wal.FlushAsync(TestToken);
        }
        await using var recovered = new WriteAheadLog(options, IStateMachine.CreateNoOp());
        await recovered.InitializeAsync(TestToken);
        Equal(2L, recovered.LastCommittedEntryIndex);
        Equal(2L, recovered.LastAppliedIndex);
    }

    [Fact]
    public static async Task StagedAppendIsNotPublishedToReplication()
    {
        var location = GetTempPath();
        var identity = Guid.NewGuid().ToString("N");
        var options = new WriteAheadLog.Options
        {
            Location = location,
            FlushInterval = Timeout.InfiniteTimeSpan,
            MemoryManagement = WriteAheadLog.MemoryManagementStrategy.PrivateMemory,
            MeasurementTags = new() { { "durability-test", identity } },
        };
        await SeedPrefixAsync(options);
        await using var wal = new WriteAheadLog(options, IStateMachine.CreateNoOp());
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        using var release = new ManualResetEventSlim();
        using var listener = new MeterListener();
        listener.InstrumentPublished = (instrument, observer) =>
        {
            if (instrument.Meter.Name == "DotNext.IO.WriteAheadLog" && instrument.Name == "entries-append-count")
                observer.EnableMeasurementEvents(instrument);
        };
        listener.SetMeasurementEventCallback<long>((_, _, tags, _) =>
        {
            foreach (var tag in tags)
            {
                if (tag.Key == "durability-test" && object.Equals(tag.Value, identity))
                {
                    entered.TrySetResult();
                    if (!release.Wait(DefaultTimeout))
                        throw new TimeoutException("Staged append was not released.");
                }
            }
        });
        listener.Start();
        var appending = Task.Run(async () =>
            await wal.AppendAsync(new TestLogEntry("acknowledged tail") { Term = 1L }, TestToken), TestToken);
        try
        {
            await entered.Task.WaitAsync(DefaultTimeout, TestToken);
            Equal(1L, wal.LastEntryIndex);
            False(appending.IsCompleted);
        }
        finally
        {
            release.Set();
            await appending.WaitAsync(DefaultTimeout, TestToken);
        }
        Equal(2L, wal.LastEntryIndex);
    }

    [Fact]
    public static async Task ImportPreservesUncommittedTail()
    {
        var sourceOptions = CreateOptions(GetTempPath(), WriteAheadLog.MemoryManagementStrategy.PrivateMemory,
            false, 0, WriteAheadLog.IntegrityHashAlgorithm.Crc64);
        var destinationOptions = CreateOptions(GetTempPath(), WriteAheadLog.MemoryManagementStrategy.PrivateMemory,
            false, 0, WriteAheadLog.IntegrityHashAlgorithm.None);
        await SeedPrefixAsync(sourceOptions);
        await using (var source = new WriteAheadLog(sourceOptions, IStateMachine.CreateNoOp()))
        await using (var destination = new WriteAheadLog(destinationOptions, IStateMachine.CreateNoOp()))
        {
            await source.InitializeAsync(TestToken);
            await source.AppendAsync(new TestLogEntry("acknowledged tail") { Term = 1L }, TestToken);
            await destination.ImportAsync(source, TestToken);
        }
        await AssertRecoveredAsync(destinationOptions);
    }

    [Fact]
    public static async Task InitializationDoesNotDeadlockBetweenAppends()
    {
        var options = CreateOptions(GetTempPath(), WriteAheadLog.MemoryManagementStrategy.PrivateMemory,
            false, 0, WriteAheadLog.IntegrityHashAlgorithm.Crc64);
        await using var wal = new WriteAheadLog(options, IStateMachine.CreateNoOp());
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        using var cancellation = new CancellationTokenSource(DefaultTimeout);
        var first = wal.AppendAsync(new PausedEntry(async () =>
        {
            entered.SetResult();
            await release.Task;
        }), cancellation.Token).AsTask();
        await entered.Task.WaitAsync(DefaultTimeout, TestToken);
        var initializing = wal.InitializeAsync(cancellation.Token);
        var second = wal.AppendAsync(new TestLogEntry("second") { Term = 1L }, cancellation.Token).AsTask();
        release.SetResult();
        await Task.WhenAll(first, initializing, second).WaitAsync(DefaultTimeout, TestToken);
        Equal(2L, wal.LastEntryIndex);
    }

    [Fact]
    public static async Task FailedReplacementRejectsQueuedReadersAndCommitters()
    {
        var options = CreateOptions(GetTempPath(), WriteAheadLog.MemoryManagementStrategy.PrivateMemory,
            false, 0, WriteAheadLog.IntegrityHashAlgorithm.Crc64);
        await SeedPrefixAsync(options);
        await using var wal = new WriteAheadLog(options, IStateMachine.CreateNoOp());
        await wal.AppendAsync(new TestLogEntry("old two") { Term = 1L }, TestToken);
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        await using var entries = new LogEntryProducer<IRaftLogEntry>(
            new IRaftLogEntry[] { new TestLogEntry("new two") { Term = 2L }, new FailedEntry(wal, async () =>
            {
                entered.SetResult();
                await release.Task;
            }) });
        var overwrite = wal.AppendAsync(entries, 2L, token: TestToken).AsTask();
        await entered.Task.WaitAsync(DefaultTimeout, TestToken);
        var read = wal.ReadAsync(2L, 2L, TestToken).AsTask();
        var commit = wal.CommitAsync(2L, TestToken).AsTask();
        try
        {
            False(read.IsCompleted);
            False(commit.IsCompleted);
        }
        finally
        {
            release.SetResult();
        }
        await ThrowsAsync<IOException>(overwrite);
        await ThrowsAsync<WriteAheadLog.InternalException>(read);
        await ThrowsAsync<WriteAheadLog.InternalException>(commit);
        Equal(1L, wal.LastCommittedEntryIndex);
    }

    [Theory]
    [InlineData(WriteAheadLog.MemoryManagementStrategy.SharedMemory)]
    [InlineData(WriteAheadLog.MemoryManagementStrategy.PrivateMemory)]
    public static async Task RecoveryAcrossDataAndMetadataPages(WriteAheadLog.MemoryManagementStrategy strategy)
    {
        var options = CreateOptions(GetTempPath(), strategy, false, 0, WriteAheadLog.IntegrityHashAlgorithm.Crc64);
        var payload = new string('x', Environment.SystemPageSize + 1);
        await using (var wal = new WriteAheadLog(options, IStateMachine.CreateNoOp(1024)))
        {
            var entries = Enumerable.Range(0, 257)
                .Select(_ => new TestLogEntry(payload) { Term = 1L }).ToArray();
            await using var producer = new LogEntryProducer<TestLogEntry>(entries);
            await wal.AppendAsync(producer, 1L, token: TestToken);
            await wal.CommitAsync(256L, TestToken);
            await wal.FlushAsync(TestToken);
        }
        await using var reopened = new WriteAheadLog(options, IStateMachine.CreateNoOp(1024));
        await reopened.InitializeAsync(TestToken);
        Equal(257L, reopened.LastEntryIndex);
        Equal(256L, reopened.LastCommittedEntryIndex);
        using var tail = await reopened.ReadAsync(257L, 257L, TestToken);
        Equal(payload, await tail[0].ToStringAsync(Encoding.UTF8, token: TestToken));
        Equal(258L, await reopened.AppendAsync(new TestLogEntry("next page") { Term = 2L }, TestToken));
    }

    internal static WriteAheadLog.Options CreateOptions(string location, WriteAheadLog.MemoryManagementStrategy strategy,
        bool direct, int flushMode, WriteAheadLog.IntegrityHashAlgorithm hash) => new()
        {
            Location = location,
            MemoryManagement = strategy,
            NoBuffering = direct,
            HashAlgorithm = hash,
            FlushInterval = flushMode switch { 0 => Timeout.InfiniteTimeSpan, 1 => TimeSpan.Zero, _ => TimeSpan.FromDays(1) },
        };

    internal static async Task SeedPrefixAsync(WriteAheadLog.Options options)
    {
        await using var wal = new WriteAheadLog(options, IStateMachine.CreateNoOp());
        await wal.AppendAsync(new TestLogEntry("prefix") { Term = 1L }, TestToken);
        await wal.CommitAsync(1L, TestToken);
        await wal.FlushAsync(TestToken);
    }

    private static async Task AssertRecoveredAsync(WriteAheadLog.Options options)
    {
        await using var recovered = new WriteAheadLog(options, IStateMachine.CreateNoOp());
        await recovered.InitializeAsync(TestToken);
        Equal(2L, recovered.LastEntryIndex);
        Equal(1L, recovered.LastCommittedEntryIndex);
        Equal(1L, recovered.LastAppliedIndex);
        using (var entries = await recovered.ReadAsync(2L, 2L, TestToken))
            Equal("acknowledged tail", await entries[0].ToStringAsync(Encoding.UTF8, token: TestToken));
        Equal(3L, await recovered.AppendAsync(new TestLogEntry("next") { Term = 2L }, TestToken));
    }

    private sealed class PausedEntry(Func<Task> pause) : IRaftLogEntry
    {
        public long Term => 1L;
        public bool IsSnapshot => false;
        public bool IsReusable => false;
        public long? Length => null;

        async ValueTask IDataTransferObject.WriteToAsync<TWriter>(TWriter writer, CancellationToken token)
        {
            await pause();
            await writer.WriteAsync(new byte[] { 1, 2, 3 }, token: token);
        }
    }

    private sealed class FailedEntry(WriteAheadLog wal, Func<Task> beforeFailure = null) : IRaftLogEntry
    {
        public long Term => 2L;
        public bool IsSnapshot => false;
        public bool IsReusable => false;
        public long? Length => null;

        async ValueTask IDataTransferObject.WriteToAsync<TWriter>(TWriter writer, CancellationToken token)
        {
            await writer.WriteAsync(new byte[] { 1, 2, 3 }, token: token);
            var flush = typeof(WriteAheadLog).GetMethod("Flush", BindingFlags.Instance | BindingFlags.NonPublic);
            await IsAssignableFrom<Task>(flush.Invoke(wal, [2L, 2L, token]));
            if (beforeFailure is not null)
                await beforeFailure();
            throw new IOException("Interrupted replacement payload.");
        }
    }

    [Theory]
    [InlineData(WriteAheadLog.IntegrityHashAlgorithm.None)]
    [InlineData(WriteAheadLog.IntegrityHashAlgorithm.Crc64)]
    public static async Task ExplicitlyPersistedUncommittedPagesSurviveRecovery(WriteAheadLog.IntegrityHashAlgorithm hash)
    {
        var options = new WriteAheadLog.Options
        {
            Location = GetTempPath(),
            MemoryManagement = WriteAheadLog.MemoryManagementStrategy.PrivateMemory,
            FlushInterval = Timeout.InfiniteTimeSpan,
            HashAlgorithm = hash,
        };
        await using (var wal = new WriteAheadLog(options, IStateMachine.CreateNoOp()))
        {
            await wal.AppendAsync(new TestLogEntry("prefix") { Term = 1L }, TestToken);
            await wal.CommitAsync(1L, TestToken);
            await wal.FlushAsync(TestToken);
            await wal.AppendAsync(new TestLogEntry("acknowledged tail") { Term = 1L }, TestToken);

            // Persist the actual pages without changing the committed checkpoint.
            var flush = typeof(WriteAheadLog).GetMethod("Flush", BindingFlags.Instance | BindingFlags.NonPublic);
            NotNull(flush);
            await IsAssignableFrom<Task>(flush.Invoke(wal, [2L, 2L, TestToken]));
            Equal(2L, wal.LastEntryIndex);
            Equal(1L, wal.LastCommittedEntryIndex);
        }

        await using var reopened = new WriteAheadLog(options, IStateMachine.CreateNoOp());
        await reopened.InitializeAsync(TestToken);
        TestContext.Current.TestOutputHelper.WriteLine(
            $"Explicitly persisted tail pages; hash={hash}; expected tail/commit=2/1; " +
            $"recovered={reopened.LastEntryIndex}/{reopened.LastCommittedEntryIndex}");
        Equal(2L, reopened.LastEntryIndex);
        Equal(1L, reopened.LastCommittedEntryIndex);
        Equal(1L, reopened.LastAppliedIndex);
        using var entries = await reopened.ReadAsync(2L, 2L, TestToken);
        Equal("acknowledged tail", await entries[0].ToStringAsync(Encoding.UTF8, token: TestToken));
    }
}
