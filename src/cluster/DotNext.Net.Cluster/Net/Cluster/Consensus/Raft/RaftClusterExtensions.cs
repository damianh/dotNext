namespace DotNext.Net.Cluster.Consensus.Raft;

using Buffers.Binary;
using Text.Json;

/// <summary>
/// Represents extension methods for <see cref="IRaftCluster"/> interface.
/// </summary>
public static class RaftClusterExtensions
{
    /// <summary>
    /// Extends <see cref="IRaftCluster"/> interface.
    /// </summary>
    /// <param name="cluster">The cluster node.</param>
    extension(IRaftCluster cluster)
    {
        /// <summary>
        /// Appends binary log entry and ensures that it is replicated and committed.
        /// </summary>
        /// <remarks>
        /// The entry is stamped with the term of the local node at the time of the call and is accepted only if the node still leads in that term
        /// when the entry is appended; otherwise, <see cref="NotLeaderException"/> is thrown and the caller may retry.
        /// Any other failure, including cancellation, leaves the outcome unknown: the entry may be in the log and may be committed later.
        /// Use an idempotency key in the payload to make a retry safe. See <see cref="RaftCluster{TMember}.ReplicateAsync{TEntry}(TEntry, CancellationToken)"/>.
        /// </remarks>
        /// <param name="payload">The log entry payload.</param>
        /// <param name="context">The context to be passed to the state machine.</param>
        /// <param name="token">The token that can be used to cancel the operation. Cancellation leaves the outcome unknown.</param>
        /// <returns>The task that completes when the appended log entry has been committed by the majority of nodes and applied locally.</returns>
        /// <exception cref="NotLeaderException">The current node is not a leader, has lost leadership, or the term of the entry is no longer the term of the leader.</exception>
        /// <exception cref="OperationCanceledException">The operation has been canceled.</exception>
        public ValueTask ReplicateAsync(ReadOnlyMemory<byte> payload, object? context = null,
            CancellationToken token = default)
            => cluster.ReplicateAsync<BinaryLogEntry>(new() { Content = payload, Term = cluster.Term, Context = context }, token);

        /// <summary>
        /// Appends binary log entry and ensures that it is replicated and committed.
        /// </summary>
        /// <remarks>
        /// The entry is stamped with the term of the local node at the time of the call and is accepted only if the node still leads in that term
        /// when the entry is appended; otherwise, <see cref="NotLeaderException"/> is thrown and the caller may retry.
        /// Any other failure, including cancellation, leaves the outcome unknown: the entry may be in the log and may be committed later.
        /// Use an idempotency key in the payload to make a retry safe. See <see cref="RaftCluster{TMember}.ReplicateAsync{TEntry}(TEntry, CancellationToken)"/>.
        /// </remarks>
        /// <typeparam name="T">The type of the binary formattable log entry.</typeparam>
        /// <param name="payload">The log entry payload.</param>
        /// <param name="context">The context to be passed to the state machine.</param>
        /// <param name="token">The token that can be used to cancel the operation. Cancellation leaves the outcome unknown.</param>
        /// <returns>The task that completes when the appended log entry has been committed by the majority of nodes and applied locally.</returns>
        /// <exception cref="NotLeaderException">The current node is not a leader, has lost leadership, or the term of the entry is no longer the term of the leader.</exception>
        /// <exception cref="OperationCanceledException">The operation has been canceled.</exception>
        public ValueTask ReplicateAsync<T>(T payload, object? context = null, CancellationToken token = default)
            where T : IBinaryFormattable<T>
            => cluster.ReplicateAsync<BinaryLogEntry<T>>(new() { Content = payload, Term = cluster.Term, Context = context }, token);

        /// <summary>
        /// Appends JSON log entry and ensures that it is replicated and committed.
        /// </summary>
        /// <remarks>
        /// The entry is stamped with the term of the local node at the time of the call and is accepted only if the node still leads in that term
        /// when the entry is appended; otherwise, <see cref="NotLeaderException"/> is thrown and the caller may retry.
        /// Any other failure, including cancellation, leaves the outcome unknown: the entry may be in the log and may be committed later.
        /// Use an idempotency key in the payload to make a retry safe. See <see cref="RaftCluster{TMember}.ReplicateAsync{TEntry}(TEntry, CancellationToken)"/>.
        /// </remarks>
        /// <typeparam name="T">The type of the JSON log entry.</typeparam>
        /// <param name="payload">The log entry payload.</param>
        /// <param name="context">The context to be passed to the state machine.</param>
        /// <param name="token">The token that can be used to cancel the operation. Cancellation leaves the outcome unknown.</param>
        /// <returns>The task that completes when the appended log entry has been committed by the majority of nodes and applied locally.</returns>
        /// <exception cref="NotLeaderException">The current node is not a leader, has lost leadership, or the term of the entry is no longer the term of the leader.</exception>
        /// <exception cref="OperationCanceledException">The operation has been canceled.</exception>
        public ValueTask ReplicateJsonAsync<T>(T payload, object? context = null,
            CancellationToken token = default)
            where T : IJsonSerializable<T>
            => cluster.ReplicateAsync<JsonLogEntry<T>>(new() { Content = payload, Term = cluster.Term, Context = context }, token);

        /// <summary>
        /// Ensures linearizable read from underlying state machine.
        /// </summary>
        /// <param name="token">The token that can be used to cancel the operation.</param>
        /// <returns>The task representing asynchronous result.</returns>
        /// <exception cref="OperationCanceledException">The operation has been canceled.</exception>
        /// <exception cref="QuorumUnreachableException">The quorum is not visible to the local node.</exception>
        public ValueTask ApplyReadBarrierAsync(CancellationToken token = default)
            => cluster.ApplyReadBarrierAsync(ReadBarrierType.Strong, token);

        /// <summary>
        /// Gets term number used by Raft algorithm to check the consistency of the cluster.
        /// </summary>
        public long Term => cluster.AuditTrail.Term;
    }
}