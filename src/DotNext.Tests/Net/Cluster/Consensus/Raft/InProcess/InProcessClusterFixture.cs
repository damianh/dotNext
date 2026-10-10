using System.Net;
using DotNext.Diagnostics;

namespace DotNext.Net.Cluster.Consensus.Raft.InProcess;

internal sealed class InProcessClusterFixture : Test, IAsyncDisposable
{
    internal readonly ManualTimeProvider TimeProvider = new();
    internal readonly InProcessNetwork Network = new();
    internal readonly IPersistentState[] States;
    internal readonly InProcessCluster[] Nodes;

    internal InProcessClusterFixture(
        int memberCount,
        Func<int, IPersistentState> stateFactory = null,
        InProcessCluster.LeaseOptions lease = null,
        Func<int, TimeProvider, TimeProvider> clockFactory = null,
        Func<TimeSpan, InProcessClusterMember, IFailureDetector> failureDetectorFactory = null,
        bool aggressiveLeaderStickiness = false)
    {
        EndPoint[] membership = Enumerable.Range(0, memberCount)
            .Select(i => new DnsEndPoint($"node-{i}", 0)).ToArray();
        States = Enumerable.Range(0, memberCount)
            .Select(i => stateFactory?.Invoke(i) ?? new ConsensusOnlyState())
            .ToArray();
        Nodes = States.Select((state, i) => new InProcessCluster(
            Network, ((DnsEndPoint)membership[i]).Host, membership, state,
            clockFactory?.Invoke(i, TimeProvider) ?? TimeProvider, TimeSpan.FromMilliseconds(100), startFollower: false, lease, failureDetectorFactory, aggressiveLeaderStickiness)).ToArray();
    }

    internal InProcessCluster Leader => Nodes[0];

    /// <summary>
    /// Restarts the node with its retained persistent state (term, vote and log).
    /// </summary>
    internal async Task<InProcessCluster> RestartAsync(int member)
    {
        var replacement = await Nodes[member].RestartAsync(static state => state, TestToken);
        await Nodes[member].DisposeAsync();
        return Nodes[member] = replacement;
    }

    internal async Task StartAsync()
    {
        foreach (var node in Nodes)
            await node.StartAsync(TestToken);
    }

    internal async Task ElectAsync(TimeSpan? electionDelay = null)
    {
        Leader.StartElectionTimer();
        TimeProvider.Advance(electionDelay ?? TimeSpan.FromMilliseconds(100));
        for (var i = 1; i < Nodes.Length; i++)
            await Network.DeliverAsync(await PendingAsync(i, RaftMessageType.PreVote));

        // All votes are requested before any response. Once a majority grants, the rest are canceled with the
        // candidate state (#146), so they may no longer be deliverable.
        var votes = new PendingMessage[Nodes.Length - 1];
        for (var i = 1; i < Nodes.Length; i++)
            votes[i - 1] = await PendingAsync(i, RaftMessageType.Vote);
        foreach (var vote in votes)
            await Network.TryDeliverAsync(vote);
        await Leader.WaitForLeaderAsync(TimeSpan.FromSeconds(5), TestToken);
    }

    internal void HoldFollowers()
    {
        foreach (var node in Nodes.Skip(1))
            Network.Hold(Leader.EndPoint, node.EndPoint);
    }

    internal async Task StartLeaderAsync(int laggingFollower = 0, TimeSpan? electionDelay = null)
    {
        await StartAsync();
        HoldFollowers();
        await ElectAsync(electionDelay);

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

        var entries = await PendingRoundAsync();
        for (var i = 0; i < entries.Length; i++)
        {
            if (i + 1 == laggingFollower)
                Network.Drop(entries[i]);
            else
                await Network.DeliverAsync(entries[i]);
        }
        await retry;
        await Leader.WaitForLeadershipAsync(TestToken);

        // The lease is activated by its own continuation on the write barrier, which can run after the one above.
        await Leader.LeaseActivation;
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
