using DotNext.IO;
using static System.Threading.Timeout;

namespace DotNext.Net.Cluster.Consensus.Raft.StateMachine;

public sealed partial class RegressionIssue14 : Test
{
    [Fact]
    public static async Task SnapshotInsideUncommittedSuffixDoesNotRecoverThatSuffix()
    {
        const long committedIndex = 2L;
        const long reportedSnapshotIndex = 4L;
        const long installationIndex = 5L;
        const long tailIndex = 6L;
        byte[] state = [10, 20, 30, 40];
        var retainedPayload = BitConverter.GetBytes(installationIndex);
        var replacementPayload = "replacement after restart"u8.ToArray();

        var options = CreateOptions(InfiniteTimeSpan);
        var machineLocation = new DirectoryInfo(GetTempPath());

        await using (var machine = new SuffixSnapshotStateMachine(machineLocation, reportedSnapshotIndex))
        {
            await machine.RestoreAsync(TestToken);
            await using var wal = new WriteAheadLog(options, machine);

            for (var i = 1L; i <= tailIndex; i++)
            {
                await wal.AppendAsync(
                    new BinaryLogEntry { Content = BitConverter.GetBytes(i), Term = 1L },
                    TestToken);
            }

            await wal.CommitAsync(committedIndex, TestToken);
            await wal.FlushAsync(TestToken);
            await wal.WaitForApplyAsync(committedIndex, TestToken);

            QuiesceApplier(wal);
            await wal.AppendAsync(new SnapshotEntry(state, SnapshotTerm), installationIndex, TestToken);
            Equal(reportedSnapshotIndex, wal.LastCommittedEntryIndex);
            Equal(tailIndex, wal.LastEntryIndex);

            await wal.FlushAsync(TestToken);
            Equal(reportedSnapshotIndex, ReadCheckpoint(options.Location));

            using var liveEntries = await wal.ReadAsync(installationIndex, installationIndex, TestToken);
            Equal(retainedPayload, await liveEntries[0].ToByteArrayAsync(token: TestToken));
        }

        await using (var machine = new SuffixSnapshotStateMachine(machineLocation, reportedSnapshotIndex))
        {
            await machine.RestoreAsync(TestToken);
            Equal(state, machine.State);

            await using var wal = new WriteAheadLog(options, machine);
            await wal.InitializeAsync(TestToken);

            Equal(reportedSnapshotIndex, wal.LastCommittedEntryIndex);
            Equal(reportedSnapshotIndex, wal.LastEntryIndex);
            await ThrowsAsync<ArgumentOutOfRangeException>(wal.ReadAsync(installationIndex, installationIndex, TestToken).AsTask);

            Equal(
                reportedSnapshotIndex + 1L,
                await wal.AppendAsync(
                    new BinaryLogEntry { Content = replacementPayload, Term = SnapshotTerm },
                    TestToken));
            await wal.CommitAsync(reportedSnapshotIndex + 1L, TestToken);
            await wal.FlushAsync(TestToken);
            Equal(reportedSnapshotIndex + 1L, ReadCheckpoint(options.Location));
        }

        await using (var machine = new SuffixSnapshotStateMachine(machineLocation, reportedSnapshotIndex))
        {
            await machine.RestoreAsync(TestToken);
            await using var wal = new WriteAheadLog(options, machine);
            await wal.InitializeAsync(TestToken);

            Equal(reportedSnapshotIndex + 1L, wal.LastCommittedEntryIndex);
            Equal(reportedSnapshotIndex + 1L, wal.LastEntryIndex);
            Equal(state, machine.State);

            using var entries = await wal.ReadAsync(reportedSnapshotIndex + 1L, reportedSnapshotIndex + 1L, TestToken);
            Equal(replacementPayload, await entries[0].ToByteArrayAsync(token: TestToken));
        }
    }

    [Fact]
    public static async Task SnapshotInsideSuffixKeepsLaterEntriesCommittedBeforeRestart()
    {
        const long committedIndex = 2L;
        const long reportedSnapshotIndex = 4L;
        const long installationIndex = 5L;
        const long tailIndex = 6L;
        byte[] state = [50, 60, 70, 80];
        var firstRetainedPayload = BitConverter.GetBytes(installationIndex);
        var secondRetainedPayload = BitConverter.GetBytes(tailIndex);

        var options = CreateOptions(InfiniteTimeSpan);
        var machineLocation = new DirectoryInfo(GetTempPath());

        await using (var machine = new SuffixSnapshotStateMachine(machineLocation, reportedSnapshotIndex))
        {
            await machine.RestoreAsync(TestToken);
            await using var wal = new WriteAheadLog(options, machine);

            for (var i = 1L; i <= tailIndex; i++)
            {
                await wal.AppendAsync(
                    new BinaryLogEntry { Content = BitConverter.GetBytes(i), Term = 1L },
                    TestToken);
            }

            await wal.CommitAsync(committedIndex, TestToken);
            await wal.FlushAsync(TestToken);
            await wal.WaitForApplyAsync(committedIndex, TestToken);

            QuiesceApplier(wal);
            await wal.AppendAsync(new SnapshotEntry(state, SnapshotTerm), installationIndex, TestToken);
            await wal.CommitAsync(tailIndex, TestToken);
            await wal.FlushAsync(TestToken);
            Equal(tailIndex, ReadCheckpoint(options.Location));
        }

        await using (var machine = new SuffixSnapshotStateMachine(machineLocation, reportedSnapshotIndex))
        {
            await machine.RestoreAsync(TestToken);
            await using var wal = new WriteAheadLog(options, machine);
            await wal.InitializeAsync(TestToken);

            Equal(tailIndex, wal.LastCommittedEntryIndex);
            Equal(tailIndex, wal.LastEntryIndex);
            Equal(state, machine.State);

            using var entries = await wal.ReadAsync(installationIndex, tailIndex, TestToken);
            Equal(firstRetainedPayload, await entries[0].ToByteArrayAsync(token: TestToken));
            Equal(secondRetainedPayload, await entries[1].ToByteArrayAsync(token: TestToken));
        }
    }

