using System.Diagnostics;
using System.Diagnostics.Metrics;
using System.Threading.Channels;
using Microsoft.Extensions.Logging;

namespace DotNext.Net.Cluster.Consensus.Raft.ReplicationUtils;

using Diagnostics;
using IO;
using IO.Log;
using Metrics;
using Threading;

internal class ReplicationProcess : Disposable
{
    private protected static readonly Counter<int> LateResponsesMeter = Instrumentation.ServerSide.CreateCounter<int>(
        "post-quorum-responses", description: "Number of replication acknowledgments discarded because the leader had already reached majority consensus");

    public virtual void Replicate(ReplicationBarrier barrier)
        => barrier.SetResult(MemberResult.Replicated(barrier.Checkpoint));

    public virtual Task StopAsync(bool interrupt = false) => Task.CompletedTask;

    public virtual bool IsAvailable => true;
}

internal sealed class ReplicationProcess<TMember> : ReplicationProcess, ILogEntryConsumer<IRaftLogEntry, Result<ReplicationStatus>>
    where TMember : IRaftClusterMember
{
    private readonly TMember member;
    private readonly ChannelReader<ReplicationBarrier> reader;
    private readonly ChannelWriter<ReplicationBarrier> writer;
    private readonly CancellationTokenSource interruption;
    private readonly TagList measurementTags;
    private long replicationIndex, precedingTerm;

    // The index up to which the member confirmed, in the latest round, that its log matches the leader's log.
    // Unlike MemberResult, it doesn't distinguish replication of the leader's term, which is required for
    // commitment but not for catching up a new member.
    private long matchedIndex;

    // Set immediately before the request to the member. A failure before that point is a failure of the
    // leader's own log (or local preparation) and must not be attributed to the member.
    private bool memberCallStarted;
    private bool available = true;
    private IFailureDetector? detector;
    private ActivityTracker? activity;

    public ReplicationProcess(TMember member, int queueSize)
    {
        Debug.Assert(queueSize > 0);
        
        this.member = member;

        var channel = Channel.CreateBounded<ReplicationBarrier>(new BoundedChannelOptions(queueSize)
        {
            // DropWrite makes TryWrite report success (silently discarding the item) even when full;
            // Replicate() below relies on a false return to synchronously report the member as
            // unavailable for this round, which only Wait actually provides.
            FullMode = BoundedChannelFullMode.Wait,
            AllowSynchronousContinuations = false,
            SingleReader = true,
            SingleWriter = true,
        });

        reader = channel.Reader;
        writer = channel.Writer;
        interruption = new();
    }
    
    public required IPersistentState AuditTrail { get; init; }

    public TagList MeasurementTags
    {
        init
        {
            value.Add(IRaftClusterMember.RemoteAddressMeterAttributeName, member.EndPoint.ToString());
            measurementTags = value;
        }
    }

    public override bool IsAvailable => Volatile.Read(ref available);
    
    public required long Term { get; init; }

    public required ILogger Logger { get; init; }
    
    public IFailureDetector? FailureDetector
    {
        init => detector = value;
    }

    // a queued round is counted until its result is reported to the barrier
    public ActivityTracker? Activity
    {
        init => activity = value;
    }

    public override void Replicate(ReplicationBarrier barrier)
    {
        activity?.Enter();

        // If member is too slow and cannot process the queue, we assume that it's temporary unavailable
        if (!writer.TryWrite(barrier))
        {
            barrier.SetResult(MemberResult.Unavailable);
            Logger.SlowMember(member.EndPoint);
            activity?.Exit();
        }
    }

    public void Start(CancellationToken token) => _ = ReplicateAsync(token);

    public override Task StopAsync(bool interrupt = false)
    {
        if (interrupt)
            interruption.Cancel(throwOnFirstException: false);

        writer.Complete();
        return reader.Completion;
    }

    private async Task ReplicateAsync(CancellationToken token)
    {
        // There are two cancellation tokens:
        // First one represents leadership token maintained by the Leader state;
        // Second one is a token that can be used to abort any communication with the member. This is useful
        // when the member needs to be removed due to membership changes.
        using var source = CancellationToken.Combine(token, interruption.Token);
        
        // Do not pass the token to WaitToReadAsync(), because we want to read all the signals from the channel
        // even in case of cancellation
        while (await reader.WaitToReadAsync(CancellationToken.None).ConfigureAwait(false))
        {
            for (MemberResult? result; reader.TryRead(out var barrier); SetResult(barrier, in result), activity?.Exit())
            {
                replicationIndex = member.State.PrecedingIndex;
                matchedIndex = -1L;
                memberCallStarted = false;
                try
                {
                    precedingTerm = await AuditTrail.GetTermAsync(replicationIndex, source.Token).ConfigureAwait(false);
                    var response = available
                        ? await ReplicateAsync(replicationIndex + 1L, barrier.Checkpoint, source.Token).ConfigureAwait(false)
                        : throw new MemberUnavailableException(member);

                    detector?.ReportHeartbeat();
                    result = ConvertToResult(in response);
                }
                catch (MemberUnavailableException)
                {
                    result = MemberResult.Unavailable;
                }
                catch (OperationCanceledException e) when (e.CausedBy(source, token))
                {
                    result = MemberResult.Canceled;
                    detector = null; // disable failure detection
                    // continue loop to drain the channel
                }
                catch (OperationCanceledException e) when (e.CausedBy(source, interruption.Token))
                {
                    // the process has been interrupted, report this member as unavailable and disable failure detection
                    result = MemberResult.Unavailable;
                    detector = null;
                }
                catch (Exception e) when (!memberCallStarted)
                {
                    // The leader could not read its own log for this member. The member was not contacted,
                    // so the failure detector is neither fed nor queried for this round. The round still
                    // counts as unavailable, so the leader steps down if it cannot replicate to a majority.
                    Logger.LocalLogReadFailed(member.EndPoint, e);
                    result = MemberResult.Unavailable;
                    continue;
                }
                catch (Exception e)
                {
                    Logger.LogError(e, ExceptionMessages.UnexpectedError);
                    result = MemberResult.Unavailable;
                }

                CheckHealthStatus();
            }
        }
    }

    private ValueTask<Result<ReplicationStatus>> ReplicateAsync(long startIndex, long endIndex, CancellationToken token)
    {
        Logger.ReplicationStarted(member.EndPoint, startIndex, endIndex);
        return AuditTrail
            .ReadAsync(this, startIndex, endIndex, token);
    }

    private void SetResult(ReplicationBarrier barrier, in MemberResult? result)
    {
        if (!barrier.SetResult(in result))
            LateResponsesMeter.Add(1, measurementTags);
    }

    private void CheckHealthStatus()
    {
        switch (detector)
        {
            case { IsMonitoring: false }:
                Logger.UnknownHealthStatus(member.EndPoint);
                break;
            case { IsHealthy: false }:
                Volatile.Write(ref available, false);
                detector = null; // disable failure detection
                break;
        }
    }

    private MemberResult? ConvertToResult(in Result<ReplicationStatus> result)
    {
        var status = result.Value;
        switch (status.Result)
        {
            case HeartbeatResult.ReplicatedWithLeaderTerm:
                OnReplicated();
                return MemberResult.Replicated(replicationIndex);
            // An accepted empty heartbeat whose preceding entry has the leader's term proves, by the Log Matching
            // property, that the member stores the leader's log up to that entry. It counts toward commitment,
            // so a round with nothing new doesn't wait for a slow member to complete the commit majority.
            case HeartbeatResult.Replicated when precedingTerm == Term && replicationIndex == member.State.PrecedingIndex:
                OnReplicated();
                return MemberResult.Replicated(replicationIndex);
            case HeartbeatResult.Replicated:
                OnReplicated();
                return MemberResult.Touched;
            case HeartbeatResult.Rejected when result.Term > Term:
                return MemberResult.HigherTermDetected(result.Term);
            case HeartbeatResult.UnsupportedVersion:
                // Do not decrement NextIndex
                Logger.UnsupportedVersion(member.EndPoint);
                return MemberResult.Touched;
            default:
                ref var currentState = ref member.State;
                Logger.ReplicationFailed(member.EndPoint,
                    currentState.NextIndex = long.Min(currentState.PrecedingIndex, status.LastIndex + 1L));
                return MemberResult.Touched;
        }
    }

    private void OnReplicated()
    {
        Logger.ReplicationSuccessful(member.EndPoint, member.State.NextIndex);
        member.State.NextIndex = replicationIndex + 1L;
        matchedIndex = replicationIndex;
    }
    
    ValueTask<Result<ReplicationStatus>> ILogEntryConsumer<IRaftLogEntry, Result<ReplicationStatus>>.
        ReadAsync<TEntryImpl, TList>(TList entries, long? snapshotIndex, CancellationToken token)
        => new(snapshotIndex.HasValue
            ? ReplicateSnapshotAsync(entries[0], snapshotIndex.GetValueOrDefault(), token)
            : ReplicateEntriesAsync<TEntryImpl, TList>(entries, token));

    private Task<Result<ReplicationStatus>> ReplicateEntriesAsync<TEntry, TList>(TList entries, CancellationToken token)
        where TEntry : IRaftLogEntry
        where TList : IReadOnlyList<TEntry>
    {
        Logger.ReplicaSize(member.EndPoint, entries.Count, replicationIndex, precedingTerm);
        memberCallStarted = true;
        var result = member.AppendEntriesAsync<TEntry, TList>(Term, entries, replicationIndex, precedingTerm,
            AuditTrail.LastCommittedEntryIndex, token);
        replicationIndex += entries.Count;
        return result;
    }

    private async Task<Result<ReplicationStatus>> ReplicateSnapshotAsync<TSnapshot>(TSnapshot snapshot,
        long snapshotIndex, CancellationToken token)
        where TSnapshot : IRaftLogEntry
    {
        Debug.Assert(snapshot.IsSnapshot);

        Logger.InstallingSnapshot(member.EndPoint, replicationIndex = snapshotIndex);

        var (config, configVersion) = await LoadConfigurationAsync(token).ConfigureAwait(false);
        memberCallStarted = true;
        var result = await member.InstallSnapshotAsync(Term, snapshot, snapshotIndex, config, configVersion, token)
            .ConfigureAwait(false);

        return new()
        {
            Term = result.Term,
            Value = new()
            {
                LastIndex = snapshotIndex,
                Result = result.Value,
            }
        };
    }

    private ValueTask<(IDataTransferObject, long)> LoadConfigurationAsync(CancellationToken token)
        => AuditTrail.ConfigurationStorage?.LoadConfigurationAsync(token) ??
           ValueTask.FromResult((IDataTransferObject.Empty, 0L));

    public async ValueTask<bool> CatchUpAsync(int rounds, CancellationToken token)
    {
        var watermarkIndex = AuditTrail.LastCommittedEntryIndex;
        Start(token);

        for (var barrier = new ReplicationBarrier(); rounds > 0; rounds--, barrier.Reuse())
        {
            var result = await ReplicateSingleAsync(barrier).ConfigureAwait(false);

            switch (barrier[0])
            {
                case { IsCanceled: true }:
                    throw new OperationCanceledException(token);
                case { Term: not null }:
                    rounds = 0;
                    break;
                // A matching-prefix acknowledgment up to the watermark is enough to catch up, even if it carries
                // no entry of the leader's term (e.g. an empty heartbeat) and is therefore reported as Touched.
                // A rejection is never enough, even if the watermark is 0 (#52).
                // The barrier completion publishes matchedIndex written by the replication loop.
                case var _ when result.HasConsensus && matchedIndex >= watermarkIndex:
                    writer.Complete();
                    return true;
            }
        }

        writer.Complete();
        return false;
    }

    private ValueTask<ReplicationResult> ReplicateSingleAsync(ReplicationBarrier barrier)
    {
        var task = barrier.WaitAsync(memberCount: 1, AuditTrail.LastEntryIndex);
        Replicate(barrier);
        return task;
    }

    protected override void Dispose(bool disposing)
    {
        if (disposing)
        {
            interruption.Dispose();
            writer.TryComplete(new ObjectDisposedException(GetType().Name));
        }
        
        base.Dispose(disposing);
    }
}