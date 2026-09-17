using System.Buffers;
using System.Runtime.CompilerServices;
using System.Text;

namespace DotNext.Net.Cluster.Consensus.Raft.StateMachine;

using Buffers;
using IO;
using IO.Log;
using Threading;

[Collection(TestCollections.WriteAheadLog)]
public sealed class WriteAheadLogAppendFailureTests : Test
{
    public enum AppendKind
    {
        Buffered,
        OwnedBuffer,
        Unbuffered,
        Indexed,
        Overwrite,
        Snapshot,
        Producer,
        AppendAndCommit,
        AppendAndCommitSlow,
    }

    public static TheoryData<AppendKind> AppendKinds
    {
        get
        {
            var result = new TheoryData<AppendKind>();
            foreach (var kind in Enum.GetValues<AppendKind>())
                result.Add(kind);
            return result;
        }
    }

    [Theory]
    [MemberData(nameof(AppendKinds))]
    public static async Task CancellationWhileWaitingForPersistenceDoesNotPoisonLog(AppendKind kind)
    {
        var options = CreateOptions();
        await SeedAsync(options);
        await using var wal = new WriteAheadLog(options, IStateMachine.CreateNoOp());
        await wal.InitializeAsync(TestToken);
        using var cancellation = new CancellationTokenSource();
        var persistence = PersistenceLock(wal);
        persistence.TrackSuspendedCallers(() => "append");
        await persistence.AcquireAsync(TestToken);
        try
        {
            var append = AppendAsync(wal, kind, cancellation.Token);
            False(append.IsCompleted);
#if DEBUG
            True(SpinWait.SpinUntil(() => persistence.GetSuspendedCallers().Count is 1, DefaultTimeout));
#endif
            await cancellation.CancelAsync();
            var error = await ThrowsAnyAsync<OperationCanceledException>(
                () => append.WaitAsync(DefaultTimeout, TestToken));
            Equal(cancellation.Token, error.CancellationToken);
        }
        finally
        {
            persistence.Release();
        }

        await AssertUsableAsync(wal);
    }

    [Theory]
    [MemberData(nameof(AppendKinds))]
    public static async Task PreCanceledAppendDoesNotPoisonLog(AppendKind kind)
    {
        var options = CreateOptions();
        await SeedAsync(options);
        await using var wal = new WriteAheadLog(options, IStateMachine.CreateNoOp());
        using var cancellation = new CancellationTokenSource();
        cancellation.Cancel();
        await ThrowsAnyAsync<OperationCanceledException>(() => AppendAsync(wal, kind, cancellation.Token));
        await AssertUsableAsync(wal);
    }

    [Fact]
    public static async Task CancellationBeforeAppendLockReleasesOwnedBuffer()
    {
        var options = CreateOptions();
        await SeedAsync(options);
        await using var wal = new WriteAheadLog(options, IStateMachine.CreateNoOp());
        using var cancellation = new CancellationTokenSource();
        cancellation.Cancel();
        var buffer = new TrackedBuffer();
        await ThrowsAnyAsync<OperationCanceledException>(
            () => wal.AppendAsync(new OwnedEntry(buffer), cancellation.Token).AsTask());
        True(buffer.IsDisposed);
        await AssertUsableAsync(wal);
    }

    [Theory]
    [InlineData(-1L, false)]
    [InlineData(4L, false)]
    [InlineData(1L, false)]
    [InlineData(1L, true)]
    public static async Task InvalidSingleAppendDoesNotPoisonLog(long index, bool snapshot)
    {
        var options = CreateOptions();
        await SeedAsync(options);
        await using var wal = new WriteAheadLog(options, IStateMachine.CreateNoOp());
        var append = wal.AppendAsync(new Entry { IsSnapshot = snapshot }, index, TestToken).AsTask();
        if (index is < 0L or > 3L)
            await ThrowsAsync<ArgumentOutOfRangeException>(append);
        else
            await ThrowsAsync<InvalidOperationException>(append);
        await AssertUsableAsync(wal);
    }

