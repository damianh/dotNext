using System.Diagnostics.CodeAnalysis;
using System.Runtime.CompilerServices;
using DotNext.IO;
using DotNext.Runtime.CompilerServices;

namespace DotNext.Net.Cluster.Consensus.Raft.StateMachine;

using Commands;

/// <summary>
/// Represents a state machine that keeps the entire state in the memory but periodically
/// creates a persistent snapshot for recovery.
/// </summary>
/// <remarks>
/// The state machine opts in to <see cref="IStateMachine.IsSnapshotInstallCancellationSafe"/>. Installing a snapshot
/// received from the leader first transfers it to a temporary file, which observes the cancellation token of the request
/// and leaves neither the state nor the published snapshot changed if canceled. The subsequent
/// <see cref="RestoreAsync(FileInfo, CancellationToken)"/> is not affected by that token.
/// <para>
/// A snapshot is taken in the background when <see cref="ApplyAsync(LogEntry, CancellationToken)"/> returns
/// <see langword="true"/>, and is published before the next entry is applied or replaced by a snapshot received from
/// the leader. If creating it fails or is canceled for any reason other than the disposal of this object, the failed
/// snapshot is dropped: its temporary file is deleted, nothing is published, and the previous snapshot stays in place.
/// The failure is reported to <see cref="OnSnapshotFailed(Exception)"/> and does not fault the write-ahead log.
/// The next entry is applied as usual, and the next time <see cref="ApplyAsync(LogEntry, CancellationToken)"/> returns
/// <see langword="true"/> a new attempt starts. There is no retry on its own. Until a snapshot succeeds,
/// recovery after a restart starts from the last good snapshot.
/// </para>
/// <para>
/// A failure to publish an already written snapshot (the final rename) is not dropped. It is rethrown to the caller,
/// because the published file can be in an unknown state.
/// </para>
/// </remarks>
public abstract partial class SimpleStateMachine : IAsyncDisposable, IStateMachine
{
    private readonly CancellationToken lifetimeToken;
    private readonly DirectoryInfo location;
    private readonly Task<SnapshotWriter> sentinel;
    private readonly Func<long, FileInfo, SnapshotWriter> writerFactory;

    [SuppressMessage("Usage", "CA2213", Justification = "See DisposeAsync() implementation")]
    private CancellationTokenSource? lifetimeSource;
    private long appliedIndex;
    private volatile Snapshot? snapshot;
    private volatile Task<SnapshotWriter>? snapshottingProcess;

    /// <summary>
    /// Initializes a new simple state machine.
    /// </summary>
    /// <param name="location"></param>
    protected SimpleStateMachine(DirectoryInfo location)
        : this(location, CreateSnapshotWriterFactory())
    {
    }

    internal SimpleStateMachine(DirectoryInfo location, Func<long, FileInfo, SnapshotWriter> writerFactory)
    {
        ArgumentNullException.ThrowIfNull(writerFactory);

        if (!location.Exists)
            location.Create();

        this.location = location;
        lifetimeToken = (lifetimeSource = new()).Token;
        sentinel = Task.FromException<SnapshotWriter>(new ObjectDisposedException(GetType().Name));
        this.writerFactory = writerFactory;
        
        // if there is a snapshot on disk, it must be loaded via RestoreAsync before use;
        // otherwise, there is nothing to restore
        appliedIndex = GetSnapshots().FirstOrDefault() is null ? 0L : -1L;
    }

    /// <summary>
    /// Restores the in-memory state from the snapshot.
    /// </summary>
    /// <param name="snapshotFile">The snapshot file.</param>
    /// <param name="token">The token that can be used to cancel the operation.</param>
    /// <returns>The task representing asynchronous execution of the method.</returns>
    /// <remarks>
    /// When the snapshot is installed by the write-ahead log, the token is not the token of the request
    /// that delivers the snapshot: it is canceled only when this state machine is disposed. Once the restoration
    /// starts, the in-memory state can be partially rebuilt, and a canceled request must not interrupt it.
    /// If the restoration throws <see cref="OperationCanceledException"/> in that case, the installation fails
    /// with <see cref="InvalidOperationException"/> and the write-ahead log is faulted.
    /// </remarks>
    /// <exception cref="OperationCanceledException">The operation has been canceled.</exception>
    protected abstract ValueTask RestoreAsync(FileInfo snapshotFile, CancellationToken token);

    /// <summary>
    /// Restores the in-memory state from the snapshot.
    /// </summary>
    /// <remarks>
    /// This method is intended to be called from <see cref="RestoreAsync(System.IO.FileInfo,System.Threading.CancellationToken)"/>
    /// when the compatibility with <see cref="CommandInterpreter"/> is required.
    /// </remarks>
    /// <param name="interpreter">The command interpreter.</param>
    /// <param name="snapshotFile">The snapshot file.</param>
    /// <param name="token">The token that can be used to cancel the operation.</param>
    /// <returns>The task representing asynchronous execution of the method.</returns>
    /// <exception cref="OperationCanceledException">The operation has been canceled.</exception>
    protected static async ValueTask RestoreAsync(CommandInterpreter interpreter, FileInfo snapshotFile, CancellationToken token)
        => await interpreter.InterpretAsync(new Snapshot(snapshotFile, SnapshotWriter.CreateDefault), token).ConfigureAwait(false);
    
