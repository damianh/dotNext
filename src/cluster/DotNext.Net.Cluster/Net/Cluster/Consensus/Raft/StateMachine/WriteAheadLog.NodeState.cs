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

    /// <inheritdoc/>
    async ValueTask<long> IPersistentState.IncrementTermAsync(ClusterMemberId member, CancellationToken token)
    {
        await stateLock.AcquireAsync(token).ConfigureAwait(false);
        long term;
        try
        {
            term = state.StageIncrementedTerm(member);
            await state.FlushAsync(token).ConfigureAwait(false);
            state.Publish();
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
            state.StageTerm(term, resetLastVote);
            await state.FlushAsync(token).ConfigureAwait(false);
            state.Publish();
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
            state.StageVote(member);
            await state.FlushAsync(token).ConfigureAwait(false);
            state.Publish();
        }
        finally
        {
            stateLock.Release();
        }
    }
}