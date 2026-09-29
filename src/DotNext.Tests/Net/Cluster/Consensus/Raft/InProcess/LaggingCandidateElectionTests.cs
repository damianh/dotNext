using System.Collections.Concurrent;
using System.Runtime.CompilerServices;

namespace DotNext.Net.Cluster.Consensus.Raft.InProcess;

using static MembershipClusterFixture;

/// <summary>
/// #49 "CE-2, lagging candidate": a voter that applied the committed membership changes but did not activate
/// them still counts votes over its old member set, so it can win a term that a node on the new set also wins.
/// </summary>
public sealed class LaggingCandidateElectionTests : RaftTest
{
    private const string Issue = "Reproduces #49 CE-2 (two leaders in one term): https://github.com/damianh/dotNext/issues/49";

    // voters {0,1,2}, joiners 3 and 4
    [Fact(Timeout = TestTimeouts.Default, Skip = Issue)]
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

        var lastIndex = n0.Log.LastEntryIndex;
        Equal(3L, lastIndex); // no-op, +3, +4
        Equal(lastIndex, n1.Log.LastEntryIndex);
        Equal(lastIndex, n2.Log.LastEntryIndex);
        True(n1.Log.LastCommittedEntryIndex >= 2L);
        True(n2.Log.LastCommittedEntryIndex >= 2L);

        // node 0 loses its quorum and steps down, then activates {0..4}; nodes 1 and 2 activate nothing
        await cluster.PumpAsync(n0, WaitForFollowerAsync(n0), static _ => MessageAction.Drop);
        await n0.PropagateConfigurationAsync();
        Equal(Endpoints(cluster, 0, 1, 2, 3, 4), n0.Members.Select(static member => member.EndPoint).ToHashSet());
        Equal(Endpoints(cluster, 0, 1, 2), n1.Members.Select(static member => member.EndPoint).ToHashSet());
        Equal(Endpoints(cluster, 0, 1, 2), n2.Members.Select(static member => member.EndPoint).ToHashSet());
        DoesNotContain(claims, static claim => claim.Term > 1L);

        // term 2, partition {0,3,4} | {1,2}: only pre-votes and votes are delivered
        var votes = new ConcurrentDictionary<long, PendingMessage>();
        var first = await TryElectAsync(cluster, n0, target => target == n1.Id || target == n2.Id, votes);
        var second = await TryElectAsync(cluster, n1, target => target == n0.Id, votes);

        var trace = Describe(cluster, votes);
        Equal(2L, n0.Term);
        Equal(2L, n1.Term);
        True(first, $"node 0 did not win term 2 with {{0,3,4}}:{Environment.NewLine}{trace}");
        True(second, $"node 1 did not win term 2 with {{1,2}}:{Environment.NewLine}{trace}");

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
