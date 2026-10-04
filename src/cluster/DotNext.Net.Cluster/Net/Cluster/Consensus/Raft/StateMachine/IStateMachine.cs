namespace DotNext.Net.Cluster.Consensus.Raft.StateMachine;

/// <summary>
/// Represents state machine.
/// </summary>
public interface IStateMachine : ISnapshotManager
{
    /// <summary>
    /// Applies the log entry to the underlying state machine.
    /// </summary>
    /// <remarks>
    /// This method is never called concurrently by <see cref="WriteAheadLog"/> infrastructure. However,
    /// it can be called concurrently with <see cref="ISnapshotManager.Snapshot"/> or <see cref="ISnapshotManager.ReclaimGarbageAsync"/>
    /// methods. The implementation can create a new snapshot on the disk. In this case, it should replace the current snapshot
    /// and <see cref="ISnapshotManager.Snapshot"/> will return newly generated snapshot.
    /// <para>
    /// If <see cref="LogEntry.IsSnapshot"/> is <see langword="true"/>, the method installs the snapshot received from
    /// the leader, and the token is the token of that request. It is canceled when the connection drops, the request
    /// times out or the leader is lost, so cancellation is routine. The <see cref="WriteAheadLog"/> treats an
    /// <see cref="OperationCanceledException"/> as routine, and keeps the log usable, only if
    /// <see cref="IsSnapshotInstallCancellationSafe"/> is <see langword="true"/> and the token passed to this method is
    /// canceled. Any other exception, and any cancellation from a state machine that doesn't opt in, is fatal:
    /// the log rejects all subsequent operations until it is reopened.
    /// </para>
    /// </remarks>
    /// <param name="entry">The log entry to be applied.</param>
    /// <param name="token">The token that can be used to cancel the operation.</param>
    /// <returns>The index of the last applied log entry. It should be greater than or equal to <see cref="LogEntry.Index"/>.</returns>
    /// <exception cref="OperationCanceledException">The operation has been canceled.</exception>
    ValueTask<long> ApplyAsync(LogEntry entry, CancellationToken token);

    /// <summary>
    /// Gets a value that indicates whether canceling the installation of a snapshot leaves this state machine unchanged.
    /// </summary>
    /// <remarks>
    /// Return <see langword="true"/> only if <see cref="ApplyAsync"/> guarantees the following for a snapshot entry:
    /// if it throws <see cref="OperationCanceledException"/> because its token was canceled, then the application
    /// state, <see cref="ISnapshotManager.Snapshot"/> and the applied index are exactly what they were before the call,
    /// and the same snapshot can be applied again later. Transferring the snapshot to a temporary location can observe
    /// the token. Rebuilding the application state from the transferred snapshot must not, because the state machine
    /// can no longer roll it back once it is partially rebuilt; use a token that is not tied to the request for that
    /// step, for example, one that is canceled only when the state machine is disposed.
    /// <para>
    /// Built-in transports cancel the request token when the payload source fails. A custom transport should provide
    /// the snapshot handler with a token whose source it controls, cancel that source before surfacing a payload-read
    /// failure, and report cancellation from the snapshot payload. This lets an opted-in state machine roll back a
    /// transfer that did not reach restoration.
    /// </para>
    /// <para>
    /// A state machine that returns <see langword="true"/> but restores its state under the request token risks running
    /// with a partially restored state after the log stays usable. The default value is <see langword="false"/>, which
    /// keeps every cancellation of a snapshot installation fatal for the <see cref="WriteAheadLog"/>.
    /// </para>
    /// </remarks>
    bool IsSnapshotInstallCancellationSafe => false;

    /// <summary>
    /// Gets the version of the replication protocol.
    /// </summary>
    int Version => 0;

    /// <summary>
    /// Creates no-op state machine.
    /// </summary>
    /// <param name="snapshotThreshold">The number of log entries to be squashed as a snapshot.</param>
    /// <returns>A new instance of the state machine.</returns>
    public static IStateMachine CreateNoOp(long snapshotThreshold = 10L)
    {
        ArgumentOutOfRangeException.ThrowIfLessThan(snapshotThreshold, 2L);

        return new NoOpStateMachine(snapshotThreshold);
    }
}