using System.Diagnostics.CodeAnalysis;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using Microsoft.Win32.SafeHandles;
using static System.Buffers.Binary.BinaryPrimitives;

namespace DotNext.Net.Cluster.Consensus.Raft.StateMachine;

using Threading;
using BoxedClusterMemberId = Runtime.BoxedValue<ClusterMemberId>;

partial class WriteAheadLog
{
    [SuppressMessage("Usage", "CA2213", Justification = "False positive")]
    private readonly AsyncExclusiveLock stateLock;
    private NodeState state;
    
    [StructLayout(LayoutKind.Auto)]
    private struct NodeState : IDisposable
    {
        private const string FileName = "state";

        private const int LastVotePresenceOffset = 0;
        private const int LastVoteOffset = LastVotePresenceOffset + sizeof(byte);
        private static readonly int TermOffset = LastVoteOffset + ClusterMemberId.Size;
        private static readonly int Size = TermOffset + sizeof(long);
        
        private readonly SafeFileHandle handle;
        private readonly byte[] buffer;
        private volatile BoxedClusterMemberId? votedFor;
        private long term; // volatile
        private (long Term, BoxedClusterMemberId? Vote) staged;
        private bool suspect;

        public NodeState(DirectoryInfo location)
        {
            var path = Path.Combine(location.FullName, FileName);
            long preallocationSize;
            FileMode mode;

            if (File.Exists(path))
            {
                preallocationSize = 0L;
                mode = FileMode.Open;
            }
            else
            {
                preallocationSize = Size;
                mode = FileMode.CreateNew;
            }

            handle = File.OpenHandle(path, mode, FileAccess.ReadWrite, FileShare.Read, FileOptions.WriteThrough, preallocationSize);
            buffer = GC.AllocateUninitializedArray<byte>(Size, pinned: true);

            if (RandomAccess.Read(handle, buffer, fileOffset: 0L) < buffer.Length)
            {
                Array.Clear(buffer);
                RandomAccess.Write(handle, buffer, fileOffset: 0L);
            }

            if (Unsafe.BitCast<byte, bool>(buffer[LastVotePresenceOffset]))
                votedFor = BoxedClusterMemberId.Box(new ClusterMemberId(buffer.AsSpan(LastVoteOffset)));
            term = ReadInt64LittleEndian(buffer.AsSpan(TermOffset));
        }

        public readonly long Term => Atomic.Read(in term);
        
        public readonly bool IsVotedFor(in ClusterMemberId expected) => IPersistentState.IsVotedFor(votedFor, expected);
        
        // Staging fills the write buffer with the complete next record but leaves the published term and vote
        // untouched. The published values change only after FlushAsync succeeds, so a failed or cancelled write
        // never lets the node act on a term or vote that is not durable.
        public void StageTerm(long value, bool resetLastVote)
            => Stage(value, resetLastVote ? null : votedFor);

        public long StageIncrementedTerm(ClusterMemberId id)
        {
            var result = Term + 1L;
            Stage(result, BoxedClusterMemberId.Box(id));
            return result;
        }

        public void StageVote(ClusterMemberId id)
            => Stage(Term, BoxedClusterMemberId.Box(id));

        private void Stage(long newTerm, BoxedClusterMemberId? newVote)
        {
            WriteInt64LittleEndian(buffer.AsSpan(TermOffset), newTerm);
            if (newVote is null)
            {
                buffer[LastVotePresenceOffset] = Unsafe.BitCast<bool, byte>(false);
            }
            else
            {
                buffer[LastVotePresenceOffset] = Unsafe.BitCast<bool, byte>(true);
                newVote.Value.Format(buffer.AsSpan(LastVoteOffset));
            }

            staged = (newTerm, newVote);
        }

        public void Publish()
        {
            var (newTerm, newVote) = staged;
            votedFor = newVote;
            Atomic.Write(ref term, newTerm);
        }

        public readonly ValueTask FlushAsync(CancellationToken token = default)
            => RandomAccess.WriteAsync(handle, buffer, fileOffset: 0L, token);

        public readonly bool IsSuspect => suspect;

        // A failed or cancelled write may still have reached the file, so the file, not memory, is the truth.
        public void MarkSuspect() => suspect = true;

