using System.Buffers.Binary;
using System.Diagnostics;
using System.Net;
using System.Net.Sockets;
using Microsoft.Extensions.Logging.Abstractions;

namespace DotNext.Net.Cluster.Consensus.Raft.NetworkTransport.ConnectionOriented.Tcp;

using IO;
using IO.Log;
using Membership;
using StateMachine;
using LogEntryMetadata = NetworkTransport.LogEntryMetadata;

/// <summary>
/// Guards for issue #22 on the TCP transport. A peer that sends a request header and part of the body, then stalls,
/// must not hold the node's transition lock (or the log append lock) beyond the configured
/// <see cref="RaftCluster.NodeConfiguration.RequestTimeout"/>, which is the TCP server receive timeout.
/// Declared entry counts and lengths must not drive allocation.
/// </summary>
[Collection(TestCollections.AllocationBudget)]
public sealed class ProtocolInputBudgetTcpTests : RaftTest
{
    private const int LocalPort = 3362;
    private const int PeerPort = 3363;
    private const long SenderTerm = 5L;
    private const int DeclaredPayloadLength = 1024;
    private const int DeliveredPayloadLength = 16;
    private const int FrameHeaderSize = sizeof(int);

    private static readonly TimeSpan RequestTimeout = TimeSpan.FromSeconds(1);

    // The server cancels the request after RequestTimeout. The slack absorbs scheduling delays on a loaded CI agent.
    private static readonly TimeSpan ReleaseDeadline = RequestTimeout + TimeSpan.FromSeconds(2);

    // Gives the server time to read the header and enter the handler before the probe is sent.
    private static readonly TimeSpan StallLeadTime = TimeSpan.FromMilliseconds(300);

    [Theory(Timeout = TestTimeouts.Default)]
    [InlineData(false)]
    [InlineData(true)]
    public static async Task StalledAppendEntriesBodyReleasesTransitionLock(bool useWal)
    {
        var state = useWal ? CreateWal(IStateMachine.CreateNoOp()) : (IPersistentState)new ConsensusOnlyState();
        try
        {
            await using var cluster = new RaftCluster(CreateConfiguration()) { AuditTrail = state };
            await cluster.StartAsync(TestToken);

            var sender = new ClusterMemberId(Random.Shared);
            using var peer = await ConnectAsync();
            await peer.SendAsync(PartialAppendEntriesRequest(sender), SocketFlags.None, TestToken);
            await Task.Delay(StallLeadTime, TestToken);

            await ProbeAsync(cluster, sender, expectBlocked: true);
            await AssertConnectionClosedAsync(peer);
            await AssertLogUsableAsync(state);
        }
        finally
        {
            await DisposeAsync(state);
        }
    }

    [Fact(Timeout = TestTimeouts.Default)]
    public static async Task StalledInstallSnapshotBodyReleasesTransitionLock()
    {
        // the state machine reads the snapshot payload while the WAL and the transition lock are held
        var state = CreateWal(new DrainingStateMachine());
        try
        {
            await using var cluster = new RaftCluster(CreateConfiguration()) { AuditTrail = state };
            await cluster.StartAsync(TestToken);

            var sender = new ClusterMemberId(Random.Shared);
            using var peer = await ConnectAsync();
            await peer.SendAsync(PartialInstallSnapshotRequest(sender), SocketFlags.None, TestToken);
            await Task.Delay(StallLeadTime, TestToken);

            await ProbeAsync(cluster, sender, expectBlocked: true);
            await AssertConnectionClosedAsync(peer);
            await AssertLogUsableAsync(state);
        }
        finally
        {
            await DisposeAsync(state);
        }
    }

    // A peer that disconnects in the middle of the body causes a transport failure, not a cancellation.
    // The follower must stay available: the transition lock is released, the partial write is rolled back
    // and the log accepts further appends (#90). A graceful close (FIN) mid-body must not persist
    // the truncated entry or snapshot (#97).
    [Theory(Timeout = TestTimeouts.Default)]
    [InlineData(false, false)]
    [InlineData(false, true)]
    [InlineData(true, false)]
    [InlineData(true, true)]
    public static async Task PeerDisconnectMidBodyLeavesLogUsable(bool snapshot, bool reset)
    {
        var state = CreateWal(snapshot ? new DrainingStateMachine() : IStateMachine.CreateNoOp());
        try
        {
            await using var cluster = new RaftCluster(CreateConfiguration()) { AuditTrail = state };
            await cluster.StartAsync(TestToken);

            var sender = new ClusterMemberId(Random.Shared);
            using (var peer = await ConnectAsync())
            {
                await peer.SendAsync(snapshot ? PartialInstallSnapshotRequest(sender) : PartialAppendEntriesRequest(sender), SocketFlags.None, TestToken);
                await Task.Delay(StallLeadTime, TestToken);

                if (reset)
                    peer.LingerState = new(true, 0);
                else
                    peer.Shutdown(SocketShutdown.Send);

                peer.Close();
            }

            await ProbeAsync(cluster, sender, expectBlocked: false);

            // an entry or snapshot whose declared payload was not fully received is not in the log
            Equal(0L, state.LastEntryIndex);
            await AssertLogUsableAsync(state);
        }
        finally
        {
            await DisposeAsync(state);
        }
    }