    [Fact]
    public static async Task SnapshotIndexBelowCommitDoesNotMoveCommitBackwards()
    {
        const long committedIndex = 4L;
        const long reportedSnapshotIndex = 3L;
        const long installationIndex = 5L;

        var options = CreateOptions(InfiniteTimeSpan);
        var machineLocation = new DirectoryInfo(GetTempPath());

        await using var machine = new SuffixSnapshotStateMachine(machineLocation, reportedSnapshotIndex);
        await machine.RestoreAsync(TestToken);
        await using var wal = new WriteAheadLog(options, machine);

        for (var i = 1L; i <= installationIndex; i++)
        {
            await wal.AppendAsync(
                new BinaryLogEntry { Content = BitConverter.GetBytes(i), Term = 1L },
                TestToken);
        }

        await wal.CommitAsync(committedIndex, TestToken);
        await wal.FlushAsync(TestToken);
        await wal.WaitForApplyAsync(committedIndex, TestToken);

        QuiesceApplier(wal);
        await wal.AppendAsync(new SnapshotEntry([1, 2, 3, 4], SnapshotTerm), installationIndex, TestToken);

        Equal(committedIndex, wal.LastCommittedEntryIndex);
        Equal(installationIndex, wal.LastEntryIndex);
    }

    private sealed class SuffixSnapshotStateMachine(DirectoryInfo location, long snapshotIndex) : IStateMachine, IAsyncDisposable
    {
        private SuffixSnapshot snapshot;

        internal byte[] State { get; private set; } = [];

        ISnapshot ISnapshotManager.Snapshot => snapshot;

        internal async ValueTask RestoreAsync(CancellationToken token)
        {
            location.Create();

            foreach (var candidate in location.EnumerateFiles("*-*", SearchOption.TopDirectoryOnly))
            {
                if (snapshot is null || SuffixSnapshot.ParseIndex(candidate) > snapshot.Index)
                    snapshot = new(candidate);
            }

            if (snapshot is not null)
                State = await System.IO.File.ReadAllBytesAsync(snapshot.File.FullName, token);
        }

        async ValueTask<long> IStateMachine.ApplyAsync(LogEntry entry, CancellationToken token)
        {
            if (entry.IsSnapshot)
            {
                State = await entry.ToByteArrayAsync(token: token);
                var file = new FileInfo(Path.Combine(location.FullName, $"{snapshotIndex}-{entry.Term}"));
                await System.IO.File.WriteAllBytesAsync(file.FullName, State, token);
                snapshot = new(file);
            }

            return entry.Index;
        }

        ValueTask ISnapshotManager.ReclaimGarbageAsync(long watermark, CancellationToken token)
        {
            foreach (var candidate in location.EnumerateFiles("*-*", SearchOption.TopDirectoryOnly))
            {
                token.ThrowIfCancellationRequested();
                if (SuffixSnapshot.ParseIndex(candidate) < watermark)
                    candidate.Delete();
            }

            return ValueTask.CompletedTask;
        }

        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
    }

    private sealed class SuffixSnapshot(FileInfo file) : ISnapshot
    {
        internal readonly FileInfo File = file;

        public long Index { get; } = ParseIndex(file);

        public long Term { get; } = ParseTerm(file);

        public long? Length => File.Length;

        bool IDataTransferObject.IsReusable => true;

        internal static long ParseIndex(FileInfo file) => long.Parse(file.Name.AsSpan(0, file.Name.IndexOf('-')));

        private static long ParseTerm(FileInfo file) => long.Parse(file.Name.AsSpan(file.Name.IndexOf('-') + 1));

        public async ValueTask WriteToAsync<TWriter>(TWriter writer, CancellationToken token)
            where TWriter : IAsyncBinaryWriter
            => await writer.Invoke(await System.IO.File.ReadAllBytesAsync(File.FullName, token), token);
    }
}