    /// <summary>
    /// Persists the current state.
    /// </summary>
    /// <param name="writer">The writer that can be used to write the state.</param>
    /// <param name="token">The token that can be used to cancel the operation.</param>
    /// <returns>The task representing asynchronous execution of the method.</returns>
    protected abstract ValueTask PersistAsync(IAsyncBinaryWriter writer, CancellationToken token);

    /// <summary>
    /// Restores the in-memory state from the most recent snapshot stored on the disk.
    /// </summary>
    /// <param name="token">The token that can be used to cancel the operation.</param>
    /// <returns>The task representing asynchronous execution of the method.</returns>
    /// <exception cref="OperationCanceledException">The operation has been canceled.</exception>
    public ValueTask RestoreAsync(CancellationToken token = default)
    {
        // find the most recent snapshot
        foreach (var candidate in GetSnapshots())
        {
            if (snapshot is null || candidate.Index > snapshot.Index)
            {
                snapshot = candidate;
                appliedIndex = candidate.Index;
            }
        }

        if (snapshot is { File: { } snapshotFile })
            return RestoreAsync(snapshotFile, token);

        appliedIndex = 0L;
        return ValueTask.CompletedTask;
    }

    /// <inheritdoc/>
    ISnapshot? ISnapshotManager.Snapshot => appliedIndex >= 0L
        ? snapshot
        : throw new InvalidOperationException(ExceptionMessages.StateMachineIsNotRestored);

    /// <inheritdoc/>
    ValueTask ISnapshotManager.ReclaimGarbageAsync(long watermark, CancellationToken token)
    {
        var task = ValueTask.CompletedTask;
        try
        {
            foreach (var candidate in GetSnapshots())
            {
                token.ThrowIfCancellationRequested();
                if (candidate.Index < watermark)
                    candidate.File.Delete();
            }
        }
        catch (OperationCanceledException e)
        {
            task = ValueTask.FromCanceled(e.CancellationToken);
        }
        catch (Exception e)
        {
            task = ValueTask.FromException(e);
        }

        return task;
    }

    /// <inheritdoc/>
    bool IStateMachine.IsSnapshotInstallCancellationSafe => true;

    /// <inheritdoc/>
    ValueTask<long> IStateMachine.ApplyAsync(LogEntry entry, CancellationToken token)
    {
        return appliedIndex >= entry.Index
            ? ValueTask.FromResult(appliedIndex)
            : entry.IsSnapshot
                ? InstallSnapshotAsync(entry, token)
                : ApplyCoreAsync(entry, token);
    }

    private async ValueTask<long> ApplyCoreAsync(LogEntry entry, CancellationToken token)
    {
        await EndSnapshottingAsync(commit: true).ConfigureAwait(false);

        if (await ApplyAsync(entry, token).ConfigureAwait(false))
        {
            await SetSnapshottingProcessAsync(BeginSnapshottingAsync(entry.Index, entry.Term, lifetimeToken)).ConfigureAwait(false);
        }

        return appliedIndex = entry.Index;
    }

    private Task SetSnapshottingProcessAsync(Task<SnapshotWriter> task)
        => snapshottingProcess is not null || Interlocked.CompareExchange(ref snapshottingProcess, task, null) is not null
            ? task
            : Task.CompletedTask;

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private Task EndSnapshottingAsync(bool commit)
        => snapshottingProcess is not { } task
            ? Task.CompletedTask
            : commit
                ? InstallSnapshotAsync(task)
                : RollbackSnapshotAsync(task);

    private async Task InstallSnapshotAsync(Task<SnapshotWriter> task)
    {
        SnapshotWriter writer;
        try
        {
            writer = await task.ConfigureAwait(false);
        }
        catch (Exception e) when (!lifetimeToken.IsCancellationRequested)
        {
            DropFailedSnapshot(task, e);
            return;
        }

        writer.Dispose();

        if (ReferenceEquals(Interlocked.CompareExchange(ref snapshottingProcess, null, task), task))
        {
            try
            {
                writer.Commit();
            }
            catch
            {
                writer.Rollback();
                throw;
            }

            snapshot = new Snapshot(writer.Destination, writerFactory);
        }
    }

