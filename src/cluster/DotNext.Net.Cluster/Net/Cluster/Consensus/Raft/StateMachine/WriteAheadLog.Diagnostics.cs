using System.Diagnostics;
using System.Diagnostics.Metrics;
using System.Diagnostics.Tracing;

namespace DotNext.Net.Cluster.Consensus.Raft.StateMachine;

// Opt-in persistence diagnostics (#123): the time of each phase of a persist cycle, and lock wait and hold times.
// Nothing is measured unless a MeterListener subscribes to the instruments or an EventListener, EventPipe or ETW
// session enables WriteAheadLogEventSource with the Persistence keyword; then only timestamps are added, never
// a change to ordering, locking or durability.
partial class WriteAheadLog
{
    private const string PhaseMeterAttribute = "dotnext.wal.phase";
    private const string CauseMeterAttribute = "dotnext.wal.cause";
    private const string LockMeterAttribute = "dotnext.wal.lock";

    private static readonly Histogram<double> PersistPhaseDurationMeter, LockWaitDurationMeter, LockHoldDurationMeter;

    private static bool IsPersistPhaseTracingEnabled
        => PersistPhaseDurationMeter.Enabled || WriteAheadLogEventSource.Log.IsPersistenceEnabled;

    private static bool IsLockTracingEnabled
        => LockWaitDurationMeter.Enabled || LockHoldDurationMeter.Enabled || WriteAheadLogEventSource.Log.IsPersistenceEnabled;

    private readonly PersistenceTrace persistenceTrace;

    private static void RecordLockWait(in TagList measurementTags, string lockName, string cause, long startTimestamp)
    {
        var duration = Stopwatch.GetElapsedTime(startTimestamp).TotalMilliseconds;
        if (LockWaitDurationMeter.Enabled)
        {
            var tags = measurementTags;
            tags.Add(LockMeterAttribute, lockName);
            tags.Add(CauseMeterAttribute, cause);
            LockWaitDurationMeter.Record(duration, tags);
        }

        WriteAheadLogEventSource.Log.LockWait(lockName, cause, duration);
    }

    private ValueTask AcquirePersistenceLockAsync(string cause, CancellationToken token)
        => IsLockTracingEnabled ? AcquirePersistenceLockTracedAsync(cause, token) : persistenceLock.AcquireAsync(token);

    private async ValueTask AcquirePersistenceLockTracedAsync(string cause, CancellationToken token)
    {
        var start = Stopwatch.GetTimestamp();
        await persistenceLock.AcquireAsync(token).ConfigureAwait(false);
        RecordLockWait(in measurementTags, PersistenceLockName, cause, start);
        persistenceTrace.OnLockAcquired(cause);
    }

    private void ReleasePersistenceLock()
    {
        persistenceTrace.OnLockReleasing();
        persistenceLock.Release();
    }

    internal const string PersistenceLockName = "persistence";

    // Tag values. The append cause covers every append path; snapshot is the installation of a snapshot;
    // flush is the background or manual flusher that persists the commit index.
    internal const string AppendCause = "append", SnapshotCause = "snapshot", FlushCause = "flush";
    internal const string ApplyCause = "apply", ReadCause = "read", CleanupCause = "cleanup", CommitCause = "commit";

    internal const string PagesPhase = "pages",
        DataDirectoryPhase = "data-directory",
        MetadataDirectoryPhase = "metadata-directory",
        CheckpointIntentPhase = "checkpoint-intent",
        CheckpointSlotPhase = "checkpoint-slot",
        CheckpointCommitPhase = "checkpoint-commit",
        CheckpointUpgradePhase = "checkpoint-upgrade";

    // Accessed under persistenceLock only.
    private sealed class PersistenceTrace(in TagList measurementTags)
    {
        private readonly TagList measurementTags = measurementTags;
        private long phaseMark, lockAcquiredAt;
        private string cause = string.Empty, lockCause = string.Empty;

