namespace DotNext.Net.Cluster.Consensus.Raft.StateMachine;

using IO;

[Collection(TestCollections.WriteAheadLog)]
public sealed class SimpleStateMachineSnapshotTests : Test
{
    public enum FailureMode
    {
        Serialization,
        Cancellation,
        Write,
        FinalWrite,
        Flush,
        Commit,
    }

    public enum PublicationPath
    {
        Incoming,
        Outgoing,
    }

    [Theory]
    [InlineData(FailureMode.Serialization)]
    [InlineData(FailureMode.Cancellation)]
    [InlineData(FailureMode.Write)]
    [InlineData(FailureMode.FinalWrite)]
    [InlineData(FailureMode.Flush)]
    [InlineData(FailureMode.Commit)]
    public async Task FailedIncomingSnapshotIsNotPublished(FailureMode failureMode)
    {
        const long previousIndex = 10L, failedIndex = 20L, retryIndex = 30L;
        byte[] previousState = [1, 2, 3, 4];
        byte[] failedState = new byte[Environment.SystemPageSize * 2];
        byte[] retryState = [5, 6, 7, 8];
        Array.Fill(failedState, (byte)42);
        var location = new DirectoryInfo(GetTempPath());

        await using (var machine = new SnapshotStateMachine(location))
        {
            await InstallAsync(machine, new SnapshotEntry(previousState, term: 1L), previousIndex, TestToken);
            Equal(previousIndex, machine.As<IStateMachine>().Snapshot.Index);
            Equal(previousState, machine.State);
        }

        await using (var machine = new SnapshotStateMachine(location, failureMode))
        {
            await machine.RestoreAsync(TestToken);
            Equal(previousIndex, machine.As<IStateMachine>().Snapshot.Index);
            Equal(previousState, machine.State);

            using var cancellation = failureMode is FailureMode.Cancellation ? new CancellationTokenSource() : null;
            var entry = new SnapshotEntry(failedState, term: 2L, failureMode, cancellation);
            var failureToken = cancellation?.Token ?? TestToken;
            switch (failureMode)
            {
                case FailureMode.Cancellation or FailureMode.FinalWrite:
                    await ThrowsAnyAsync<OperationCanceledException>(
                        InstallAsync(machine, entry, failedIndex, failureToken).AsTask());
                    break;
                default:
                    await ThrowsAsync<IOException>(
                        InstallAsync(machine, entry, failedIndex, failureToken).AsTask());
                    break;
            }

            Equal(previousIndex, machine.As<IStateMachine>().Snapshot.Index);
        }

        await using (var machine = new SnapshotStateMachine(location))
        {
            await machine.RestoreAsync(TestToken);
            Equal(previousIndex, machine.As<IStateMachine>().Snapshot.Index);
            Equal(1L, machine.As<IStateMachine>().Snapshot.Term);
            Equal(previousState, machine.State);

            await InstallAsync(machine, new SnapshotEntry(retryState, term: 3L), retryIndex, TestToken);
            Equal(retryIndex, machine.As<IStateMachine>().Snapshot.Index);
            Equal(retryState, machine.State);
        }

        await using (var machine = new SnapshotStateMachine(location))
        {
            await machine.RestoreAsync(TestToken);
            Equal(retryIndex, machine.As<IStateMachine>().Snapshot.Index);
            Equal(3L, machine.As<IStateMachine>().Snapshot.Term);
            Equal(retryState, machine.State);
        }

        Equal(previousState, await File.ReadAllBytesAsync(
            Path.Combine(location.FullName, $"{previousIndex}-1"), TestToken));
        False(File.Exists(Path.Combine(location.FullName, $"{failedIndex}-2")));
        Equal(retryState, await File.ReadAllBytesAsync(
            Path.Combine(location.FullName, $"{retryIndex}-3"), TestToken));
        Empty(location.EnumerateFiles("*.tmp"));
    }