    // After a graceful close (FIN) mid-entry, the leader retransmits the complete entry at the same index and term.
    // The follower must store it in full (#97).
    [Fact(Timeout = TestTimeouts.Default)]
    public static async Task RetransmissionAfterGracefulCloseMidEntryIsStoredInFull()
    {
        var state = CreateWal(IStateMachine.CreateNoOp());
        try
        {
            await using var cluster = new RaftCluster(CreateConfiguration()) { AuditTrail = state };
            await cluster.StartAsync(TestToken);

            var sender = new ClusterMemberId(Random.Shared);
            using (var peer = await ConnectAsync())
            {
                await peer.SendAsync(PartialAppendEntriesRequest(sender), SocketFlags.None, TestToken);
                await Task.Delay(StallLeadTime, TestToken);
                peer.Shutdown(SocketShutdown.Send);
                peer.Close();
            }

            await ProbeAsync(cluster, sender, expectBlocked: false);

            await using var retransmission = new LogEntryProducer<IRaftLogEntry>(new TestLogEntry(new string('x', DeclaredPayloadLength)) { Term = SenderTerm });
            await ((ILocalMember)cluster).AppendEntriesAsync(sender, SenderTerm, retransmission, 0L, 0L, 0L, 0, TestToken);

            Equal(1L, state.LastEntryIndex);
            Func<IReadOnlyList<IRaftLogEntry>, long?, CancellationToken, ValueTask<long>> reader = static (entries, _, _)
                => ValueTask.FromResult(entries[^1].Length ?? -1L);
            var length = await state.ReadAsync(new LogEntryConsumer<IRaftLogEntry, long>(reader), 1L, 1L, TestToken);
            TestContext.Current.TestOutputHelper?.WriteLine($"Entry 1 after a complete retransmission: stored {length} bytes");
            True(length >= DeclaredPayloadLength, $"Entry 1 stored {length} bytes after a complete retransmission");
        }
        finally
        {
            await DisposeAsync(state);
        }
    }

    // The memory reserved for received entries must be proportional to the entries received, not to the declared count.
    // The peer declares 2^25 entries, sends none and closes the connection.
    // ConsensusOnlyState is not covered: it reserves memory for the declared count (see RAFT-REVIEW.md, #22).
    [Fact(Timeout = TestTimeouts.Default)]
    public static async Task DeclaredEntryCountDoesNotDriveAllocation()
    {
        const int DeclaredCount = 1 << 25;
        const long AllocationBudget = 32L << 20;

        var state = CreateWal(IStateMachine.CreateNoOp());
        try
        {
            await using var cluster = new RaftCluster(CreateConfiguration()) { AuditTrail = state };
            await cluster.StartAsync(TestToken);

            var sender = new ClusterMemberId(Random.Shared);
            var request = new byte[1 + AppendEntriesMessage.Size];
            request[0] = (byte)MessageType.AppendEntries;
            new AppendEntriesMessage(sender, SenderTerm, 0L, 0L, 0L, EntriesCount: DeclaredCount, StateVersion: 0).Format(request.AsSpan(1));

            var before = GC.GetTotalAllocatedBytes(precise: true);
            using (var peer = await ConnectAsync())
            {
                await peer.SendAsync(request, SocketFlags.None, TestToken);
                peer.Shutdown(SocketShutdown.Send);

                // wait until the server drops the connection
                await AssertConnectionClosedAsync(peer);
            }

            await ProbeAsync(cluster, sender, expectBlocked: false);
            var allocated = GC.GetTotalAllocatedBytes(precise: true) - before;
            TestContext.Current.TestOutputHelper?.WriteLine($"Declared {DeclaredCount} entries, delivered 0, allocated {allocated} bytes, last entry index {state.LastEntryIndex}");

            True(allocated < AllocationBudget, $"Allocated {allocated} bytes for 0 delivered entries");
            Equal(0L, state.LastEntryIndex);
            await AssertLogUsableAsync(state);
        }
        finally
        {
            await DisposeAsync(state);
        }
    }

