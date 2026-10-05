using System.Diagnostics;
using DotNext.Benchmarks.DurableWrite.Oracles;
using DotNext.IO.Log;
using DotNext.Net.Cluster.Consensus.Raft;
using DotNext.Net.Cluster.Consensus.Raft.InProcess;
using DotNext.Net.Cluster.Consensus.Raft.StateMachine;

namespace DotNext.Benchmarks.DurableWrite;

/// <summary>
/// The raw write-ahead log, without replication: the lower bound of a durable write on this machine.
/// </summary>
internal static class WalMode
{
    private const int Node = 0;
    private static readonly TimeSpan RecoveryTimeout = TimeSpan.FromMinutes(2);

    internal static async Task RunAsync(CellRun run, FsyncProbe? probe)
    {
        var spec = run.Spec;
        var root = Path.Combine(run.Root, $"node{Node}");
        var stateMachine = NodeStorage.CreateStateMachine(root, Node, spec.SnapshotInterval, run.Checker, timeline: null);
        await stateMachine.RestoreAsync(run.RunToken).ConfigureAwait(false);
        var wal = new WriteAheadLog(NodeStorage.CreateOptions(root, spec.Memory, spec.NoBuffering, Node), stateMachine);
        long lastIndex, appliedIndex;
        IReadOnlyList<AppliedEntry> history, walTail;
        try
        {
            await wal.InitializeAsync(run.RunToken).ConfigureAwait(false);
            var phases = run.RunPhasesAsync(() => new BacklogSample(
                wal.LastEntryIndex - wal.LastCommittedEntryIndex,
                wal.LastCommittedEntryIndex - wal.LastAppliedIndex,
                FollowerLag: 0L));

            var workers = spec.Kind is CellKind.WalBatch
                ? [BatchWriterAsync(run, wal)]
                : Enumerable.Range(0, spec.Concurrency).Select(client => AppendWriterAsync(run, wal, client)).ToArray();

            try
            {
                await Task.WhenAll(workers).ConfigureAwait(false);
            }
            finally
            {
                run.Stop("worker");
                await phases.ConfigureAwait(false);
            }

            lastIndex = wal.LastEntryIndex;
            appliedIndex = wal.LastAppliedIndex;
            history = stateMachine.History;
            walTail = await NodeStorage.ReadAsync(wal, history.Count + 1L, appliedIndex, run.RunToken).ConfigureAwait(false);
        }
        finally
        {
            await wal.DisposeAsync().ConfigureAwait(false);
            await stateMachine.DisposeAsync().ConfigureAwait(false);
        }

        run.CompleteLoad(probe);

        var checker = run.Checker;
        run.Report.Oracles.Checked.AddRange([OnlineHistoryChecker.ApplyOrder, OnlineHistoryChecker.AcknowledgedWrites, OnlineHistoryChecker.PrefixAgreement]);
        checker.CheckFinal([history]);

        var durable = await NodeStorage.RecoverAsync(root, spec.Memory, spec.NoBuffering, Node, RecoveryTimeout, run.RunToken).ConfigureAwait(false);
        run.CheckDurability([durable]);
        run.Reconcile(Node, history, appliedIndex, walTail, durable);
        run.AddNode(Node, "single", durable: true, root, lastIndex, stateMachine, durable.Count);
        run.CompleteWriteAmplification(Node);
    }

    // AppendAsync, then CommitAsync and wait for the entry to be applied: the acknowledgment of a single-node log.
    private static async Task AppendWriterAsync(CellRun run, WriteAheadLog wal, int client)
    {
        await Task.Yield();
        var buffer = run.Payload.CreateBuffer();
        var token = run.RunToken;
        for (var seq = 1L; !run.IsStopped; seq++)
        {
            var key = new WriteKey(WriteKey.ClosedLoop, client, seq);
            var entry = new BinaryLogEntry { Term = 1L, Content = run.Payload.Write(buffer, key) };
            run.OnOffered();
            run.BeginRequest();
            var start = Stopwatch.GetTimestamp();
            long index, appended;
            try
            {
                index = await wal.AppendAsync(entry, token).ConfigureAwait(false);
                appended = Stopwatch.GetTimestamp();
                await wal.CommitAsync(index, token).ConfigureAwait(false);
                await wal.WaitForApplyAsync(index, token).ConfigureAwait(false);
            }
            finally
            {
                run.EndRequest();
            }

            var end = Stopwatch.GetTimestamp();
            Acknowledge(run, key, index);
            if (run.Measuring)
            {
                run.AppendLatency.RecordTicks(appended - start);
                run.CommitApplyLatency.RecordTicks(end - appended);
            }

            run.OnAcknowledged(1, start, end);
        }
    }

    // A batch appended at an explicit index with one durable persist, then committed: the path a follower takes for
    // a multi-entry AppendEntries.
    private static async Task BatchWriterAsync(CellRun run, WriteAheadLog wal)
    {
        await Task.Yield();
        var size = run.Spec.BatchSize;
        var buffers = new byte[size][];
        for (var i = 0; i < size; i++)
            buffers[i] = run.Payload.CreateBuffer();

        var entries = new BinaryLogEntry[size];
        var keys = new WriteKey[size];
        var token = run.RunToken;
        var seq = 0L;
        while (!run.IsStopped)
        {
            for (var i = 0; i < size; i++)
            {
                keys[i] = new(WriteKey.ClosedLoop, 0, ++seq);
                entries[i] = new() { Term = 1L, Content = run.Payload.Write(buffers[i], keys[i]) };
            }

            run.OnOffered(size);
            run.BeginRequest(size);
            var first = wal.LastEntryIndex + 1L;
            var last = first + size - 1L;
            var start = Stopwatch.GetTimestamp();
            long appended;
            try
            {
                await wal.AppendAsync(new LogEntryProducer<BinaryLogEntry>(entries), first, token: token).ConfigureAwait(false);
                appended = Stopwatch.GetTimestamp();
                await wal.CommitAsync(last, token).ConfigureAwait(false);
                await wal.WaitForApplyAsync(last, token).ConfigureAwait(false);
            }
            finally
            {
                run.EndRequest(size);
            }

            var end = Stopwatch.GetTimestamp();
            for (var i = 0; i < size; i++)
                Acknowledge(run, keys[i], first + i);

            if (run.Measuring)
            {
                run.AppendLatency.RecordTicks(appended - start);
                run.CommitApplyLatency.RecordTicks(end - appended);
            }

            run.OnAcknowledged(size, start, end);
        }
    }

    private static void Acknowledge(CellRun run, WriteKey key, long index)
    {
        var applied = run.Checker.OnAcknowledged(Node, key);
        if (applied >= 0L && applied != index)
        {
            run.Checker.Report(new SafetyViolationException(OnlineHistoryChecker.AcknowledgedWrites,
                $"'{key}' was appended at index {index} but applied at index {applied}"));
        }
    }
}
