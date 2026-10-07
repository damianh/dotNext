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

    public static TheoryData<AppendKind> ProducerAppendKinds
    {
        get
        {
            var result = new TheoryData<AppendKind>();
            result.Add(AppendKind.Producer);
            result.Add(AppendKind.AppendAndCommit);
            result.Add(AppendKind.AppendAndCommitSlow);
            return result;
        }
    }

    public static TheoryData<AppendKind, bool> ProducerMutationFailureKinds
    {
        get
        {
            var result = new TheoryData<AppendKind, bool>();
            foreach (var kind in new[] { AppendKind.Producer, AppendKind.AppendAndCommit, AppendKind.AppendAndCommitSlow })
            {
                result.Add(kind, false);
                result.Add(kind, true);
            }

            return result;
        }
    }

    [Theory(Timeout = TestTimeouts.Default)]
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

    [Theory(Timeout = TestTimeouts.Default)]
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

    [Fact(Timeout = TestTimeouts.Default)]
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

    [Theory(Timeout = TestTimeouts.Default)]
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

    [Theory(Timeout = TestTimeouts.Default)]
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

    [Fact(Timeout = TestTimeouts.Default)]
    public static async Task SnapshotWithoutIndexDoesNotPoisonLog()
    {
        var options = CreateOptions();
        await SeedAsync(options);
        await using var wal = new WriteAheadLog(options, IStateMachine.CreateNoOp());
        await ThrowsAsync<InvalidOperationException>(
            () => wal.AppendAsync(new Entry { IsSnapshot = true }, TestToken).AsTask());
        await AssertUsableAsync(wal);
    }

    [Theory(Timeout = TestTimeouts.Default)]
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

    [Theory(Timeout = TestTimeouts.Default)]
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

    [Theory(Timeout = TestTimeouts.Default)]
    [MemberData(nameof(ProducerAppendKinds))]
    public static async Task ProducerCancellationAfterWrittenPrefixPublishesAndPreservesLog(AppendKind kind)
    {
        var options = CreateOptions();
        await SeedAsync(options);
        await using (var wal = new WriteAheadLog(options, IStateMachine.CreateNoOp()))
        {
            using var cancellation = new CancellationTokenSource();
            await using var entries = new FailingProducer([new Entry { Term = 2L }], () =>
            {
                cancellation.Cancel();
                return new OperationCanceledException(cancellation.Token);
            });

            var error = await ThrowsAnyAsync<OperationCanceledException>(
                () => AppendProducerAsync(wal, kind, entries, cancellation.Token));
            Equal(cancellation.Token, error.CancellationToken);
            await AssertPublishedAsync(wal, 2L);
            await AssertProgressAsync(wal, 3L, "next after producer cancellation");
        }

        await using var recovered = new WriteAheadLog(options, IStateMachine.CreateNoOp());
        await recovered.InitializeAsync(TestToken);
        await AssertPublishedAsync(recovered, 3L, 2L);
        using var next = await recovered.ReadAsync(3L, 3L, TestToken);
        Equal("next after producer cancellation", await next[0].ToStringAsync(Encoding.UTF8, token: TestToken));
    }

    [Theory(Timeout = TestTimeouts.Default)]
    [MemberData(nameof(ProducerAppendKinds))]
    public static async Task PreMoveNextCancellationPublishesWrittenPrefixWithoutAdvancingProducer(AppendKind kind)
    {
        var options = CreateOptions();
        await SeedAsync(options);
        await using (var wal = new WriteAheadLog(options, IStateMachine.CreateNoOp()))
        {
            using var cancellation = new CancellationTokenSource();
            await using var entries = new FailingProducer(
                [new Entry { Term = 2L, AfterWrite = cancellation.Cancel }],
                () => new IOException("MoveNextAsync should not be called after request cancellation."));

            var error = await ThrowsAnyAsync<OperationCanceledException>(
                () => AppendProducerAsync(wal, kind, entries, cancellation.Token));
            Equal(cancellation.Token, error.CancellationToken);
            Equal(1, entries.MoveNextCallCount);
            await AssertPublishedAsync(wal, 2L);
            await AssertProgressAsync(wal, 3L, "next after pre-MoveNext cancellation");
        }

        await using var recovered = new WriteAheadLog(options, IStateMachine.CreateNoOp());
        await recovered.InitializeAsync(TestToken);
        await AssertPublishedAsync(recovered, 3L, 2L);
        using var next = await recovered.ReadAsync(3L, 3L, TestToken);
        Equal("next after pre-MoveNext cancellation", await next[0].ToStringAsync(Encoding.UTF8, token: TestToken));
    }

    [Theory(Timeout = TestTimeouts.Default)]
    [MemberData(nameof(ProducerMutationFailureKinds))]
    public static async Task ProducerFailureAfterWrittenPrefixStillPoisonsLog(AppendKind kind, bool unrelatedCancellation)
    {
        var options = CreateOptions();
        await SeedAsync(options);
        await using (var wal = new WriteAheadLog(options, IStateMachine.CreateNoOp()))
        {
            Exception failure;
            if (unrelatedCancellation)
            {
                using var unrelated = new CancellationTokenSource();
                unrelated.Cancel();
                failure = new OperationCanceledException(unrelated.Token);
            }
            else
            {
                failure = new IOException("Producer failed.");
            }

            await using var entries = new FailingProducer([new Entry { Term = 2L }], () => failure);
            if (unrelatedCancellation)
                Same(failure, await ThrowsAnyAsync<OperationCanceledException>(() => AppendProducerAsync(wal, kind, entries, TestToken)));
            else
                Same(failure, await ThrowsAsync<IOException>(() => AppendProducerAsync(wal, kind, entries, TestToken)));

            await AssertPoisonedAsync(wal, failure);
        }

        await using var recovered = new WriteAheadLog(options, IStateMachine.CreateNoOp());
        await recovered.InitializeAsync(TestToken);
        await AssertUsableAsync(recovered, unrelatedCancellation
            ? "next after unrelated producer cancellation"
            : "next after producer failure");
    }

    [Theory(Timeout = TestTimeouts.Default)]
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

    [Theory(Timeout = TestTimeouts.Default)]
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

    [Theory(Timeout = TestTimeouts.Default)]
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

    [Theory(Timeout = TestTimeouts.Default)]
    [InlineData(AppendKind.Unbuffered, false)]
    [InlineData(AppendKind.Indexed, false)]
    [InlineData(AppendKind.Overwrite, false)]
    [InlineData(AppendKind.Producer, false)]
    [InlineData(AppendKind.AppendAndCommit, false)]
    [InlineData(AppendKind.AppendAndCommitSlow, false)]
    [InlineData(AppendKind.Unbuffered, true)]
    [InlineData(AppendKind.Indexed, true)]
    [InlineData(AppendKind.Overwrite, true)]
    [InlineData(AppendKind.Producer, true)]
    [InlineData(AppendKind.AppendAndCommit, true)]
    [InlineData(AppendKind.AppendAndCommitSlow, true)]
    public static async Task CancellationDuringPayloadWriteRollsBackAndKeepsLogUsable(AppendKind kind, bool leadershipLoss)
    {
        var options = CreateOptions();
        await SeedAsync(options);
        await using (var wal = new WriteAheadLog(options, IStateMachine.CreateNoOp()))
        {
            using var leadership = new CancellationTokenSource();
            using var caller = new CancellationTokenSource();
            using var linked = CancellationTokenSource.CreateLinkedTokenSource(caller.Token, leadership.Token);
            var entry = new PartiallyFailingEntry(
                beforeFailure: leadershipLoss ? leadership.Cancel : caller.Cancel,
                failureFactory: () => new OperationCanceledException(linked.Token))
            { Term = 2L };

            var error = await ThrowsAnyAsync<OperationCanceledException>(
                () => AppendAsync(wal, kind, linked.Token, entry));
            Equal(linked.Token, error.CancellationToken);
            await AssertSeededStateAsync(wal);
            False(File.Exists(Path.Combine(options.Location, "overwrite")));
            await AssertUsableAsync(wal, expectedNext: "next after rollback");
        }

        await using var recovered = new WriteAheadLog(options, IStateMachine.CreateNoOp());
        await recovered.InitializeAsync(TestToken);
        await AssertRecoveredProgressAsync(recovered, 3L, "next after rollback");
    }

    [Theory(Timeout = TestTimeouts.Default)]
    [InlineData(AppendKind.Unbuffered)]
    [InlineData(AppendKind.Indexed)]
    [InlineData(AppendKind.Overwrite)]
    [InlineData(AppendKind.Producer)]
    [InlineData(AppendKind.AppendAndCommit)]
    [InlineData(AppendKind.AppendAndCommitSlow)]
    public static async Task MidPayloadFailureStillPoisonsLog(AppendKind kind)
    {
        var options = CreateOptions();
        await SeedAsync(options);
        await using (var wal = new WriteAheadLog(options, IStateMachine.CreateNoOp()))
        {
            var failure = new IOException("Payload failed.");
            var entry = new PartiallyFailingEntry(failureFactory: () => failure) { Term = 2L };
            Same(failure, await ThrowsAsync<IOException>(() => AppendAsync(wal, kind, TestToken, entry)));
            await AssertPoisonedAsync(wal, failure);
        }

        await using var recovered = new WriteAheadLog(options, IStateMachine.CreateNoOp());
        await recovered.InitializeAsync(TestToken);
        await AssertUsableAsync(recovered, expectedNext: "next after payload failure");
    }

    [Theory(Timeout = TestTimeouts.Default)]
    [InlineData(AppendKind.Unbuffered)]
    [InlineData(AppendKind.Indexed)]
    [InlineData(AppendKind.Overwrite)]
    [InlineData(AppendKind.Producer)]
    [InlineData(AppendKind.AppendAndCommit)]
    [InlineData(AppendKind.AppendAndCommitSlow)]
    public static async Task UnrelatedPayloadCancellationStillPoisonsLog(AppendKind kind)
    {
        var options = CreateOptions();
        await SeedAsync(options);
        await using (var wal = new WriteAheadLog(options, IStateMachine.CreateNoOp()))
        {
            using var unrelated = new CancellationTokenSource();
            unrelated.Cancel();
            var failure = new OperationCanceledException(unrelated.Token);
            var entry = new PartiallyFailingEntry(failureFactory: () => failure) { Term = 2L };
            Same(failure, await ThrowsAnyAsync<OperationCanceledException>(() => AppendAsync(wal, kind, TestToken, entry)));
            await AssertPoisonedAsync(wal, failure);
        }

        await using var recovered = new WriteAheadLog(options, IStateMachine.CreateNoOp());
        await recovered.InitializeAsync(TestToken);
        await AssertUsableAsync(recovered, expectedNext: "next after unrelated cancellation");
    }

    [Theory(Timeout = TestTimeouts.Default)]
    [InlineData(AppendKind.Unbuffered, false)]
    [InlineData(AppendKind.Indexed, false)]
    [InlineData(AppendKind.Overwrite, false)]
    [InlineData(AppendKind.Producer, false)]
    [InlineData(AppendKind.AppendAndCommit, false)]
    [InlineData(AppendKind.AppendAndCommitSlow, false)]
    [InlineData(AppendKind.Unbuffered, true)]
    [InlineData(AppendKind.Overwrite, true)]
    [InlineData(AppendKind.Producer, true)]
    [InlineData(AppendKind.AppendAndCommit, true)]
    public static async Task CancellationAfterPayloadCompletesPublishesDurableAppend(AppendKind kind, bool leadershipLoss)
    {
        var options = CreateOptions();
        await SeedAsync(options);
        var expectedLast = kind is AppendKind.Unbuffered or AppendKind.Indexed ? 3L : 2L;
        await using (var wal = new WriteAheadLog(options, IStateMachine.CreateNoOp()))
        {
            // Leadership loss reaches the WAL through a token linked with the caller's token.
            using var leadership = new CancellationTokenSource();
            using var caller = new CancellationTokenSource();
            using var linked = CancellationTokenSource.CreateLinkedTokenSource(caller.Token, leadership.Token);
            var entry = new Entry
            {
                Term = 2L,
                AfterWrite = leadershipLoss ? leadership.Cancel : caller.Cancel,
            };

            await AppendAsync(wal, kind, linked.Token, entry);
            True(linked.IsCancellationRequested);
            await AssertPublishedAsync(wal, expectedLast);
            await AssertProgressAsync(wal, expectedLast + 1L);
        }

        await using var recovered = new WriteAheadLog(options, IStateMachine.CreateNoOp());
        await recovered.InitializeAsync(TestToken);
        await AssertPublishedAsync(recovered, expectedLast + 1L, expectedLast);
    }

    [Theory(Timeout = TestTimeouts.Default)]
    [InlineData(false)]
    [InlineData(true)]
    public static async Task CancellationBetweenEntriesPublishesWrittenPrefix(bool appendAndCommit)
    {
        var options = CreateOptions();
        await SeedAsync(options);
        await using (var wal = new WriteAheadLog(options, IStateMachine.CreateNoOp()))
        {
            using var cancellation = new CancellationTokenSource();
            await using var entries = new LogEntryProducer<IRaftLogEntry>(new IRaftLogEntry[]
            {
                new Entry { Term = 2L, AfterWrite = cancellation.Cancel },
                new Entry { Term = 2L },
            });
            var append = appendAndCommit
                ? ((IAuditTrail<IRaftLogEntry>)wal).AppendAndCommitAsync(entries, 2L, false, 1L, cancellation.Token).AsTask()
                : wal.AppendAsync(entries, 2L, token: cancellation.Token).AsTask();

            // The unknown outcome is reported to the caller; only the fully written prefix is published.
            var error = await ThrowsAnyAsync<OperationCanceledException>(() => append);
            Equal(cancellation.Token, error.CancellationToken);
            await AssertPublishedAsync(wal, 2L);
            await AssertProgressAsync(wal, 3L);
        }

        await using var recovered = new WriteAheadLog(options, IStateMachine.CreateNoOp());
        await recovered.InitializeAsync(TestToken);
        await AssertPublishedAsync(recovered, 3L, 2L);
    }

    [Theory(Timeout = TestTimeouts.Default)]
    [InlineData(false, false)]
    [InlineData(false, true)]
    [InlineData(true, false)]
    [InlineData(true, true)]
    public static async Task CancellationDuringSecondPayloadPublishesFirstEntry(bool appendAndCommit, bool leadershipLoss)
    {
        var options = CreateOptions();
        await SeedAsync(options);
        await using (var wal = new WriteAheadLog(options, IStateMachine.CreateNoOp()))
        {
            using var leadership = new CancellationTokenSource();
            using var caller = new CancellationTokenSource();
            using var linked = CancellationTokenSource.CreateLinkedTokenSource(caller.Token, leadership.Token);
            await using var entries = new LogEntryProducer<IRaftLogEntry>(new IRaftLogEntry[]
            {
                new Entry { Term = 2L },
                new PartiallyFailingEntry(
                    beforeFailure: leadershipLoss ? leadership.Cancel : caller.Cancel,
                    failureFactory: () => new OperationCanceledException(linked.Token))
                { Term = 2L },
            });

            var append = appendAndCommit
                ? ((IAuditTrail<IRaftLogEntry>)wal).AppendAndCommitAsync(entries, 2L, false, 1L, linked.Token).AsTask()
                : wal.AppendAsync(entries, 2L, token: linked.Token).AsTask();

            var error = await ThrowsAnyAsync<OperationCanceledException>(() => append);
            Equal(linked.Token, error.CancellationToken);
            await AssertPublishedAsync(wal, 2L);
            Equal(1L, wal.LastCommittedEntryIndex);
            False(File.Exists(Path.Combine(options.Location, "overwrite")));
        }

        await using var recovered = new WriteAheadLog(options, IStateMachine.CreateNoOp());
        await recovered.InitializeAsync(TestToken);
        await AssertPublishedAsync(recovered, 2L);
        await AssertProgressAsync(recovered, 3L, "next after published prefix");
    }

    public enum SnapshotFailure
    {
        // the state machine failed with an OCE that has nothing to do with the request token
        UnrelatedCancellation,
        // the request token was canceled and the state machine reported it
        RequestCancellation,
        Error,
    }

    [Theory(Timeout = TestTimeouts.Default)]
    [InlineData(SnapshotFailure.UnrelatedCancellation, false)]
    [InlineData(SnapshotFailure.UnrelatedCancellation, true)]
    [InlineData(SnapshotFailure.RequestCancellation, false)]
    [InlineData(SnapshotFailure.Error, false)]
    [InlineData(SnapshotFailure.Error, true)]
    public static async Task SnapshotApplicationFailurePoisonsLog(SnapshotFailure kind, bool cancellationSafe)
    {
        var options = CreateOptions();
        await SeedAsync(options);
        using var request = new CancellationTokenSource();
        Exception failure = kind switch
        {
            SnapshotFailure.UnrelatedCancellation => new OperationCanceledException(),
            SnapshotFailure.RequestCancellation => new OperationCanceledException(request.Token),
            _ => new IOException("Injected snapshot failure."),
        };
        var stateMachine = new FailingSnapshotStateMachine(
            failure,
            cancellationSafe,
            kind is SnapshotFailure.RequestCancellation ? request : null);
        await using var wal = new WriteAheadLog(options, stateMachine);
        await wal.InitializeAsync(TestToken);
        Same(failure, await ThrowsAnyAsync<Exception>(
            () => wal.AppendAsync(new Entry { IsSnapshot = true }, 3L, request.Token).AsTask()));
        True(stateMachine.SnapshotStarted);
        await AssertPoisonedAsync(wal, failure);
    }

    [Fact(Timeout = TestTimeouts.Default)]
    public static async Task CancellationOfCancellationSafeSnapshotApplicationKeepsLogUsable()
    {
        var options = CreateOptions();
        await SeedAsync(options);
        using var request = new CancellationTokenSource();
        var failure = new OperationCanceledException(request.Token);
        var stateMachine = new FailingSnapshotStateMachine(failure, cancellationSafe: true, request);
        await using var wal = new WriteAheadLog(options, stateMachine);
        await wal.InitializeAsync(TestToken);
        Same(failure, await ThrowsAnyAsync<OperationCanceledException>(
            () => wal.AppendAsync(new Entry { IsSnapshot = true }, 3L, request.Token).AsTask()));
        True(stateMachine.SnapshotStarted);
        await AssertUsableAsync(wal);
    }

    [Theory(Timeout = TestTimeouts.Default)]
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

    [Theory(Timeout = TestTimeouts.Default)]
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
        var readTicket = 0L;
        if (overwrite)
            readTicket = await locks.AcquireReadLockAsync(TestToken);
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
                locks.ReleaseReadLock(readTicket);
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

    private static async Task AssertSeededStateAsync(WriteAheadLog wal)
    {
        Equal(2L, wal.LastEntryIndex);
        Equal(1L, wal.LastCommittedEntryIndex);
        using (var entries = await wal.ReadAsync(1L, 2L, TestToken))
        {
            Equal("prefix", await entries[0].ToStringAsync(Encoding.UTF8, token: TestToken));
            Equal("old tail", await entries[1].ToStringAsync(Encoding.UTF8, token: TestToken));
        }
    }

    private static async Task AssertUsableAsync(WriteAheadLog wal, string expectedNext = "next")
    {
        await AssertSeededStateAsync(wal);
        await AssertProgressAsync(wal, 3L, expectedNext);
    }

    private static async Task AssertRecoveredProgressAsync(WriteAheadLog wal, long expectedLast, string expectedNext)
    {
        Equal(expectedLast, wal.LastEntryIndex);
        Equal(expectedLast, wal.LastCommittedEntryIndex);
        Equal(expectedLast, wal.LastAppliedIndex);
        using (var entries = await wal.ReadAsync(2L, expectedLast, TestToken))
        {
            Equal("old tail", await entries[0].ToStringAsync(Encoding.UTF8, token: TestToken));
            Equal(expectedNext, await entries[1].ToStringAsync(Encoding.UTF8, token: TestToken));
        }
    }

    // The entry at `writtenIndex` carries the payload of the append published before cancellation surfaced.
    private static async Task AssertPublishedAsync(WriteAheadLog wal, long expectedLast, long? writtenIndex = null)
    {
        Equal(expectedLast, wal.LastEntryIndex);
        var index = writtenIndex ?? expectedLast;
        using var entries = await wal.ReadAsync(index, index, TestToken);
        Equal(2L, entries[0].Term);
        Equal(new byte[] { 1, 2, 3 }, await entries[0].ToByteArrayAsync(token: TestToken));
    }

    private static async Task AssertProgressAsync(WriteAheadLog wal, long index, string payload = "next")
    {
        Equal(index, await wal.AppendAsync(new TestLogEntry(payload) { Term = 2L }, TestToken));
        await wal.CommitAsync(index, TestToken);
        await wal.WaitForApplyAsync(index, TestToken);
        await wal.FlushAsync(TestToken);
        using var entries = await wal.ReadAsync(index, index, TestToken);
        Equal(payload, await entries[0].ToStringAsync(Encoding.UTF8, token: TestToken));
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

    private static Task AppendProducerAsync(WriteAheadLog wal, AppendKind kind, ILogEntryProducer<IRaftLogEntry> entries, CancellationToken token)
        => kind switch
        {
            AppendKind.Producer => wal.AppendAsync(entries, 2L, token: token).AsTask(),
            AppendKind.AppendAndCommit => ((IAuditTrail<IRaftLogEntry>)wal)
                .AppendAndCommitAsync(entries, 2L, false, 1L, token).AsTask(),
            AppendKind.AppendAndCommitSlow => ((IAuditTrail<IRaftLogEntry>)wal)
                .AppendAndCommitAsync(entries, 2L, false, 2L, token).AsTask(),
            _ => throw new ArgumentOutOfRangeException(nameof(kind)),
        };

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

    private sealed class PartiallyFailingEntry(Action beforeFailure = null, Func<Exception> failureFactory = null) : Entry, IRaftLogEntry
    {
        public override long? Length => 3L;

        async ValueTask IDataTransferObject.WriteToAsync<TWriter>(TWriter writer, CancellationToken token)
        {
            await writer.WriteAsync(new byte[] { 1, 2 }, token: token);
            beforeFailure?.Invoke();
            throw failureFactory?.Invoke() ?? new IOException("Payload failed.");
        }
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

    private sealed class FailingProducer(IRaftLogEntry[] prefix, Func<Exception> failure, long remainingCountOnFailure = 1L) : ILogEntryProducer<IRaftLogEntry>
    {
        private int position = -1;
        public long RemainingCount => position < prefix.Length
            ? prefix.Length - position - 1L + remainingCountOnFailure
            : remainingCountOnFailure;
        public IRaftLogEntry Current => prefix[position];
        internal int MoveNextCallCount { get; private set; }

        public ValueTask<bool> MoveNextAsync()
        {
            MoveNextCallCount++;
            return ++position < prefix.Length ? ValueTask.FromResult(true) : ValueTask.FromException<bool>(failure());
        }

        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
    }

    private sealed class FailingSnapshotStateMachine(
        Exception failure,
        bool cancellationSafe = false,
        CancellationTokenSource cancelOnSnapshot = null) : NoOpSnapshotManager, IStateMachine
    {
        internal bool SnapshotStarted { get; private set; }

        public bool IsSnapshotInstallCancellationSafe => cancellationSafe;

        public ValueTask<long> ApplyAsync(LogEntry entry, CancellationToken token)
        {
            if (!entry.IsSnapshot)
                return ValueTask.FromResult(entry.Index);
            SnapshotStarted = true;
            cancelOnSnapshot?.Cancel();
            return ValueTask.FromException<long>(failure);
        }
    }
}
