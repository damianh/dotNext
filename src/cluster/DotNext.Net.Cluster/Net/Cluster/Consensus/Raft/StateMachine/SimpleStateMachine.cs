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
        var writer = await task.ConfigureAwait(false);
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
        var writer = await task.ConfigureAwait(false);
        writer.Dispose();
        writer.Rollback();
        Interlocked.CompareExchange(ref snapshottingProcess, null, task);
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
            // The local snapshot failed on its own and stays in place, so the same snapshot cannot be applied again.
            // The cancellation doesn't belong to the request: fail closed.
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