using System.Net;

namespace DotNext.Net.Cluster.Consensus.Raft.InProcess;

internal sealed class InProcessClusterFixture : Test, IAsyncDisposable
{
    internal readonly ManualTimeProvider TimeProvider = new();
    internal readonly InProcessNetwork Network = new();
    internal readonly IPersistentState[] States;
    internal readonly InProcessCluster[] Nodes;

    internal InProcessClusterFixture(int memberCount, Func<int, IPersistentState> stateFactory = null)
    {
        EndPoint[] membership = Enumerable.Range(0, memberCount)
            .Select(i => new DnsEndPoint($"node-{i}", 0)).ToArray();
        States = Enumerable.Range(0, memberCount)
            .Select(i => stateFactory?.Invoke(i) ?? new ConsensusOnlyState())
            .ToArray();
        Nodes = States.Select((state, i) => new InProcessCluster(
            Network, ((DnsEndPoint)membership[i]).Host, membership, state,
            TimeProvider, TimeSpan.FromMilliseconds(100), startFollower: false)).ToArray();
    }

    internal InProcessCluster Leader => Nodes[0];

    internal async Task StartAsync()
    {
        foreach (var node in Nodes)
            await node.StartAsync(TestToken);
    }

    internal async Task ElectAsync()
    {
        Leader.StartElectionTimer();
        TimeProvider.Advance(TimeSpan.FromMilliseconds(100));
        for (var i = 1; i < Nodes.Length; i++)
            await Network.DeliverAsync(await PendingAsync(i, RaftMessageType.PreVote));
        for (var i = 1; i < Nodes.Length; i++)
            await Network.DeliverAsync(await PendingAsync(i, RaftMessageType.Vote));
        await Leader.WaitForLeaderAsync(TimeSpan.FromSeconds(5), TestToken);
    }

    internal void HoldFollowers()
    {
        foreach (var node in Nodes.Skip(1))
            Network.Hold(Leader.EndPoint, node.EndPoint);
    }

    internal async Task StartLeaderAsync()
    {
        await StartAsync();
        HoldFollowers();
        await ElectAsync();

        // Observe the automatic round before forcing its retry, and account for
        // every worker's setup RPC before starting the round under test.
        var initial = await PendingRoundAsync();
        var retry = Leader.ForceReplicationAsync(TestToken).AsTask();
        foreach (var message in initial)
        {
            await Network.DeliverAsync(message);
            var response = await IsType<Task<Result<ReplicationStatus>>>(message.Completion);
            Equal(HeartbeatResult.Rejected, response.Value.Result);
        }

        foreach (var message in await PendingRoundAsync())
            await Network.DeliverAsync(message);
        await retry;
        await Leader.WaitForLeadershipAsync(TestToken);
        Equal(1L, States[0].LastCommittedEntryIndex);
    }

    internal Task<PendingMessage> PendingAsync(int member, RaftMessageType type)
        => Network.WaitForMessageAsync(Leader.EndPoint, Nodes[member].EndPoint, type, TestToken);

    internal async Task<PendingMessage[]> PendingRoundAsync(RaftMessageType lastType = RaftMessageType.AppendEntries)
    {
        var messages = new PendingMessage[Nodes.Length - 1];
        for (var i = 1; i < Nodes.Length; i++)
            messages[i - 1] = await PendingAsync(i, i == Nodes.Length - 1 ? lastType : RaftMessageType.AppendEntries);
        return messages;
    }

    internal async Task DeliverRoundAsync(int drop = -1, RaftMessageType lastType = RaftMessageType.AppendEntries)
    {
        var round = Leader.ForceReplicationAsync(TestToken).AsTask();
        var messages = await PendingRoundAsync(lastType);
        for (var i = 0; i < messages.Length; i++)
        {
            if (i + 1 == drop)
                Network.Drop(messages[i]);
            else
                await Network.DeliverAsync(messages[i]);
        }
        await round;
    }

    public async ValueTask DisposeAsync()
    {
        try
        {
            // A lifecycle regression must not hang the rest of the test process.
            await Task.WhenAll(Nodes.Select(node => node.DisposeAsync().AsTask()))
                .WaitAsync(DefaultTimeout, TestToken);
        }
        finally
        {
            foreach (var state in States)
            {
                switch (state)
                {
                    case IAsyncDisposable asyncDisposable:
                        await asyncDisposable.DisposeAsync();
                        break;
                    case IDisposable disposable:
                        disposable.Dispose();
                        break;
                }
            }
        }
    }
}