    [Theory]
    [InlineData(FailureMode.Serialization)]
    [InlineData(FailureMode.Commit)]
    public async Task FailedOutgoingSnapshotIsRolledBack(FailureMode failureMode)
    {
        var location = new DirectoryInfo(GetTempPath());
        await using var machine = new SnapshotStateMachine(
            location,
            failureMode,
            persist: async (writer, token) =>
            {
                await writer.WriteAsync(new byte[Environment.SystemPageSize + 1], null, token);
                if (failureMode is FailureMode.Serialization)
                    throw new IOException("Injected outgoing snapshot failure.");
            },
            takeSnapshot: true);

        await machine.As<IStateMachine>().ApplyAsync(new LogEntry(term: 1L, index: 5L), TestToken);
        await ThrowsAsync<IOException>(
            machine.As<IStateMachine>().ApplyAsync(new LogEntry(term: 1L, index: 6L), TestToken).AsTask());

        False(File.Exists(Path.Combine(location.FullName, "5-1")));
        Empty(location.EnumerateFiles("*.tmp"));
    }

    [Theory]
    [InlineData(PublicationPath.Incoming)]
    [InlineData(PublicationPath.Outgoing)]
    public async Task FailedSnapshotReplacementPreservesExistingSnapshot(PublicationPath publicationPath)
    {
        const long index = 10L, term = 1L;
        byte[] previousState = [1, 2, 3, 4];
        byte[] replacementState = [5, 6, 7, 8];
        var location = new DirectoryInfo(GetTempPath());

        await using (var machine = new SnapshotStateMachine(location))
        {
            await InstallAsync(machine, new SnapshotEntry(previousState, term), index, TestToken);
        }

        await using (var machine = new SnapshotStateMachine(
            location,
            FailureMode.Commit,
            persist: (writer, token) => writer.WriteAsync(replacementState, null, token),
            takeSnapshot: publicationPath is PublicationPath.Outgoing))
        {
            if (publicationPath is PublicationPath.Incoming)
            {
                await ThrowsAsync<IOException>(
                    InstallAsync(machine, new SnapshotEntry(replacementState, term), index, TestToken).AsTask());
            }
            else
            {
                await machine.As<IStateMachine>().ApplyAsync(new LogEntry(term, index), TestToken);
                await ThrowsAsync<IOException>(
                    machine.As<IStateMachine>().ApplyAsync(new LogEntry(term, index + 1L), TestToken).AsTask());
            }
        }

        Equal(previousState, await File.ReadAllBytesAsync(
            Path.Combine(location.FullName, $"{index}-{term}"), TestToken));
        Empty(location.EnumerateFiles("*.tmp"));

        await using var restoredMachine = new SnapshotStateMachine(location);
        await restoredMachine.RestoreAsync(TestToken);
        Equal(index, restoredMachine.As<IStateMachine>().Snapshot.Index);
        Equal(term, restoredMachine.As<IStateMachine>().Snapshot.Term);
        Equal(previousState, restoredMachine.State);
    }

    [Fact]
    public async Task IncomingSnapshotRollsBackOutgoingSnapshot()
    {
        var snapshotting = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var location = new DirectoryInfo(GetTempPath());
        await using var machine = new SnapshotStateMachine(
            location,
            persist: async (writer, token) =>
            {
                await writer.WriteAsync(new byte[Environment.SystemPageSize + 1], null, token);
                snapshotting.SetResult();
                await release.Task.WaitAsync(token);
            },
            takeSnapshot: true);

        await machine.As<IStateMachine>().ApplyAsync(new LogEntry(term: 1L, index: 5L), TestToken);
        await snapshotting.Task.WaitAsync(TestToken);

        byte[] incomingState = [9, 10, 11, 12];
        var installation = InstallAsync(
            machine,
            new SnapshotEntry(incomingState, term: 2L),
            index: 10L,
            TestToken).AsTask();
        False(installation.IsCompleted);

        release.SetResult();
        Equal(10L, await installation);
        Equal(incomingState, machine.State);
        False(File.Exists(Path.Combine(location.FullName, "5-1")));
        True(File.Exists(Path.Combine(location.FullName, "10-2")));
        Empty(location.EnumerateFiles("*.tmp"));
    }

