using System.Reflection;
using System.Text;

namespace DotNext.Net.Cluster.Consensus.Raft.StateMachine;

using IO;

[Collection(TestCollections.WriteAheadLog)]
public sealed class WriteAheadLogDurabilityTests : Test
{
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
