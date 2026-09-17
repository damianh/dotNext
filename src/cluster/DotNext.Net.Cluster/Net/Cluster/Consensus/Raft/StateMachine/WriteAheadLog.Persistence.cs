namespace DotNext.Net.Cluster.Consensus.Raft.StateMachine;

using Threading;

partial class WriteAheadLog
{
    private readonly AsyncExclusiveLock persistenceLock = new();
    private readonly OverwriteJournal overwriteJournal;
    private readonly DirectoryInfo dataLocation, metadataLocation;
    private CheckpointVersion1 durableState;
    private long stagedLastIndex;

    // Call under Append (and, for replacements, Overwrite) and persistenceLock after pre-mutation checks.
    // Journal publication can fail after modifying storage, so the caller must already be in the fatal scope.
    private async ValueTask PrepareAppendAsync(long firstIndex, CancellationToken token)
    {
        stagedLastIndex = LastEntryIndex;
        firstIndex = long.Max(firstIndex, LastCommittedEntryIndex + 1L);
        if (firstIndex <= stagedLastIndex)
        {
            await overwriteJournal.WriteAsync(checkpoint.Generation + 1L, firstIndex,
                stagedLastIndex, metadataPages, token).ConfigureAwait(false);
        }
    }

    private async ValueTask PersistAppendAsync(long firstIndex, CancellationToken token)
    {
        var snapshotIndex = SnapshotIndex;
        var lastIndex = long.Max(stagedLastIndex, snapshotIndex);
        firstIndex = long.Max(firstIndex, snapshotIndex);
        if (firstIndex <= lastIndex)
            await Flush(firstIndex, lastIndex, token).ConfigureAwait(false);

        Checkpoint.FlushDirectory(dataLocation);
        Checkpoint.FlushDirectory(metadataLocation);
        await PersistCheckpointAsync(lastIndex, long.Max(LastCommittedEntryIndex, snapshotIndex),
            snapshotIndex, dataPages.LastWrittenAddress, token).ConfigureAwait(false);
        LastEntryIndex = lastIndex;
        overwriteJournal.Clear();
    }

    private async ValueTask PersistCheckpointAsync(long lastIndex, long committedIndex,
        long snapshotIndex, ulong writePosition, CancellationToken token)
    {
        var next = new CheckpointVersion1(
            long.Max(durableState.Checkpoint, committedIndex),
            lastIndex,
            writePosition,
            long.Max(durableState.SnapshotIndex, snapshotIndex),
            checkpoint.Generation + 1L);
        await checkpoint.UpdateAsync(next, token).ConfigureAwait(false);
        durableState = next;
        Atomic.Write(ref nextUnflushedIndex, next.Checkpoint + 1L);
        flushCompleted?.Signal(resumeAll: true);
    }
}
