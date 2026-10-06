using System.Collections.Concurrent;
using System.Runtime.CompilerServices;

namespace DotNext.Net.Cluster.Consensus.Raft.InProcess;

using static MembershipClusterFixture;

/// <summary>
/// #49 "CE-2, lagging candidate": a voter that holds the committed membership changes must count votes over
/// the new member set, otherwise it can win a term that a node on the new set also wins. The configuration
/// is active as soon as it is appended, so every node that holds the changes counts over {0..4}.
/// </summary>
public sealed class LaggingCandidateElectionTests : RaftTest
{
    // voters {0,1,2}, joiners 3 and 4
    [Fact(Timeout = TestTimeouts.Default)]
    public static async Task StaleActiveMembersCannotWinTheTermOfANodeOnTheNewMembers()
    {
        await using var cluster = new MembershipClusterFixture(voterCount: 3, joinerCount: 2);
        await cluster.StartAsync();
        var (n0, n1, n2, n3, n4) = (cluster.Nodes[0], cluster.Nodes[1], cluster.Nodes[2], cluster.Nodes[3], cluster.Nodes[4]);

        // every node's own leadership claim, independent of any node's view of the others
        var claims = new ConcurrentQueue<(int Node, long Term)>();
        foreach (var (node, index) in cluster.Nodes.Select(static (node, index) => (node, index)))
        {
            var captured = index;
            node.LeaderChanged += (sender, leader) =>
            {
                if (leader is not null && leader.Id == ((MembershipNode)sender).Id)
                    claims.Enqueue((captured, sender.Term));
            };
        }

        // term 1: node 0 adds nodes 3 and 4, each change committed by {0,1,2}
        await cluster.ElectAsync(n0);
        Equal(1L, n0.Term);
        foreach (var joiner in new[] { n3, n4 })
        {
            var addition = n0.AddAsync(joiner.EndPoint, TestToken);
            await cluster.PumpAsync(n0, addition);
            True(await addition);
        }

        // the addition of node 4 can commit on {0,3,4} or {0,2,3} before node 1 has its entry
        await cluster.ReplicateToAllVotersAsync(n0);

        var lastIndex = n0.Log.LastEntryIndex;
        Equal(3L, lastIndex); // no-op, +3, +4
        Equal(lastIndex, n1.Log.LastEntryIndex);
        Equal(lastIndex, n2.Log.LastEntryIndex);
        True(n1.Log.LastCommittedEntryIndex >= 2L);
        True(n2.Log.LastCommittedEntryIndex >= 2L);

        // the changes are active on every node that holds them
        foreach (var node in new[] { n0, n1, n2 })
            Equal(Endpoints(cluster, 0, 1, 2, 3, 4), node.Members.Select(static member => member.EndPoint).ToHashSet());

        // node 0 loses its quorum and steps down
        await cluster.PumpAsync(n0, WaitForFollowerAsync(n0), static _ => MessageAction.Drop);
        DoesNotContain(claims, static claim => claim.Term > 1L);

        // term 2, partition {0,3,4} | {1,2}: only pre-votes and votes are delivered
        var votes = new ConcurrentDictionary<long, PendingMessage>();
        var first = await TryElectAsync(cluster, n0, target => target == n1.Id || target == n2.Id, votes);
        var second = await TryWinAsync(cluster, n1, target => target == n0.Id, votes);

        var trace = Describe(cluster, votes);
        Equal(2L, n0.Term);
        True(first, $"node 0 did not win term 2 with {{0,3,4}}:{Environment.NewLine}{trace}");
        False(second, $"node 1 won term {n1.Term} with {{1,2}}:{Environment.NewLine}{trace}");

        var duplicates = claims.GroupBy(static claim => claim.Term)
            .Where(static group => group.Select(static claim => claim.Node).Distinct().Count() > 1)
            .Select(static group => $"term {group.Key}: nodes {string.Join(",", group.Select(static claim => claim.Node))}")
            .ToArray();
        True(duplicates.Length is 0, $"Two nodes claimed leadership of the same term: {string.Join("; ", duplicates)}{Environment.NewLine}{trace}");
    }