    [Theory]
    [InlineData(AppendKind.Unbuffered)]
    [InlineData(AppendKind.Indexed)]
    [InlineData(AppendKind.Overwrite)]
    [InlineData(AppendKind.Producer)]
    [InlineData(AppendKind.AppendAndCommit)]
    [InlineData(AppendKind.AppendAndCommitSlow)]
    public static async Task InvalidEntryLengthBeforeMutationDoesNotPoisonLog(AppendKind kind)
    {
        var options = CreateOptions();
        await SeedAsync(options);
        await using var wal = new WriteAheadLog(options, IStateMachine.CreateNoOp());
        await ThrowsAsync<ArgumentException>(
            () => AppendAsync(wal, kind, TestToken, new InvalidLengthEntry { Term = 2L }));
        False(File.Exists(Path.Combine(options.Location, "overwrite")));
        await AssertUsableAsync(wal);
    }

    [Fact]
    public static async Task SnapshotWithoutIndexDoesNotPoisonLog()
    {
        var options = CreateOptions();
        await SeedAsync(options);
        await using var wal = new WriteAheadLog(options, IStateMachine.CreateNoOp());
        await ThrowsAsync<InvalidOperationException>(
            () => wal.AppendAsync(new Entry { IsSnapshot = true }, TestToken).AsTask());
        await AssertUsableAsync(wal);
    }

    [Theory]
    [InlineData(1L, false, false)]
    [InlineData(2L, false, true)]
    [InlineData(1L, true, true)]
    public static async Task InvalidProducerEntryBeforeMutationDoesNotPoisonLog(long index, bool skipCommitted, bool snapshot)
    {
        var options = CreateOptions();
        await SeedAsync(options);
        await using var wal = new WriteAheadLog(options, IStateMachine.CreateNoOp());
        IRaftLogEntry invalid = new Entry { IsSnapshot = snapshot };
        await using var entries = new LogEntryProducer<IRaftLogEntry>(skipCommitted
            ? new IRaftLogEntry[] { new Entry(), invalid }
            : new[] { invalid });
        await ThrowsAsync<InvalidOperationException>(
            () => wal.AppendAsync(entries, index, skipCommitted, TestToken).AsTask());
        False(File.Exists(Path.Combine(options.Location, "overwrite")));
        await AssertUsableAsync(wal);
    }

