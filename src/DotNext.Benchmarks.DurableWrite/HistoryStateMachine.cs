using System.Buffers;
using System.Buffers.Binary;
using System.Diagnostics;
using DotNext.Benchmarks.DurableWrite.Oracles;
using DotNext.IO;
using DotNext.Net.Cluster.Consensus.Raft.StateMachine;

namespace DotNext.Benchmarks.DurableWrite;

/// <summary>
/// A state machine that records the history of applied entries, reports it to the <see cref="OnlineHistoryChecker"/>,
/// and takes a snapshot of the whole history every <c>snapshotInterval</c> entries, so that the write-ahead log
/// compacts while the workload runs.
/// </summary>
/// <remarks>
/// The snapshot holds every applied entry, so a node that installs a snapshot from the leader still has the history
/// from index 1. The <see cref="FailureInjection.DropApplied"/> and <see cref="FailureInjection.ReorderApplied"/> hooks
/// only change what this class reports to the checker and keeps in its history. They exist to show that the oracles
/// catch such a failure; the write-ahead log and the cluster are not changed.
/// </remarks>
internal sealed class HistoryStateMachine : SimpleStateMachine
{
    private const byte NoKey = byte.MaxValue;
    private const int RecordSize = sizeof(long) + sizeof(byte) + sizeof(int) + sizeof(long);
    private const long FirstInjectedIndex = 100L;

    private readonly Lock sync = new();
    private readonly List<AppliedEntry> entries = [];
    private readonly int node;
    private readonly OnlineHistoryChecker? checker;
    private readonly ApplyTimeline? timeline;
    private readonly long snapshotInterval;
    private FailureInjection injection;
    private AppliedEntry? held;
    private long sinceSnapshot;
    private int restores, snapshots, snapshotFailures;

    internal HistoryStateMachine(DirectoryInfo location, int node, long snapshotInterval,
        OnlineHistoryChecker? checker = null, ApplyTimeline? timeline = null, FailureInjection injection = FailureInjection.None)
        : base(location)
    {
        this.node = node;
        this.checker = checker;
        this.timeline = timeline;
        this.snapshotInterval = snapshotInterval;
        this.injection = injection;
    }

    /// <summary>
    /// Arms a test-only failure on this node: the next keyed entry at index 100 or above is dropped or reordered.
    /// </summary>
    internal void Arm(FailureInjection failure)
    {
        lock (sync)
            injection = failure;
    }

    internal bool IsArmed
    {
        get
        {
            lock (sync)
                return injection is not FailureInjection.None;
        }
    }

    internal int Restores => Volatile.Read(in restores);

    internal int Snapshots => Volatile.Read(in snapshots);

    internal int SnapshotFailures => Volatile.Read(in snapshotFailures);

    internal IReadOnlyList<AppliedEntry> History
    {
        get
        {
            lock (sync)
                return entries.ToArray();
        }
    }

    internal static WriteKey? ReadKey(in LogEntry entry)
    {
        if (entry.IsConfiguration || !entry.TryGetPayload(out var payload) || payload.Length < WriteKey.HeaderSize)
            return null;

        Span<byte> header = stackalloc byte[WriteKey.HeaderSize];
        payload.Slice(0, WriteKey.HeaderSize).CopyTo(header);
        return WriteKey.TryRead(header);
    }

    protected override ValueTask<bool> ApplyAsync(LogEntry entry, CancellationToken token)
    {
        var applied = new AppliedEntry(entry.Index, entry.Term, ReadKey(in entry));
        timeline?.OnApplied(entry.Index);

        bool snapshot;
        lock (sync)
        {
            switch (injection)
            {
                case FailureInjection.DropApplied when applied is { Key: not null, Index: >= FirstInjectedIndex }:
                    // The entry is lost by this node: neither reported nor kept.
                    injection = FailureInjection.None;
                    return ValueTask.FromResult(false);
                case FailureInjection.ReorderApplied when applied is { Key: not null, Index: >= FirstInjectedIndex }:
                    if (held is not { } first)
                    {
                        held = applied;
                        return ValueTask.FromResult(false);
                    }

                    // Report the two entries with their writes swapped.
                    held = null;
                    if (first.Index + 1L == applied.Index)
                    {
                        injection = FailureInjection.None;
                        Record(first with { Key = applied.Key });
                        Record(applied with { Key = first.Key });
                    }
                    else
                    {
                        Record(first);
                        Record(applied);
                    }

                    break;
                default:
                    if (held is { } pending)
                    {
                        held = null;
                        Record(pending);
                    }

                    Record(applied);
                    break;
            }

            snapshot = ++sinceSnapshot >= snapshotInterval;
            if (snapshot)
                sinceSnapshot = 0L;
        }

        return ValueTask.FromResult(snapshot);
    }