    // The candidate's pre-votes and votes to members matching dropTarget are lost, the rest is delivered.
    private static async Task<bool> TryElectAsync(MembershipClusterFixture cluster, MembershipNode candidate,
        Func<ClusterMemberId, bool> dropTarget, ConcurrentDictionary<long, PendingMessage> votes)
    {
        try
        {
            await cluster.ElectAsync(candidate, passWriteBarrier: false, message =>
            {
                votes.TryAdd(message.Id, message);
                return dropTarget(message.TargetId) ? MessageAction.Drop : MessageAction.Deliver;
            });
            return true;
        }
        catch (TimeoutException)
        {
            return false;
        }
    }

    // Starts an election of the candidate and delivers its pre-votes and votes (those to members matching dropTarget
    // are lost) until it wins, or no new request arrives for a while after the last response.
    private static async Task<bool> TryWinAsync(MembershipClusterFixture cluster, MembershipNode candidate,
        Func<ClusterMemberId, bool> dropTarget, ConcurrentDictionary<long, PendingMessage> votes)
    {
        var quietPeriod = TimeSpan.FromMilliseconds(500);
        var elected = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var candidateId = candidate.Id;
        candidate.LeaderChanged += OnLeaderChanged;
        try
        {
            candidate.StartElectionTimer();
            cluster.TimeProvider.Advance(TimeSpan.FromMilliseconds(100));
            while (!elected.Task.IsCompleted)
            {
                using var quiet = CancellationTokenSource.CreateLinkedTokenSource(TestToken);
                quiet.CancelAfter(quietPeriod);
                PendingMessage message;
                try
                {
                    message = await cluster.Network.WaitForMessageAsync(
                        m => m.SourceId == candidateId && m.MessageType is RaftMessageType.PreVote or RaftMessageType.Vote,
                        quiet.Token);
                }
                catch (OperationCanceledException) when (!TestToken.IsCancellationRequested)
                {
                    break;
                }

                votes.TryAdd(message.Id, message);
                if (dropTarget(message.TargetId))
                    cluster.Network.TryDrop(message);
                else
                    await cluster.Network.TryDeliverAsync(message);
            }
        }
        finally
        {
            candidate.LeaderChanged -= OnLeaderChanged;
        }

        return elected.Task.IsCompleted;

        void OnLeaderChanged(RaftCluster<InProcessClusterMember> sender, InProcessClusterMember leader)
        {
            if (leader is not null && leader.Id == candidateId)
                elected.TrySetResult();
        }
    }

    private static string Describe(MembershipClusterFixture cluster, ConcurrentDictionary<long, PendingMessage> votes)
    {
        return string.Join(Environment.NewLine, votes.Values.OrderBy(static message => message.Id).Select(message =>
        {
            var outcome = message.Completion switch
            {
                Task<Result<bool>> { IsCompletedSuccessfully: true } vote => $"granted={vote.Result.Value}, responder term={vote.Result.Term}",
                Task<Result<PreVoteResult>> { IsCompletedSuccessfully: true } preVote => $"{preVote.Result.Value}, responder term={preVote.Result.Term}",
                { IsFaulted: true } => "lost",
                _ => "pending",
            };

            return $"{message.MessageType} {Name(message.SourceId)} -> {Name(message.TargetId)}: {outcome}";
        }));

        string Name(ClusterMemberId id) => $"n{Array.FindIndex(cluster.Nodes, node => node.Id == id)}";
    }

    private static HashSet<System.Net.EndPoint> Endpoints(MembershipClusterFixture cluster, params int[] nodes)
        => nodes.Select(i => cluster.Nodes[i].EndPoint).ToHashSet();

    private static async Task WaitForFollowerAsync(MembershipNode node)
    {
        while (Accessors<InProcessClusterMember>.State(node) is not FollowerState<InProcessClusterMember>)
            await Task.Delay(1, TestToken);
    }

    private static class Accessors<TMember>
        where TMember : class, IRaftClusterMember, IDisposable
    {
        [UnsafeAccessor(UnsafeAccessorKind.Field, Name = "state")]
        internal static extern ref RaftState<TMember> State(RaftCluster<TMember> cluster);
    }
}
