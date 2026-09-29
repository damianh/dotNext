using Microsoft.AspNetCore.Connections;

namespace DotNext.Net.Cluster.Consensus.Raft.Http;

using Membership;

internal partial class RaftHttpCluster
{
    private readonly ClusterMemberAnnouncer<UriEndPoint>? announcer;

    private async Task<bool> AddMemberAsync(UriEndPoint address, CancellationToken token)
    {
        using var member = CreateMember(address);
        return await AddMemberAsync(member, warmupRounds, ConfigurationStorage, GetAddress, token).ConfigureAwait(false);
    }

    private static UriEndPoint GetAddress(RaftClusterMember member) => member.EndPoint;

    Task<bool> IRaftHttpCluster.AddMemberAsync(Uri address, CancellationToken token)
        => AddMemberAsync(new(address), token);

    Task<bool> IRaftHttpCluster.RemoveMemberAsync(Uri address, CancellationToken token)
        => RemoveMemberAsync(ClusterMemberId.FromEndPoint(new UriEndPoint(address)), ConfigurationStorage, GetAddress, token);
}