    // A declared entry length beyond the delivered payload must not drive allocation, and an entry whose payload
    // does not match its declared length must not be persisted (#97). The final frame carries 16 bytes and the
    // connection stays open, so the mismatch is the only defect of the request.
    [Theory(Timeout = TestTimeouts.Default)]
    [InlineData(1L << 30)]
    [InlineData(long.MaxValue)]
    public static async Task DeclaredEntryLengthDoesNotDriveAllocation(long declaredLength)
    {
        const long AllocationBudget = 32L << 20;

        var state = CreateWal(IStateMachine.CreateNoOp());
        try
        {
            await using var cluster = new RaftCluster(CreateConfiguration()) { AuditTrail = state };
            await cluster.StartAsync(TestToken);

            var sender = new ClusterMemberId(Random.Shared);
            var before = GC.GetTotalAllocatedBytes(precise: true);
            using (var peer = await ConnectAsync())
            {
                await peer.SendAsync(PartialAppendEntriesRequest(sender, declaredLength, DeliveredPayloadLength), SocketFlags.None, TestToken);
                await Task.Delay(StallLeadTime, TestToken);
                await ProbeAsync(cluster, sender, expectBlocked: false);
            }

            var allocated = GC.GetTotalAllocatedBytes(precise: true) - before;
            TestContext.Current.TestOutputHelper?.WriteLine($"Declared {declaredLength} bytes, delivered {DeliveredPayloadLength}, allocated {allocated} bytes, last entry index {state.LastEntryIndex}");
            True(allocated < AllocationBudget, $"Allocated {allocated} bytes for {DeliveredPayloadLength} delivered bytes");
            Equal(0L, state.LastEntryIndex);
            await AssertLogUsableAsync(state);
        }
        finally
        {
            await DisposeAsync(state);
        }
    }

    // A snapshot whose final frame is shorter than its declared length must not be installed (#97).
    // The connection stays open, so the mismatch is the only defect of the request.
    [Fact(Timeout = TestTimeouts.Default)]
    public static async Task SnapshotShorterThanDeclaredLengthIsNotInstalled()
    {
        var state = CreateWal(new DrainingStateMachine());
        try
        {
            await using var cluster = new RaftCluster(CreateConfiguration()) { AuditTrail = state };
            await cluster.StartAsync(TestToken);

            var sender = new ClusterMemberId(Random.Shared);
            using (var peer = await ConnectAsync())
            {
                await peer.SendAsync(PartialInstallSnapshotRequest(sender, DeliveredPayloadLength), SocketFlags.None, TestToken);
                await Task.Delay(StallLeadTime, TestToken);
                await ProbeAsync(cluster, sender, expectBlocked: false);
            }

            Equal(0L, state.LastEntryIndex);
            await AssertLogUsableAsync(state);
        }
        finally
        {
            await DisposeAsync(state);
        }
    }

    // one entry announcing 1 KiB of payload, but only 16 bytes are sent
    private static byte[] PartialAppendEntriesRequest(ClusterMemberId sender, long declaredLength = DeclaredPayloadLength, int frameLength = DeclaredPayloadLength)
    {
        var request = new byte[1 + AppendEntriesMessage.Size + LogEntryMetadata.Size + FrameHeaderSize + DeliveredPayloadLength];
        var offset = 0;
        request[offset++] = (byte)MessageType.AppendEntries;
        new AppendEntriesMessage(sender, SenderTerm, 0L, 0L, 0L, EntriesCount: 1, StateVersion: 0).Format(request.AsSpan(offset));
        offset += AppendEntriesMessage.Size;
        offset += WriteMetadata(request.AsSpan(offset), SenderTerm, isSnapshot: false, declaredLength);
        BinaryPrimitives.WriteInt32LittleEndian(request.AsSpan(offset), int.MinValue | frameLength);
        return request;
    }

    // header, an empty final configuration frame, then a snapshot announcing 1 KiB with 16 bytes sent
    private static byte[] PartialInstallSnapshotRequest(ClusterMemberId sender, int frameLength = DeclaredPayloadLength)
    {
        var request = new byte[1 + SnapshotMessage.Size + FrameHeaderSize + FrameHeaderSize + DeliveredPayloadLength];
        var offset = 0;
        request[offset++] = (byte)MessageType.InstallSnapshot;
        var metadata = new byte[LogEntryMetadata.Size];
        WriteMetadata(metadata, SenderTerm, isSnapshot: true, DeclaredPayloadLength);
        new SnapshotMessage(sender, SenderTerm, SnapshotIndex: 10L, new LogEntryMetadata(metadata), ConfigurationVersion: 0L, StateVersion: 0)
            .Format(request.AsSpan(offset));
        offset += SnapshotMessage.Size;
        BinaryPrimitives.WriteInt32LittleEndian(request.AsSpan(offset), int.MinValue);
        offset += FrameHeaderSize;
        BinaryPrimitives.WriteInt32LittleEndian(request.AsSpan(offset), int.MinValue | frameLength);
        return request;
    }

