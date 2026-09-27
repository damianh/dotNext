namespace DotNext.Net.Cluster.Consensus.Raft;

using Buffers;
using IO;
using StateMachine;

[Collection(TestCollections.WriteAheadLog)]
public sealed class TermGuardedAppendTests : Test
{
    public enum EntryKind
    {
        Memory,
        OwnedBuffer,
        Unbuffered,
    }

    [Theory(Timeout = TestTimeouts.Default)]
    [InlineData(EntryKind.Memory)]
    [InlineData(EntryKind.OwnedBuffer)]
    [InlineData(EntryKind.Unbuffered)]
    public static async Task WriteAheadLogRejectsOtherTerms(EntryKind kind)
    {
        await using var wal = new WriteAheadLog(new()
        {
            Location = GetTempPath(),
            MemoryManagement = WriteAheadLog.MemoryManagementStrategy.PrivateMemory,
        }, IStateMachine.CreateNoOp());
        await AssertGuardAsync(wal, kind);
    }

    [Theory(Timeout = TestTimeouts.Default)]
    [InlineData(EntryKind.Memory)]
    [InlineData(EntryKind.OwnedBuffer)]
    [InlineData(EntryKind.Unbuffered)]
    public static async Task ConsensusOnlyStateRejectsOtherTerms(EntryKind kind)
    {
        using var state = new ConsensusOnlyState();
        await AssertGuardAsync(state, kind);
    }

    private static async Task AssertGuardAsync(IPersistentState state, EntryKind kind)
    {
        await state.UpdateTermAsync(2L, resetLastVote: false, TestToken);

        await ThrowsAsync<NotLeaderException>(() => state.AppendInCurrentTermAsync(CreateEntry(kind, 1L), TestToken).AsTask());
        await ThrowsAsync<NotLeaderException>(() => state.AppendInCurrentTermAsync(CreateEntry(kind, 3L), TestToken).AsTask());
        Equal(0L, state.LastEntryIndex);

        Equal(1L, await state.AppendInCurrentTermAsync(CreateEntry(kind, 2L), TestToken));
        Equal(1L, state.LastEntryIndex);
        Equal(2L, await state.GetTermAsync(1L, TestToken));

        // the guard doesn't break the log: an unguarded append still works
        Equal(2L, await state.AppendAsync(CreateEntry(kind, 2L), TestToken));
    }

    private static IRaftLogEntry CreateEntry(EntryKind kind, long term) => kind switch
    {
        EntryKind.Memory => new TestLogEntry("payload") { Term = term },
        EntryKind.OwnedBuffer => new OwnedEntry { Term = term },
        _ => new UnbufferedEntry { Term = term },
    };

    private class UnbufferedEntry : IRaftLogEntry
    {
        public required long Term { get; init; }

        bool IDataTransferObject.IsReusable => true;

        long? IDataTransferObject.Length => null;

        ValueTask IDataTransferObject.WriteToAsync<TWriter>(TWriter writer, CancellationToken token)
            => writer.WriteAsync(new byte[] { 1, 2, 3 }, token: token);
    }

    private sealed class OwnedEntry : UnbufferedEntry, ISupplier<MemoryAllocator<byte>, MemoryOwner<byte>>
    {
        MemoryOwner<byte> ISupplier<MemoryAllocator<byte>, MemoryOwner<byte>>.Invoke(MemoryAllocator<byte> allocator)
        {
            var owner = allocator(3);
            owner.Span.Fill(1);
            return owner;
        }
    }
}