        // Publishes the record read back from the file. Whatever is on disk is durable, so publishing it is safe.
        // Returns true if the record differs from what was published before the failed write.
        public readonly async ValueTask<byte[]> ReadRecordAsync(CancellationToken token)
        {
            var actual = new byte[Size];
            if (await RandomAccess.ReadAsync(handle, actual, fileOffset: 0L, token).ConfigureAwait(false) < actual.Length)
                throw new IOException("The term/vote record cannot be read back after a failed write");

            return actual;
        }

        public bool Reconcile(byte[] actual)
        {
            var diskTerm = ReadInt64LittleEndian(actual.AsSpan(TermOffset));
            var diskVote = Unsafe.BitCast<byte, bool>(actual[LastVotePresenceOffset])
                ? BoxedClusterMemberId.Box(new ClusterMemberId(actual.AsSpan(LastVoteOffset)))
                : null;

            var changed = diskTerm != Term || !SameVote(diskVote, votedFor);
            votedFor = diskVote;
            Atomic.Write(ref term, diskTerm);
            suspect = false;
            return changed;

            static bool SameVote(BoxedClusterMemberId? x, BoxedClusterMemberId? y)
                => x is null ? y is null : y is not null && x.Value == y.Value;
        }
        public void Dispose()
        {
            handle?.Dispose();
            this = default;
        }
    }

    /// <inheritdoc/>
    bool IPersistentState.IsVotedFor(in ClusterMemberId id) => state.IsVotedFor(in id);

    /// <inheritdoc/>
    long IPersistentState.Term => state.Term;

    // A failed write may have persisted the record anyway. Until the record is read back, nothing may be staged:
    // the next write would derive from the published term and could lower the durable one. When the disk turns out
    // to differ from what this request was decided on, the request fails and the caller re-evaluates.
    private async ValueTask ReconcileStateAsync(CancellationToken token)
    {
        if (state.IsSuspect && state.Reconcile(await state.ReadRecordAsync(token).ConfigureAwait(false)))
            throw new IOException("The term/vote record was changed by an interrupted write");
    }

    // Best effort right after a failed write, so that RPCs do not keep reading a term older than the durable one.
    // If the read-back fails too, the record stays suspect and the next term/vote operation fails closed.
    private async ValueTask TryReconcileAsync()
    {
        try
        {
            state.Reconcile(await state.ReadRecordAsync(CancellationToken.None).ConfigureAwait(false));
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            // stays suspect
        }
    }

    private async ValueTask FlushStateAsync(CancellationToken token)
    {
        try
        {
            await state.FlushAsync(token).ConfigureAwait(false);
        }
        catch
        {
            state.MarkSuspect();
            await TryReconcileAsync().ConfigureAwait(false);
            throw;
        }

        state.Publish();
    }

    /// <inheritdoc/>
    async ValueTask<long> IPersistentState.IncrementTermAsync(ClusterMemberId member, CancellationToken token)
    {
        await stateLock.AcquireAsync(token).ConfigureAwait(false);
        long term;
        try
        {
            await ReconcileStateAsync(token).ConfigureAwait(false);
            term = state.StageIncrementedTerm(member);
            await FlushStateAsync(token).ConfigureAwait(false);
        }
        finally
        {
            stateLock.Release();
        }

        return term;
    }

    /// <inheritdoc/>
    async ValueTask IPersistentState.UpdateTermAsync(long term, bool resetLastVote, CancellationToken token)
    {
        await stateLock.AcquireAsync(token).ConfigureAwait(false);
        try
        {
            await ReconcileStateAsync(token).ConfigureAwait(false);
            state.StageTerm(term, resetLastVote);
            await FlushStateAsync(token).ConfigureAwait(false);
        }
        finally
        {
            stateLock.Release();
        }
    }

    /// <inheritdoc/>
    async ValueTask IPersistentState.UpdateVotedForAsync(ClusterMemberId member, CancellationToken token)
    {
        await stateLock.AcquireAsync(token).ConfigureAwait(false);
        try
        {
            await ReconcileStateAsync(token).ConfigureAwait(false);
            state.StageVote(member);
            await FlushStateAsync(token).ConfigureAwait(false);
        }
        finally
        {
            stateLock.Release();
        }
    }
}