    [Theory]
    [InlineData(false, 0)]
    [InlineData(true, 0)]
    [InlineData(false, 1)]
    [InlineData(true, 1)]
    [InlineData(false, 2)]
    [InlineData(true, 2)]
    public static async Task ProducerFailureBeforeMutationDoesNotPoisonLog(bool cancel, int prefix)
    {
        var options = CreateOptions();
        await SeedAsync(options);
        await using var wal = new WriteAheadLog(options, IStateMachine.CreateNoOp());
        using var cancellation = new CancellationTokenSource();
        Exception failure = cancel ? new OperationCanceledException(cancellation.Token) : new IOException("Producer failed.");
        await using var entries = new FailingProducer(prefix is 0 ? [] : [new Entry()], () =>
        {
            if (cancel)
                cancellation.Cancel();
            return failure;
        });

        // A committed prefix is skipped; an uncommitted matching prefix is preserved during replication.
        var append = prefix is 2
            ? ((IAuditTrail<IRaftLogEntry>)wal).AppendAndCommitAsync(entries, 2L, false, 1L, cancellation.Token).AsTask()
            : wal.AppendAsync(entries, prefix is 1 ? 1L : 2L, skipCommitted: true, token: cancellation.Token).AsTask();
        if (cancel)
            Same(failure, await ThrowsAnyAsync<OperationCanceledException>(() => append));
        else
            Same(failure, await ThrowsAsync<IOException>(() => append));
        False(File.Exists(Path.Combine(options.Location, "overwrite")));
        await AssertUsableAsync(wal);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public static async Task MatchingPrefixThenInvalidEntryDoesNotPoisonLog(bool slow)
    {
        var options = CreateOptions();
        await SeedAsync(options);
        await using var wal = new WriteAheadLog(options, IStateMachine.CreateNoOp());
        await using var entries = new LogEntryProducer<IRaftLogEntry>(
            new IRaftLogEntry[] { new Entry(), new Entry { IsSnapshot = true } });
        await ThrowsAsync<InvalidOperationException>(() => ((IAuditTrail<IRaftLogEntry>)wal)
            .AppendAndCommitAsync(entries, 2L, false, slow ? 2L : 1L, TestToken).AsTask());
        False(File.Exists(Path.Combine(options.Location, "overwrite")));
        await AssertUsableAsync(wal);
    }

    [Theory]
    [InlineData(0)]
    [InlineData(1)]
    [InlineData(2)]
    public static async Task ProducerWithoutWritableEntriesLeavesLogUsable(int prefix)
    {
        var options = CreateOptions();
        await SeedAsync(options);
        await using var wal = new WriteAheadLog(options, IStateMachine.CreateNoOp());
        await using var entries = new LogEntryProducer<IRaftLogEntry>(
            prefix is 0 ? Array.Empty<IRaftLogEntry>() : new IRaftLogEntry[] { new Entry() });
        if (prefix is 2)
            await ((IAuditTrail<IRaftLogEntry>)wal).AppendAndCommitAsync(entries, 2L, false, 1L, TestToken);
        else
            await wal.AppendAsync(entries, prefix is 1 ? 1L : 2L, skipCommitted: true, token: TestToken);
        False(File.Exists(Path.Combine(options.Location, "overwrite")));
        await AssertUsableAsync(wal);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public static async Task ValidationFailureAfterMutationPoisonsLogAndRecoversTail(bool appendAndCommit)
    {
        var options = CreateOptions();
        await SeedAsync(options);
        await using (var wal = new WriteAheadLog(options, IStateMachine.CreateNoOp()))
        {
            await using var entries = new LogEntryProducer<IRaftLogEntry>(
                new IRaftLogEntry[] { new Entry { Term = 2L }, new Entry { IsSnapshot = true } });
            var append = appendAndCommit
                ? ((IAuditTrail<IRaftLogEntry>)wal).AppendAndCommitAsync(entries, 2L, false, 2L, TestToken).AsTask()
                : wal.AppendAsync(entries, 2L, token: TestToken).AsTask();
            var failure = await ThrowsAsync<InvalidOperationException>(() => append);
            await AssertPoisonedAsync(wal, failure);
        }

        await using var recovered = new WriteAheadLog(options, IStateMachine.CreateNoOp());
        await recovered.InitializeAsync(TestToken);
        await AssertUsableAsync(recovered);
    }

    [Theory]
    [InlineData(AppendKind.Unbuffered)]
    [InlineData(AppendKind.Indexed)]
    [InlineData(AppendKind.Overwrite)]
    [InlineData(AppendKind.Producer)]
    [InlineData(AppendKind.AppendAndCommit)]
    [InlineData(AppendKind.AppendAndCommitSlow)]
    public static async Task CancellationAfterWritingPayloadPoisonsLog(AppendKind kind)
    {
        var options = CreateOptions();
        await SeedAsync(options);
        await using var wal = new WriteAheadLog(options, IStateMachine.CreateNoOp());
        using var cancellation = new CancellationTokenSource();
        var entry = new Entry
        {
            Term = 2L,
            AfterWrite = () =>
            {
                cancellation.Cancel();
                cancellation.Token.ThrowIfCancellationRequested();
            },
        };
        var failure = await ThrowsAnyAsync<OperationCanceledException>(
            () => AppendAsync(wal, kind, cancellation.Token, entry));
        await AssertPoisonedAsync(wal, failure);
    }

    [Fact]
    public static async Task SnapshotApplicationFailurePoisonsLog()
    {
        var options = CreateOptions();
        await SeedAsync(options);
        var failure = new OperationCanceledException();
        var stateMachine = new FailingSnapshotStateMachine(failure);
        await using var wal = new WriteAheadLog(options, stateMachine);
        await wal.InitializeAsync(TestToken);
        Same(failure, await ThrowsAnyAsync<OperationCanceledException>(
            () => wal.AppendAsync(new Entry { IsSnapshot = true }, 3L, TestToken).AsTask()));
        True(stateMachine.SnapshotStarted);
        await AssertPoisonedAsync(wal, failure);
    }

    [Theory]
    [InlineData(0)]
    [InlineData(1)]
    [InlineData(2)]
    [InlineData(3)]
    public static async Task FastAppendFailureNotifiesCommittedPrefix(int failureKind)
    {
        var options = WriteAheadLogDurabilityTests.CreateOptions(GetTempPath(),
            WriteAheadLog.MemoryManagementStrategy.PrivateMemory, false, 1, WriteAheadLog.IntegrityHashAlgorithm.Crc64);
        await SeedAsync(options);
        await using var wal = new WriteAheadLog(options, IStateMachine.CreateNoOp());
        await wal.InitializeAsync(TestToken);
        await wal.AppendAsync(new TestLogEntry("third") { Term = 1L }, TestToken);
        WaitForWorkersParked(wal);
        using var cancellation = new CancellationTokenSource();
        await using ILogEntryProducer<IRaftLogEntry> entries = failureKind switch
        {
            0 => new FailingProducer([], () => new IOException("Producer failed.")),
            1 => LogEntryProducer<IRaftLogEntry>.Of(new Entry { IsSnapshot = true }),
            2 => new FailingProducer([], () =>
            {
                cancellation.Cancel();
                return new OperationCanceledException(cancellation.Token);
            }),
            _ => LogEntryProducer<IRaftLogEntry>.Of(new Entry()),
        };

        var append = ((IAuditTrail<IRaftLogEntry>)wal)
            .AppendAndCommitAsync(entries, failureKind is 3 ? 5L : 3L, false, 2L, cancellation.Token).AsTask();
        switch (failureKind)
        {
            case 0:
                await ThrowsAsync<IOException>(append);
                break;
            case 1:
                await ThrowsAsync<InvalidOperationException>(append);
                break;
            case 2:
                await ThrowsAnyAsync<OperationCanceledException>(() => append);
                break;
            default:
                await ThrowsAsync<ArgumentOutOfRangeException>(append);
                break;
        }

        await AssertCommittedPrefixNotifiedAsync(wal);
        False(File.Exists(Path.Combine(options.Location, "overwrite")));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public static async Task FastAppendLockCancellationNotifiesCommittedPrefix(bool overwrite)
    {
        var options = WriteAheadLogDurabilityTests.CreateOptions(GetTempPath(),
            WriteAheadLog.MemoryManagementStrategy.PrivateMemory, false, 1, WriteAheadLog.IntegrityHashAlgorithm.Crc64);
        await SeedAsync(options);
        await using var wal = new WriteAheadLog(options, IStateMachine.CreateNoOp());
        await wal.InitializeAsync(TestToken);
        await wal.AppendAsync(new TestLogEntry("third") { Term = 1L }, TestToken);
        WaitForWorkersParked(wal);
        using var cancellation = new CancellationTokenSource();
        var locks = LockManagerOf(wal);
        locks.TrackSuspendedCallers();
        if (overwrite)
            await locks.AcquireReadLockAsync(TestToken);
        else
            await locks.AcquireAppendLockAsync(TestToken);
        try
        {
            await using var entries = LogEntryProducer<IRaftLogEntry>.Of(new Entry { Term = 2L });
            var append = ((IAuditTrail<IRaftLogEntry>)wal)
                .AppendAndCommitAsync(entries, 3L, false, 2L, cancellation.Token).AsTask();
            False(append.IsCompleted);
            Equal(2L, wal.LastCommittedEntryIndex);
            var caller = overwrite ? "Overwrite Uncommitted Tail" : "Append and Commit";
            True(SpinWait.SpinUntil(() => locks.GetSuspendedCallers().Contains(caller), DefaultTimeout));
            cancellation.Cancel();
            await ThrowsAnyAsync<OperationCanceledException>(() => append.WaitAsync(DefaultTimeout, TestToken));
        }
        finally
        {
            if (overwrite)
                locks.ReleaseReadLock();
            else
                locks.ReleaseAppendLock();
        }

        await AssertCommittedPrefixNotifiedAsync(wal);
    }

    private static async Task AssertCommittedPrefixNotifiedAsync(WriteAheadLog wal)
    {
        Equal(3L, wal.LastEntryIndex);
        Equal(2L, wal.LastCommittedEntryIndex);
        // Both waits are passive with automatic flushing enabled; neither wakes an idle worker.
        await Task.WhenAll(wal.WaitForApplyAsync(2L, TestToken).AsTask(), wal.FlushAsync(TestToken))
            .WaitAsync(DefaultTimeout, TestToken);
        Equal(2L, wal.LastAppliedIndex);
        using var entries = await wal.ReadAsync(3L, 3L, TestToken);
        Equal("third", await entries[0].ToStringAsync(Encoding.UTF8, token: TestToken));
    }

    private static void WaitForWorkersParked(WriteAheadLog wal)
    {
        // AsyncAutoResetEventSlim.CallbackAttachedState means the worker is awaiting its trigger.
        True(SpinWait.SpinUntil(
            () => Volatile.Read(ref TriggerState(ApplyTrigger(wal))) is 2
                && Volatile.Read(ref TriggerState(FlushTrigger(wal))) is 2,
            DefaultTimeout));
    }

    private static WriteAheadLog.Options CreateOptions()
        => WriteAheadLogDurabilityTests.CreateOptions(GetTempPath(),
            WriteAheadLog.MemoryManagementStrategy.PrivateMemory, false, 0, WriteAheadLog.IntegrityHashAlgorithm.Crc64);

    private static async Task SeedAsync(WriteAheadLog.Options options)
    {
        await WriteAheadLogDurabilityTests.SeedPrefixAsync(options);
        await using var wal = new WriteAheadLog(options, IStateMachine.CreateNoOp());
        await wal.AppendAsync(new TestLogEntry("old tail") { Term = 1L }, TestToken);
    }

    private static async Task AssertUsableAsync(WriteAheadLog wal)
    {
        Equal(2L, wal.LastEntryIndex);
        Equal(1L, wal.LastCommittedEntryIndex);
        using (var entries = await wal.ReadAsync(1L, 2L, TestToken))
        {
            Equal("prefix", await entries[0].ToStringAsync(Encoding.UTF8, token: TestToken));
            Equal("old tail", await entries[1].ToStringAsync(Encoding.UTF8, token: TestToken));
        }
        Equal(3L, await wal.AppendAsync(new TestLogEntry("next") { Term = 2L }, TestToken));
        await wal.CommitAsync(3L, TestToken);
        await wal.WaitForApplyAsync(3L, TestToken);
        await wal.FlushAsync(TestToken);
    }

    private static async Task AssertPoisonedAsync(WriteAheadLog wal, Exception failure)
    {
        Equal(2L, wal.LastEntryIndex);
        Equal(1L, wal.LastCommittedEntryIndex);
        Same(failure, (await ThrowsAsync<WriteAheadLog.InternalException>(
            () => wal.AppendAsync(new Entry(), TestToken).AsTask())).InnerException);
        await ThrowsAsync<WriteAheadLog.InternalException>(() => wal.ReadAsync(2L, 2L, TestToken).AsTask());
        await ThrowsAsync<WriteAheadLog.InternalException>(() => wal.CommitAsync(2L, TestToken).AsTask());
    }

    private static async Task AppendAsync(WriteAheadLog wal, AppendKind kind, CancellationToken token, Entry entry = null)
    {
        entry ??= new Entry { Term = 2L };
        switch (kind)
        {
            case AppendKind.Buffered:
                await wal.AppendAsync(new BinaryLogEntry { Content = new byte[] { 1, 2, 3 }, Term = 2L }, token);
                break;
            case AppendKind.OwnedBuffer:
                await wal.AppendAsync(new OwnedEntry(), token);
                break;
            case AppendKind.Unbuffered:
                await wal.AppendAsync(entry, token);
                break;
            case AppendKind.Indexed:
                await wal.AppendAsync(entry, 3L, token);
                break;
            case AppendKind.Overwrite:
                await wal.AppendAsync(entry, 2L, token);
                break;
            case AppendKind.Snapshot:
                await wal.AppendAsync(new Entry { IsSnapshot = true }, 3L, token);
                break;
            default:
                await using (var entries = LogEntryProducer<IRaftLogEntry>.Of(entry))
                {
                    if (kind is AppendKind.Producer)
                        await wal.AppendAsync(entries, 2L, token: token);
                    else
                        await ((IAuditTrail<IRaftLogEntry>)wal).AppendAndCommitAsync(entries, 2L, false,
                            kind is AppendKind.AppendAndCommit ? 1L : 2L, token);
                }
                break;
        }
    }

    [UnsafeAccessor(UnsafeAccessorKind.Field, Name = "persistenceLock")]
    private static extern ref AsyncExclusiveLock PersistenceLock(WriteAheadLog wal);

    [UnsafeAccessor(UnsafeAccessorKind.Field, Name = "lockManager")]
    private static extern ref WriteAheadLog.LockManager LockManagerOf(WriteAheadLog wal);

    [UnsafeAccessor(UnsafeAccessorKind.Field, Name = "applyTrigger")]
    private static extern ref AsyncAutoResetEventSlim ApplyTrigger(WriteAheadLog wal);

    [UnsafeAccessor(UnsafeAccessorKind.Field, Name = "flushTrigger")]
    private static extern ref AsyncAutoResetEventSlim FlushTrigger(WriteAheadLog wal);

    [UnsafeAccessor(UnsafeAccessorKind.Field, Name = "state")]
    private static extern ref int TriggerState(AsyncAutoResetEventSlim trigger);

    private class Entry : IRaftLogEntry
    {
        public long Term { get; init; } = 1L;
        public bool IsSnapshot { get; init; }
        public bool IsReusable => false;
        public virtual long? Length => null;
        public Action AfterWrite { get; init; }

        async ValueTask IDataTransferObject.WriteToAsync<TWriter>(TWriter writer, CancellationToken token)
        {
            await writer.WriteAsync(new byte[] { 1, 2, 3 }, token: token);
            AfterWrite?.Invoke();
        }
    }

    private sealed class InvalidLengthEntry : Entry
    {
        public override long? Length => throw new ArgumentException("Invalid entry length.");
    }

    private sealed class OwnedEntry(TrackedBuffer buffer = null) : Entry, ISupplier<MemoryAllocator<byte>, MemoryOwner<byte>>
    {
        MemoryOwner<byte> ISupplier<MemoryAllocator<byte>, MemoryOwner<byte>>.Invoke(MemoryAllocator<byte> allocator)
        {
            var owner = buffer is null ? allocator(3) : new MemoryOwner<byte>(() => buffer);
            owner.Span.Fill(1);
            return owner;
        }
    }

    private sealed class TrackedBuffer : IMemoryOwner<byte>
    {
        public Memory<byte> Memory { get; } = new byte[3];
        internal bool IsDisposed { get; private set; }
        public void Dispose() => IsDisposed = true;
    }

    private sealed class FailingProducer(IRaftLogEntry[] prefix, Func<Exception> failure) : ILogEntryProducer<IRaftLogEntry>
    {
        private int position = -1;
        public long RemainingCount => prefix.Length - position;
        public IRaftLogEntry Current => prefix[position];

        public ValueTask<bool> MoveNextAsync()
            => ++position < prefix.Length ? ValueTask.FromResult(true) : ValueTask.FromException<bool>(failure());

        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
    }

    private sealed class FailingSnapshotStateMachine(Exception failure) : NoOpSnapshotManager, IStateMachine
    {
        internal bool SnapshotStarted { get; private set; }

        public ValueTask<long> ApplyAsync(LogEntry entry, CancellationToken token)
        {
            if (!entry.IsSnapshot)
                return ValueTask.FromResult(entry.Index);
            SnapshotStarted = true;
            return ValueTask.FromException<long>(failure);
        }
    }
}