    private async Task RollbackSnapshotAsync(Task<SnapshotWriter> task)
    {
        SnapshotWriter writer;
        try
        {
            writer = await task.ConfigureAwait(false);
        }
        catch (Exception e) when (!lifetimeToken.IsCancellationRequested)
        {
            DropFailedSnapshot(task, e);
            return;
        }

        writer.Dispose();
        try
        {
            writer.Rollback();
        }
        catch (Exception e) when (!lifetimeToken.IsCancellationRequested)
        {
            // Nothing was published; a failed cleanup leaves at most a temporary file. Drop it like any failed snapshot.
            DropFailedSnapshot(task, e);
            return;
        }

        Interlocked.CompareExchange(ref snapshottingProcess, null, task);
    }

    // The writer of a failed snapshot is not committed. Only the caller that clears the process reports the failure.
    private void DropFailedSnapshot(Task<SnapshotWriter> task, Exception failure)
    {
        if (ReferenceEquals(Interlocked.CompareExchange(ref snapshottingProcess, null, task), task))
            OnSnapshotFailed(failure);
    }

    /// <summary>
    /// Called when a background snapshot has failed and was dropped.
    /// </summary>
    /// <remarks>
    /// The failure is observed when the state machine is about to apply the next entry or to install a snapshot
    /// received from the leader, not at the moment it happens. It is not called when the state machine is being disposed.
    /// The default implementation does nothing. An exception thrown by this method is propagated to that caller,
    /// which makes the write-ahead log fail.
    /// </remarks>
    /// <param name="failure">The exception thrown by <see cref="PersistAsync"/> or by writing the snapshot to the disk.
    /// It is <see cref="OperationCanceledException"/> if the snapshot was canceled. It can also be the failure to
    /// clean up a snapshot that was superseded by a snapshot received from the leader.</param>
    protected virtual void OnSnapshotFailed(Exception failure)
    {
    }

    [AsyncMethodBuilder(typeof(SpawningAsyncTaskMethodBuilder<>))]
    private async Task<SnapshotWriter> BeginSnapshottingAsync(long index, long term, CancellationToken token)
    {
        var writer = writerFactory.Invoke(0L, Snapshot.CreateSnapshotFile(location, index, term));
        try
        {
            await PersistAsync(writer, token).ConfigureAwait(false);
            await writer.WriteAsync(token).ConfigureAwait(false);
            writer.FlushToDisk();
        }
        catch
        {
            writer.Dispose();
            writer.Rollback();
            throw;
        }

        return writer;
    }

    /// <summary>
    /// Applies the log entry to this state machine.
    /// </summary>
    /// <param name="entry">The log entry to apply to the current state machine.</param>
    /// <param name="token">The token that can be used to cancel the operation.</param>
    /// <returns><see langword="true"/> if the current state must be persisted; otherwise, <see langword="false"/>.</returns>
    /// <exception cref="OperationCanceledException">The operation has been canceled.</exception>
    protected abstract ValueTask<bool> ApplyAsync(LogEntry entry, CancellationToken token);

    private async ValueTask<long> InstallSnapshotAsync(LogEntry entry, CancellationToken token)
    {
        // Only a snapshot in progress on this node is dropped here, which doesn't change the application state.
        // It is safe to repeat, because the rollback clears the process.
        try
        {
            await EndSnapshottingAsync(commit: false).ConfigureAwait(false);
        }
        catch (OperationCanceledException e)
        {
            // A failed local snapshot is dropped, so this is only the disposal of the state machine. The cancellation
            // doesn't belong to the request: fail closed.
            throw new InvalidOperationException(ExceptionMessages.SnapshotInProgressCanceled, e);
        }

        // Transferring the snapshot to a file leaves the application state untouched and observes the request token.
        var newSnapshot = new Snapshot(location, entry.Index, entry.Term, writerFactory);
        await newSnapshot.ReadFromAsync(entry, token).ConfigureAwait(false);

        // Once the restore starts, the state can be partially rebuilt and there is no way back. Cancellation of the
        // request, which is routine, must not interrupt it. Only the disposal of this object can.
        try
        {
            await RestoreAsync(newSnapshot.File, lifetimeToken).ConfigureAwait(false);
        }
        catch (OperationCanceledException e)
        {
            // Not a routine cancellation of the request: the state can be partial, so the caller must fail closed.
            throw new InvalidOperationException(ExceptionMessages.SnapshotRestoreCanceled, e);
        }

        snapshot = newSnapshot;
        return appliedIndex = entry.Index;
    }

    private async ValueTask DisposeImplAsync(CancellationTokenSource cts)
    {
        using (cts)
        {
            await cts.CancelAsync().ConfigureAwait(false);
        }

        var task = Interlocked.Exchange(ref snapshottingProcess, sentinel);
        if (task is not null && !ReferenceEquals(task, sentinel))
        {
            await task.As<Task>().ConfigureAwait(ConfigureAwaitOptions.SuppressThrowing);
        }
    }

    /// <inheritdoc/>
    public virtual ValueTask DisposeAsync()
        => Interlocked.Exchange(ref lifetimeSource, null) is { } cts ? DisposeImplAsync(cts) : ValueTask.CompletedTask;
}