    private static async Task ProbeAsync(RaftCluster cluster, ClusterMemberId sender, bool expectBlocked)
    {
        // a heartbeat always acquires the transition lock
        var timer = Stopwatch.StartNew();
        var probe = await ((ILocalMember)cluster)
            .AppendEntriesAsync(sender, SenderTerm, ILogEntryProducer<IRaftLogEntry>.Empty, 0L, 0L, 0L, 0, TestToken)
            .AsTask()
            .WaitAsync(ReleaseDeadline, TestToken);
        timer.Stop();
        TestContext.Current.TestOutputHelper?.WriteLine($"Probe acquired the transition lock after {timer.Elapsed}");

        // the stalled request held the lock, otherwise the probe would not test anything
        if (expectBlocked)
            True(timer.Elapsed >= TimeSpan.FromMilliseconds(250), $"The probe was not blocked ({timer.Elapsed})");

        True(probe.Value.Result is HeartbeatResult.Replicated or HeartbeatResult.ReplicatedWithLeaderTerm);
    }

    // the server drops the stalled connection
    private static async Task AssertConnectionClosedAsync(Socket peer)
    {
        var buffer = new byte[64];
        try
        {
            Equal(0, await peer.ReceiveAsync(buffer, SocketFlags.None, TestToken).AsTask().WaitAsync(ReleaseDeadline, TestToken));
        }
        catch (SocketException e) when (e.SocketErrorCode is SocketError.ConnectionReset)
        {
            // reset is also a closed connection
        }
    }

    private static async Task AssertLogUsableAsync(IPersistentState state)
    {
        var index = await state.AppendAsync(new EmptyLogEntry { Term = SenderTerm }, TestToken).AsTask().WaitAsync(ReleaseDeadline, TestToken);
        Equal(state.LastEntryIndex, index);
    }
    private static int WriteMetadata(Span<byte> output, long term, bool isSnapshot, long length)
    {
        // see LogEntryMetadata.Format: term, flags, command id, length
        BinaryPrimitives.WriteInt64LittleEndian(output, term);
        output[sizeof(long)] = isSnapshot ? (byte)2 : (byte)0;
        BinaryPrimitives.WriteInt32LittleEndian(output.Slice(sizeof(long) + 1), 0);
        BinaryPrimitives.WriteInt64LittleEndian(output.Slice(sizeof(long) + 1 + sizeof(int)), length);
        return LogEntryMetadata.Size;
    }

    private static async Task<Socket> ConnectAsync()
    {
        var socket = new Socket(AddressFamily.InterNetwork, SocketType.Stream, ProtocolType.Tcp);
        await socket.ConnectAsync(new IPEndPoint(IPAddress.Loopback, LocalPort), TestToken);
        return socket;
    }

    private static ValueTask DisposeAsync(IPersistentState state) => state switch
    {
        IAsyncDisposable disposable => disposable.DisposeAsync(),
        IDisposable disposable => Dispose(disposable),
        _ => ValueTask.CompletedTask,
    };

    private static ValueTask Dispose(IDisposable disposable)
    {
        disposable.Dispose();
        return ValueTask.CompletedTask;
    }

    private static WriteAheadLog CreateWal(IStateMachine stateMachine)
        => new(new() { Location = GetTempPath() }, stateMachine);

    private static RaftCluster.TcpConfiguration CreateConfiguration()
    {
        var result = new RaftCluster.TcpConfiguration(new IPEndPoint(IPAddress.Loopback, LocalPort))
        {
            ColdStart = false,

            // a standby node never starts elections, so only the requests in the test take the transition lock
            Standby = true,
            RequestTimeout = RequestTimeout,
            LoggerFactory = NullLoggerFactory.Instance,
            ConfigurationStorage = null,
        };

        var configuration = result.ConfigurationStorage as InMemoryClusterConfigurationStorage<EndPoint>;
        NotNull(configuration);
        var builder = configuration.CreateInitialConfigurationBuilder();
        builder.Add(new IPEndPoint(IPAddress.Loopback, LocalPort));
        builder.Add(new IPEndPoint(IPAddress.Loopback, PeerPort));
        builder.Build();

        return result;
    }

    private sealed class DrainingStateMachine : IStateMachine
    {
        ISnapshot ISnapshotManager.Snapshot => null;

        ValueTask ISnapshotManager.ReclaimGarbageAsync(long watermark, CancellationToken token) => ValueTask.CompletedTask;

        bool IStateMachine.IsSnapshotInstallCancellationSafe => true;

        async ValueTask<long> IStateMachine.ApplyAsync(LogEntry entry, CancellationToken token)
        {
            if (entry.IsSnapshot)
                await entry.WriteToAsync(Stream.Null, token: token);

            return entry.Index;
        }
    }
}