    // Call under the lock. The write-ahead log does not pass an entry without payload, such as the no-op of a new
    // leader, to the state machine, so the indexes in between are recorded as skipped, with term 0.
    private void Record(in AppliedEntry entry)
    {
        for (var index = entries.Count + 1L; index < entry.Index; index++)
        {
            var skipped = AppliedEntry.CreateSkipped(index);
            entries.Add(skipped);
            checker?.OnApplied(node, in skipped);
        }

        entries.Add(entry);
        checker?.OnApplied(node, in entry);
    }

    protected override async ValueTask PersistAsync(IAsyncBinaryWriter writer, CancellationToken token)
    {
        AppliedEntry[] copy;
        lock (sync)
            copy = entries.ToArray();

        var buffer = ArrayPool<byte>.Shared.Rent(64 * 1024);
        try
        {
            BinaryPrimitives.WriteInt64LittleEndian(buffer, copy.LongLength);
            var offset = sizeof(long);
            foreach (var entry in copy)
            {
                if (offset + RecordSize > buffer.Length)
                {
                    await writer.WriteAsync(buffer.AsMemory(0, offset), null, token).ConfigureAwait(false);
                    offset = 0;
                }

                Write(entry, buffer.AsSpan(offset, RecordSize));
                offset += RecordSize;
            }

            await writer.WriteAsync(buffer.AsMemory(0, offset), null, token).ConfigureAwait(false);
        }
        finally
        {
            ArrayPool<byte>.Shared.Return(buffer);
        }

        Interlocked.Increment(ref snapshots);

        static void Write(in AppliedEntry entry, Span<byte> destination)
        {
            BinaryPrimitives.WriteInt64LittleEndian(destination, entry.Term);
            if (entry.Key is { } key)
            {
                key.Write(destination[sizeof(long)..]);
            }
            else
            {
                destination[sizeof(long)] = NoKey;
                destination[(sizeof(long) + 1)..].Clear();
            }
        }
    }

    protected override async ValueTask RestoreAsync(FileInfo snapshotFile, CancellationToken token)
    {
        var restored = new List<AppliedEntry>();
        var stream = new FileStream(snapshotFile.FullName, FileMode.Open, FileAccess.Read, FileShare.Read, 64 * 1024, useAsync: true);
        await using (stream.ConfigureAwait(false))
        {
            var record = new byte[RecordSize];
            await stream.ReadExactlyAsync(record.AsMemory(0, sizeof(long)), token).ConfigureAwait(false);
            var count = BinaryPrimitives.ReadInt64LittleEndian(record);
            for (var index = 1L; index <= count; index++)
            {
                await stream.ReadExactlyAsync(record, token).ConfigureAwait(false);
                var term = BinaryPrimitives.ReadInt64LittleEndian(record);
                var key = record[sizeof(long)] is NoKey ? default(WriteKey?) : WriteKey.TryRead(record.AsSpan(sizeof(long)));
                restored.Add(new(index, term, key));
            }
        }

        lock (sync)
        {
            entries.Clear();
            entries.AddRange(restored);
            held = null;
            sinceSnapshot = 0L;
            checker?.OnRestored(node, restored);
        }

        Interlocked.Increment(ref restores);
    }

    protected override void OnSnapshotFailed(Exception failure)
    {
        Interlocked.Increment(ref snapshotFailures);
        Trace.TraceWarning($"Snapshot on node {node} failed: {failure}");
    }
}

/// <summary>
/// The time each index was first applied on any node, to measure how far behind the other nodes apply it.
/// </summary>
internal sealed class ApplyTimeline(LatencyHistogram lag)
{
    private const int Size = 1 << 18;
    private const int Mask = Size - 1;
    private readonly Lock sync = new();
    private readonly long[] indexes = new long[Size];
    private readonly long[] timestamps = new long[Size];
    private volatile bool measuring;

    // The lag is recorded in the measured window only.
    internal bool Measuring
    {
        set => measuring = value;
    }

    internal void OnApplied(long index)
    {
        if (!measuring)
            return;

        var now = Stopwatch.GetTimestamp();
        var slot = (int)(index & Mask);
        long first;
        lock (sync)
        {
            if (indexes[slot] < index)
            {
                indexes[slot] = index;
                timestamps[slot] = now;
                return;
            }

            if (indexes[slot] != index)
                return;

            first = timestamps[slot];
        }

        lag.RecordTicks(now - first);
    }
}

internal enum FailureInjection
{
    None = 0,
    DropApplied,
    ReorderApplied,
    NonDurableFollowers,
}