    private static ValueTask<long> InstallAsync(
        SnapshotStateMachine machine,
        IRaftLogEntry snapshot,
        long index,
        CancellationToken token)
        => machine.As<IStateMachine>().ApplyAsync(new LogEntry(snapshot, index), token);

    private sealed class SnapshotStateMachine : SimpleStateMachine
    {
        private readonly Func<IAsyncBinaryWriter, CancellationToken, ValueTask> persist;
        private readonly bool takeSnapshot;

        internal SnapshotStateMachine(
            DirectoryInfo location,
            FailureMode? failureMode = null,
            Func<IAsyncBinaryWriter, CancellationToken, ValueTask> persist = null,
            bool takeSnapshot = false)
            : base(
                location,
                failureMode is FailureMode.Write or FailureMode.FinalWrite or FailureMode.Flush or FailureMode.Commit
                    ? (size, destination) => new FaultingSnapshotWriter(size, destination, failureMode.GetValueOrDefault())
                    : SnapshotWriter.CreateDefault)
        {
            this.persist = persist;
            this.takeSnapshot = takeSnapshot;
        }

        internal byte[] State { get; private set; } = [];

        protected override async ValueTask RestoreAsync(FileInfo snapshotFile, CancellationToken token)
            => State = await File.ReadAllBytesAsync(snapshotFile.FullName, token);

        protected override ValueTask PersistAsync(IAsyncBinaryWriter writer, CancellationToken token)
            => persist?.Invoke(writer, token) ?? ValueTask.CompletedTask;

        protected override ValueTask<bool> ApplyAsync(LogEntry entry, CancellationToken token)
            => ValueTask.FromResult(takeSnapshot);
    }

    private sealed class FaultingSnapshotWriter(
        long preallocationSize,
        FileInfo destination,
        FailureMode failureMode)
        : SimpleStateMachine.SnapshotWriter(preallocationSize, destination)
    {
        protected override void Commit(string sourceFileName, string destinationFileName)
        {
            if (failureMode is FailureMode.Commit)
            {
                base.Commit(sourceFileName, destinationFileName);
                throw new IOException("Injected snapshot commit failure.");
            }

            base.Commit(sourceFileName, destinationFileName);
        }

        internal override ValueTask CompleteWriteAsync(CancellationToken token)
            => failureMode switch
            {
                FailureMode.Write => ValueTask.FromException(new IOException("Injected snapshot write failure.")),
                FailureMode.FinalWrite => ValueTask.FromException(new OperationCanceledException(token)),
                _ => base.CompleteWriteAsync(token),
            };

        internal override void CompleteFlush()
        {
            if (failureMode is FailureMode.Flush)
                throw new IOException("Injected snapshot flush failure.");

            base.CompleteFlush();
        }
    }

    private sealed class SnapshotEntry(
        byte[] content,
        long term,
        FailureMode? failureMode = null,
        CancellationTokenSource cancellation = null)
        : IRaftLogEntry
    {
        public long Term => term;

        public bool IsSnapshot => true;

        public long? Length => content.LongLength;

        public bool IsReusable => true;

        public async ValueTask WriteToAsync<TWriter>(TWriter writer, CancellationToken token)
            where TWriter : IAsyncBinaryWriter
        {
            var prefixLength = failureMode switch
            {
                FailureMode.Serialization or FailureMode.Cancellation => Environment.SystemPageSize + 1,
                FailureMode.Write or FailureMode.FinalWrite => 4,
                _ => content.Length,
            };
            await writer.WriteAsync(content.AsMemory(0, prefixLength), null, token);

            if (failureMode is FailureMode.Serialization)
                throw new IOException("Injected snapshot serialization failure.");

            if (failureMode is FailureMode.Cancellation)
            {
                await cancellation.CancelAsync();
                token.ThrowIfCancellationRequested();
            }
        }
    }
}
