using System.Diagnostics.Metrics;
using System.Runtime.CompilerServices;

namespace DotNext.Net.Cluster.Consensus.Raft;

using Runtime.CompilerServices;

internal sealed class CandidateState<TMember> : RaftState<TMember>
    where TMember : class, IRaftClusterMember
{
    private readonly CancellationTokenSource votingCancellation;
    private readonly CancellationToken votingCancellationToken; // cached to prevent ObjectDisposedException
    private Task? votingTask;

    public CandidateState(IRaftStateMachine<TMember> stateMachine)
        : base(stateMachine)
    {
        votingCancellation = new();
        votingCancellationToken = votingCancellation.Token;
    }

    internal required long Term
    {
        get;
        init;
    }

    [AsyncMethodBuilder(typeof(SpawningAsyncTaskMethodBuilder))]
    private async Task VoteAsync(TimeSpan timeout)
    {
        var requests = Activity is { } activity ? new ActivityTracker.Requests(activity, votingCancellationToken) : null;
        var voters = Array.Empty<Task<(TMember, long, bool?)>>();
        try
        {
            // Perf: reuse index and related term once for all members
            var lastIndex = AuditTrail.LastEntryIndex;
            var lastTerm = await AuditTrail.GetTermAsync(lastIndex, votingCancellationToken).ConfigureAwait(false);

            // start voting in parallel
            var members = Members.ToArray();
            voters = StartVoting(members, lastIndex, lastTerm, requests);
            var deadline = new VotingDeadline(votingCancellation, timeout, TimeProvider);
            await using (deadline.ConfigureAwait(false))
                await EndVoting(Task.WhenEach(voters), new(members.Length), deadline, requests).ConfigureAwait(false);
        }
        catch (Exception e) when (!IsDisposingOrDisposed)
        {
            // Supervise the voting task like the leader heartbeat: report the failure and resume
            // the election timer instead of remaining a candidate with no voting in progress.
            Logger.VotingFailed(Term, e);
            MoveToFollowerState(randomizeTimeout: true);
        }
        finally
        {
            requests?.Abandon();

            // the outstanding requests are canceled with the candidate state
            VotingState.IgnoreRemaining(voters);

            // the transition, if any, is already counted
            Activity?.Exit();
        }
    }
    
    private Task<(TMember, long, bool?)>[] StartVoting(TMember[] members, long lastIndex, long lastTerm, ActivityTracker.Requests? requests)
        => members
            .TakeWhile(NotCanceled)
            .Select(member => ActivityTracker.Requests.Start(
                requests,
                static args => args.Item1.VoteAsync(args.member, args.lastIndex, args.lastTerm),
                (this, member, lastIndex, lastTerm)))
            .ToArray();

    private bool NotCanceled(TMember _) => !votingCancellation.IsCancellationRequested;

    [AsyncMethodBuilder(typeof(SpawningAsyncTaskMethodBuilder<>))]
    private async Task<(TMember, long, bool?)> VoteAsync(TMember voter, long lastIndex, long lastTerm)
    {
        bool? result;
        long currentTerm;
        try
        {
            var response = await voter.VoteAsync(Term, lastIndex, lastTerm, votingCancellationToken).ConfigureAwait(false);
            currentTerm = response.Term;
            result = response.Value;
        }
        catch (MemberUnavailableException)
        {
            result = null;
            currentTerm = -1L;
        }

        return (voter, currentTerm, result);
    }

    // The votes are counted over the members the voting started with, the configuration of the term.
    private async Task EndVoting(IAsyncEnumerable<Task<(TMember, long, bool?)>> voters, VotingState votes, VotingDeadline? deadline, ActivityTracker.Requests? requests)
    {
        var localMember = default(TMember);

        var enumerator = voters.GetAsyncEnumerator(votingCancellationToken);
        try
        {
            while (await ActivityTracker.Requests.WaitAsync(requests, enumerator.MoveNextAsync()).ConfigureAwait(false))
            {
                requests?.Observe(enumerator.Current);
                var (member, term, result) = await enumerator.Current.ConfigureAwait(false);

                if (IsDisposingOrDisposed)
                    return;

                // current node is outdated
                if (term > Term)
                {
                    MoveToFollowerState(randomizeTimeout: false, term);
                    return;
                }

                switch (result)
                {
                    case true:
                        Logger.VoteGranted(member.EndPoint);
                        votes.Grant();
                        break;
                    case false:
                        Logger.VoteRejected(member.EndPoint);
                        votes.Deny();
                        break;
                    default:
                        Logger.MemberUnavailable(member.EndPoint);
                        votes.Deny();
                        break;
                }

                if (!member.IsRemote)
                    localMember = member;

                // #146: the remaining responses cannot change the outcome, don't wait for a silent member
                if (votes.IsLost || (votes.IsWon && localMember is not null))
                    break;
            }
        }
        catch (OperationCanceledException)
        {
            // candidate timeout happened without a decisive result (same failure family as a
            // split vote below), randomize to avoid retrying in lockstep with other candidates
            MoveToFollowerState(randomizeTimeout: true);
            return;
        }
        finally
        {
            await enumerator.DisposeAsync().ConfigureAwait(false);
        }

        Logger.VotingCompleted(votes.Weight, Term);
        // #146: don't reset the source, its registrations cancel the outstanding requests with the candidate state
        if (deadline?.TryStop() is false || votingCancellationToken.IsCancellationRequested || !votes.IsWon || localMember is null)
        {
            MoveToFollowerState(randomizeTimeout: true); // no clear consensus
        }
        else
        {
            // becomes a leader
            MoveToLeaderState(
                localMember,
                await AuditTrail.AppendAsync(new EmptyLogEntry { Term = Term }, votingCancellationToken).ConfigureAwait(false));
        }
    }

    private sealed class VotingDeadline : IAsyncDisposable
    {
        private readonly CancellationTokenSource cancellationSource;
        private readonly ITimer timer;
        private int active = 1;

        internal VotingDeadline(CancellationTokenSource cancellationSource, TimeSpan timeout, TimeProvider timeProvider)
        {
            this.cancellationSource = cancellationSource;
            timer = timeProvider.CreateTimer(
                static state => ((VotingDeadline)state!).Cancel(),
                this,
                timeout,
                Timeout.InfiniteTimeSpan);

            if (timeout == TimeSpan.Zero)
                Cancel();
        }

        private void Cancel()
        {
            if (Interlocked.Exchange(ref active, 0) is 1)
                cancellationSource.Cancel(throwOnFirstException: false);
        }

        internal bool TryStop()
        {
            var result = Interlocked.Exchange(ref active, 0) is 1;
            timer.Change(Timeout.InfiniteTimeSpan, Timeout.InfiniteTimeSpan);
            return result && !cancellationSource.IsCancellationRequested;
        }

        public ValueTask DisposeAsync()
        {
            Interlocked.Exchange(ref active, 0);
            return timer.DisposeAsync();
        }
    }

    /// <summary>
    /// Starts voting asynchronously.
    /// </summary>
    /// <param name="timeout">Candidate state timeout.</param>
    internal void StartVoting(TimeSpan timeout)
    {
        CandidateState.TransitionRateMeter.Add(1, in MeasurementTags);
        Logger.VotingStarted(timeout, Term);
        Activity?.Enter();
        votingTask = VoteAsync(timeout);
    }

    protected override async ValueTask DisposeAsyncCore()
    {
        try
        {
            votingCancellation.Cancel(throwOnFirstException: false);
            await (votingTask ?? Task.CompletedTask).ConfigureAwait(false);
        }
        catch (Exception e)
        {
            Logger.CandidateStateExitedWithError(e);
        }
        finally
        {
            Dispose(disposing: true);
        }
    }

    protected override void Dispose(bool disposing)
    {
        if (disposing)
        {
            votingCancellation.Dispose();
            votingTask = null;
        }

        base.Dispose(disposing);
    }
}

file static class CandidateState
{
    internal static readonly Counter<int> TransitionRateMeter = Metrics.Instrumentation.ServerSide.CreateCounter<int>("transitions-to-candidate-count", description: "Number of Transitions to Candidate State");
}