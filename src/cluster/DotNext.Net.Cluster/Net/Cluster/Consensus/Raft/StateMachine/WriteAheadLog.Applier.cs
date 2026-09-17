using System.Diagnostics;
using System.Diagnostics.CodeAnalysis;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;

namespace DotNext.Net.Cluster.Consensus.Raft.StateMachine;

using Diagnostics;
using Runtime.CompilerServices;
using Threading;

partial class WriteAheadLog
{
    // Diagnostics only, see QueuedSynchronizer.TrackSuspendedCallers.
    internal const string ApplierCallerInfo = "Apply Entries";

    private readonly AsyncAutoResetEventSlim applyTrigger;
    private readonly Task appenderTask;
    
    [SuppressMessage("Usage", "CA2213", Justification = "False positive")]
    private readonly AsyncTrigger appliedEvent;
    private long appliedIndex; // volatile, only applier can modify the field

    [AsyncMethodBuilder(typeof(SpawningAsyncTaskMethodBuilder))]
    private async Task ApplyAsync(CancellationToken token)
    {
        for (; !token.IsCancellationRequested && backgroundTaskFailure is null; await applyTrigger.WaitAsync().ConfigureAwait(false))
        {
            // Ensure that the appender is not running with the snapshot installation process concurrently
            lockManager.SetCallerInformation(ApplierCallerInfo);
            await lockManager.AcquireReadLockAsync(token).ConfigureAwait(false);
            try
            {
                // The target and the published progress must both be observed under the lock. Otherwise, a
                // snapshot installation completing in between can be rolled back by a stale target, see #15.
                var newIndex = LastCommittedEntryIndex;
                var oldSnapshot = SnapshotIndex;

                // Never walk back over the metadata reclaimed by an installation.
                await ApplyAsync(long.Max(LastAppliedIndex, SnapshotIndex) + 1L, newIndex, token).ConfigureAwait(false);
                LastAppliedIndex = newIndex;
                if (SnapshotIndex > oldSnapshot)
                    flushTrigger?.Set();
            }
            catch (Exception e) when (e is not OperationCanceledException canceledEx || canceledEx.CancellationToken != token)
            {
                OnBackgroundTaskFailure(e);
                break;
            }
            finally
            {
                lockManager.ReleaseReadLock();
            }
        }
    }

    /// <summary>
    /// Gets the index of the last log entry applied to the underlying state machine.
    /// </summary>
    public long LastAppliedIndex
    {
        get => Atomic.Read(in appliedIndex);
        
        private set
        {
            // Applied progress is monotonic: the applier and a snapshot installation can both publish it.
            if (value > Atomic.Read(in appliedIndex))
                Atomic.Write(ref appliedIndex, value);

            appliedEvent.Signal(resumeAll: true);
        }
    }

    // returns snapshot index
    private async ValueTask ApplyAsync(long fromIndex, long toIndex, CancellationToken token)
    {
        var ts = new Timestamp();
        for (long index = fromIndex, appliedIndex; index <= toIndex; index = long.Max(appliedIndex + 1L, index + 1L))
        {
            if (metadataPages.GetView<MetadataReader>(index) is { Metadata: { HasPayload: true } metadata })
            {
                var entry = new LogEntry(metadata, index, dataPages)
                {
                    Context = context.Remove(index, out var ctx) ? ctx : null,
                };

                // If configuration storage is provided, pass the entry to it.
                // Otherwise, pass it to the state machine.
                if (metadata.IsConfiguration && ConfigurationStorage is { } storage)
                {
                    await storage.SaveConfigurationAsync(entry, index, token).ConfigureAwait(false);
                    entry = new(metadata.Term, index);
                }

                appliedIndex = await stateMachine.ApplyAsync(entry, token).ConfigureAwait(false);
            }
            else
            {
                appliedIndex = index;
            }

            Debug.Assert(appliedIndex <= toIndex);
            
            ApplyRateMeter.Add(1L, measurementTags);
        }

        ApplyDurationMeter.Record(ts.ElapsedMilliseconds);
    }
    
    [StructLayout(LayoutKind.Auto)]
    private readonly struct CommitChecker(WriteAheadLog log, long index) : ISupplier<bool>
    {
        bool ISupplier<bool>.Invoke() => index <= log.LastAppliedIndex;
    }
}