        internal void Start(string cause)
        {
            this.cause = cause;
            phaseMark = IsPersistPhaseTracingEnabled ? Stopwatch.GetTimestamp() : 0L;
        }

        internal void Phase(string phase)
        {
            if (phaseMark is 0L)
                return;

            var now = Stopwatch.GetTimestamp();
            var duration = Stopwatch.GetElapsedTime(phaseMark, now).TotalMilliseconds;
            phaseMark = now;
            if (PersistPhaseDurationMeter.Enabled)
            {
                var tags = measurementTags;
                tags.Add(PhaseMeterAttribute, phase);
                tags.Add(CauseMeterAttribute, cause);
                PersistPhaseDurationMeter.Record(duration, tags);
            }

            WriteAheadLogEventSource.Log.PersistPhase(phase, cause, duration);
        }

        internal void Stop() => phaseMark = 0L;

        internal void OnLockAcquired(string cause)
        {
            lockCause = cause;
            lockAcquiredAt = Stopwatch.GetTimestamp();
        }

        internal void OnLockReleasing()
        {
            if (lockAcquiredAt is 0L)
                return;

            var duration = Stopwatch.GetElapsedTime(lockAcquiredAt).TotalMilliseconds;
            lockAcquiredAt = 0L;
            if (LockHoldDurationMeter.Enabled)
            {
                var tags = measurementTags;
                tags.Add(LockMeterAttribute, PersistenceLockName);
                tags.Add(CauseMeterAttribute, lockCause);
                LockHoldDurationMeter.Record(duration, tags);
            }

            WriteAheadLogEventSource.Log.LockHold(PersistenceLockName, lockCause, duration);
        }
    }
}

/// <summary>
/// Opt-in persistence diagnostics of <see cref="WriteAheadLog"/>.
/// </summary>
/// <remarks>
/// The events are written only when a session enables the source with <see cref="Keywords.Persistence"/>,
/// for example <c>dotnet-trace collect --providers DotNext-IO-WriteAheadLog:0x1:4</c>. The durations are in milliseconds.
/// The same measurements are available through the <c>DotNext.IO.WriteAheadLog</c> meter.
/// </remarks>
[EventSource(Name = SourceName)]
internal sealed class WriteAheadLogEventSource : EventSource
{
    internal const string SourceName = "DotNext-IO-WriteAheadLog";
    internal const int PersistPhaseEventId = 1, LockWaitEventId = 2, LockHoldEventId = 3;

    internal static readonly WriteAheadLogEventSource Log = new();

    private WriteAheadLogEventSource()
    {
    }

    internal bool IsPersistenceEnabled => IsEnabled(EventLevel.Informational, Keywords.Persistence);

    [Event(PersistPhaseEventId, Level = EventLevel.Informational, Keywords = Keywords.Persistence)]
    public void PersistPhase(string phase, string cause, double durationMs)
    {
        if (IsPersistenceEnabled)
            WriteEvent(PersistPhaseEventId, phase, cause, durationMs);
    }

    [Event(LockWaitEventId, Level = EventLevel.Informational, Keywords = Keywords.Persistence)]
    public void LockWait(string lockName, string cause, double durationMs)
    {
        if (IsPersistenceEnabled)
            WriteEvent(LockWaitEventId, lockName, cause, durationMs);
    }

    [Event(LockHoldEventId, Level = EventLevel.Informational, Keywords = Keywords.Persistence)]
    public void LockHold(string lockName, string cause, double durationMs)
    {
        if (IsPersistenceEnabled)
            WriteEvent(LockHoldEventId, lockName, cause, durationMs);
    }

    /// <summary>
    /// The event keywords.
    /// </summary>
    public static class Keywords
    {
        /// <summary>
        /// Persist cycle phases and lock wait and hold times.
        /// </summary>
        public const EventKeywords Persistence = (EventKeywords)0x1;
    }
}
