using System.Net;
using System.Net.Sockets;
using System.Text.Json;
using DotNext.Benchmarks.DurableWrite.Oracles;
using DotNext.Raft.FaultCampaign;
using DotNext.Raft.FaultCampaign.Driver;

namespace DotNext.Net.Cluster.Consensus.Raft.InProcess;

/// <summary>
/// The driver pieces of the real-process fault campaign (<c>src/DotNext.Raft.FaultCampaign</c>): how the history
/// polled from a node process reaches the oracles, the recovery oracle, the log classification, the schedule, and the
/// partition proxy and its oracle. The
/// campaign's <c>--inject</c> modes show that the oracles catch a failure in a running cluster.
/// </summary>
public sealed class FaultCampaignHarnessTests : Test
{
    private static readonly Guid First = Guid.NewGuid(), Second = Guid.NewGuid();

    private static AppliedEntry[] Log(params WriteKey?[] keys)
        => keys.Select(static (k, i) => new AppliedEntry(i + 1L, 1L, k)).ToArray();

    private static WriteKey Key(int client, long seq) => new(WriteKey.ClosedLoop, client, seq);

    [Fact]
    public static void PagesThatContinueTheHistoryAreApplied()
    {
        var checker = new OnlineHistoryChecker(1);
        var feed = new HistoryFeed(checker, 1);
        var log = Log(Key(0, 1), Key(0, 2), Key(1, 1));

        True(feed.Ingest(0, First, 0, 0, log[..2]));
        Equal((2, First, 0), feed.NextRequest(0));
        True(feed.Ingest(0, First, 0, 2, log[2..]));

        Equal(log, feed.Current(0));
        Equal(3L, checker.LastApplied(0));
        Equal(1, feed.Replacements(0));
        Null(checker.Violation);
    }

    [Fact]
    public static void PageThatDoesNotContinueTheHistoryIsDropped()
    {
        var feed = new HistoryFeed(new OnlineHistoryChecker(1), 1);
        True(feed.Ingest(0, First, 0, 0, Log(Key(0, 1))));

        False(feed.Ingest(0, First, 0, 2, Log(Key(0, 1), Key(0, 2), Key(0, 3))[2..]));
        False(feed.Ingest(0, Second, 0, 1, []));
        Single(feed.Current(0));
    }

    [Fact]
    public static void RestartedNodeMayComeBackWithAShorterHistory()
    {
        var checker = new OnlineHistoryChecker(2);
        var feed = new HistoryFeed(checker, 2);
        var log = Log(Key(0, 1), Key(0, 2), Key(0, 3));
        True(feed.Ingest(0, First, 0, 0, log));

        // Node 1 applied three entries, was killed before its commit index was persisted, and restarted with one.
        True(feed.Ingest(1, First, 0, 0, log));
        True(feed.Ingest(1, Second, 1, 0, log[..1]));
        True(feed.Ingest(1, Second, 1, 1, log[1..]));

        Equal(2, feed.Replacements(1));
        Null(checker.Violation);
    }

    [Fact]
    public static void ReplacedHistoryIsCheckedAgainstTheCommittedPrefix()
    {
        var checker = new OnlineHistoryChecker(2);
        var feed = new HistoryFeed(checker, 2);
        True(feed.Ingest(0, First, 0, 0, Log(Key(0, 1), Key(0, 2))));

        // Node 1 restarted from an empty data directory, as the volatile-storage injection does, and a new leader
        // committed a different write at index 1.
        True(feed.Ingest(1, Second, 0, 0, Log(Key(1, 1))));

        Equal(OnlineHistoryChecker.PrefixAgreement, checker.Violation?.Oracle);
    }

    [Fact]
    public static void RecoveryAuditRequiresEveryAcknowledgedWriteOnEveryNode()
    {
        var log = Log(null, Key(0, 1), Key(0, 2));
        AcknowledgedWrite[] acks = [new(Key(0, 1), 0, 2L), new(Key(0, 2), 1, 3L)];

        Null(RecoveryAudit.Check(acks, [log, log, log]));
        Equal(OnlineHistoryChecker.Durability, RecoveryAudit.Check(acks, [log, log, log[..2]])?.Oracle);

        var other = Log(null, Key(0, 1), Key(1, 7));
        var violation = RecoveryAudit.Check(acks, [log, other, log]);
        Equal(OnlineHistoryChecker.Durability, violation?.Oracle);
        Contains("node 1 has applied", violation?.Message, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("""{"EventId":74032,"LogLevel":"Critical","Category":"DotNext.Net.Cluster.Consensus.Raft.RaftCluster"}""", "Unexpected")]
    [InlineData("""{"EventId":74048,"LogLevel":"Error","Category":"DotNext.Net.Cluster.Consensus.Raft.RaftCluster"}""", "Unexpected")]
    [InlineData("""{"EventId":74049,"LogLevel":"Error","Category":"DotNext.Net.Cluster.Consensus.Raft.RaftCluster"}""", "Unexpected")]
    [InlineData("""{"EventId":75002,"LogLevel":"Error","Category":"DotNext.Net.Cluster.Consensus.Raft.Http.RaftHttpCluster"}""", "Unexpected")]
    [InlineData("""{"EventId":1,"LogLevel":"Critical","Category":"Microsoft.Hosting.Lifetime"}""", "Unexpected")]
    [InlineData("""{"EventId":74010,"LogLevel":"Warning","Category":"X","Exception":"DotNext.IO.Log.IntegrityException: ..."}""", "Unexpected")]
    [InlineData("Unhandled exception. System.IO.IOException: disk", "Unexpected")]
    [InlineData("""{"EventId":74010,"LogLevel":"Warning","Category":"DotNext.Net.Cluster.Consensus.Raft.Tcp.TcpServer"}""", "Expected")]
    [InlineData("""{"EventId":74015,"LogLevel":"Warning","Category":"DotNext.Net.Cluster.Consensus.Raft.RaftCluster"}""", "Expected")]
    [InlineData("""{"EventId":75001,"LogLevel":"Warning","Category":"DotNext.Net.Cluster.Consensus.Raft.Http.RaftHttpCluster"}""", "Expected")]
    [InlineData("""{"EventId":0,"LogLevel":"Error","Category":"DotNext.Net.Cluster.Consensus.Raft.RaftCluster"}""", "Expected")]
    [InlineData("""{"EventId":3,"LogLevel":"Warning","Category":"Microsoft.AspNetCore.Server.Kestrel"}""", "Unclassified")]
    [InlineData("""{"EventId":74000,"LogLevel":"Warning","Category":"DotNext.Net.Cluster.Consensus.Raft.RaftCluster"}""", "Unclassified")]
    [InlineData("{not json", "Unclassified")]
    public static void LogLinesAreClassifiedByTheDocumentedSignals(string line, string expected)
        => Equal(expected, LogClassifier.Classify(line)?.Class.ToString());

    [Theory]
    [InlineData("""{"EventId":74000,"LogLevel":"Debug","Category":"DotNext.Net.Cluster.Consensus.Raft.RaftCluster"}""")]
    [InlineData("""{"EventId":14,"LogLevel":"Information","Category":"Microsoft.Hosting.Lifetime"}""")]
    [InlineData("   at System.Net.Sockets.Socket.Connect()")]
    [InlineData("")]
    public static void OtherLinesAreNotSignals(string line)
        => Null(LogClassifier.Classify(line));

    [Fact]
    public static void ScanReadsOnlyCompleteLines()
    {
        var path = Path.GetTempFileName();
        try
        {
            const string unexpected = """{"EventId":74035,"LogLevel":"Error","Category":"DotNext.Net.Cluster.Consensus.Raft.RaftCluster"}""";
            var classifier = new LogClassifier();
            File.WriteAllText(path, unexpected + "\n" + unexpected[..10]);
            classifier.Scan(path);
            Single(classifier.Signals);

            File.AppendAllText(path, unexpected[10..] + "\n");
            classifier.Scan(path);
            classifier.Scan(path);

            Equal(2, classifier.Signals.Count);
            Equal(new[] { 1, 2 }, classifier.Signals.Select(static s => s.Line));
            Equal(2, classifier.Lines);
        }
        finally
        {
            File.Delete(path);
        }
    }

    [Fact]
    public static void HistoryRecordsRoundTrip()
    {
        AppliedEntry[] entries = [new(1L, 1L, null), new(2L, 3L, Key(2, 40L)), AppliedEntry.CreateSkipped(3L)];
        Equal(entries, entries.Select(static e => ControlApi.Decode(ControlApi.Encode(e))));
    }

    [Fact]
    public static void ScheduleIsDeterministicAndCoversEveryFault()
    {
        Equal(Schedule.Create(7, null), Schedule.Create(7, null));
        Equal(Enum.GetValues<FaultKind>().Order(), Schedule.Default.Distinct().Order());
        All(Schedule.Create(7, null), static e => InRange(e.Hold.TotalMilliseconds, 500D, 4000D));

        foreach (var kind in Enum.GetValues<FaultKind>())
            Equal(kind, Schedule.Parse(Schedule.NameOf(kind)));

        Throws<UsageException>(static () => Schedule.Parse("partition"));
    }

    [Fact]
    public static void BurnInRepeatsTheScheduleWithoutChangingTheSmokeCycle()
    {
        var smoke = Schedule.Create(7, null);
        var repeated = Schedule.Create(7, null, 3);
        Equal(repeated, Schedule.Create(7, null, 3));
        Equal(smoke, repeated[..smoke.Length]);
        Equal(Enumerable.Range(1, smoke.Length * 3), repeated.Select(static e => e.Number));
        for (var cycle = 0; cycle < 3; cycle++)
            Equal(Schedule.Default, repeated.Skip(cycle * smoke.Length).Take(smoke.Length).Select(static e => e.Kind));

        FaultKind[] subset = [FaultKind.ClusterKill, FaultKind.LaggingSnapshot];
        Equal(subset.Concat(subset), Schedule.Create(1, subset, 2).Select(static e => e.Kind));
        Throws<ArgumentOutOfRangeException>(static () => Schedule.Create(1, null, 0));
        Throws<ArgumentOutOfRangeException>(static () => Schedule.Create(1, null, 1001));
    }

    [Fact]
    public static void BurnInOptionsAreExplicitAndBounded()
    {
        var smoke = CampaignOptions.Parse([]);
        Equal(1, smoke.Cycles);
        Equal(200_000, smoke.MaxWrites);
        var burn = CampaignOptions.Parse(["--cycles", "30", "--max-writes", "10000", "--max-duration", "60"]);
        Equal(30, burn.Cycles);
        Equal(10_000, burn.MaxWrites);
        Equal(TimeSpan.FromHours(1), burn.MaxDuration);
        Throws<UsageException>(static () => CampaignOptions.Parse(["--cycles", "0"]));
        Throws<UsageException>(static () => CampaignOptions.Parse(["--cycles", "1001"]));
        Throws<UsageException>(static () => CampaignOptions.Parse(["--max-writes", "0"]));
        Throws<UsageException>(static () => CampaignOptions.Parse(["--max-writes", "1000001"]));
    }

    [Fact]
    public static void ResourceStorageInventorySeparatesPagesSnapshotsAndTemporaryFiles()
    {
        var directory = Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString());
        try
        {
            Directory.CreateDirectory(Path.Combine(directory, "wal", "data"));
            Directory.CreateDirectory(Path.Combine(directory, "wal", "metadata"));
            Directory.CreateDirectory(Path.Combine(directory, "sm"));
            File.WriteAllBytes(Path.Combine(directory, "wal", "data", "0"), new byte[16]);
            File.WriteAllBytes(Path.Combine(directory, "wal", "data", "1.a.tmp"), new byte[8]);
            File.WriteAllBytes(Path.Combine(directory, "wal", "metadata", "0"), new byte[4]);
            File.WriteAllBytes(Path.Combine(directory, "wal", "checkpoint"), new byte[2]);
            File.WriteAllBytes(Path.Combine(directory, "sm", "100-2"), new byte[32]);
            File.WriteAllBytes(Path.Combine(directory, "sm", "snapshot.tmp"), new byte[64]);
            var usage = StorageUsage.Capture(directory);
            Equal(30L, usage.WalBytes);
            Equal(1, usage.DataPages);
            Equal(1, usage.MetadataPages);
            Equal(32L, usage.SnapshotBytes);
            Equal(1, usage.SnapshotFiles);
            Equal(2, usage.TemporaryFiles);
            Equal(72L, usage.TemporaryBytes);
            Equal(0, usage.DisappearedFiles);
            var restored = JsonSerializer.Deserialize<StorageUsage>(JsonSerializer.Serialize(usage, ControlApi.Json), ControlApi.Json);
            NotNull(restored);
            Equal(usage.WalBytes, restored.WalBytes);
            Equal(usage.SnapshotBytes, restored.SnapshotBytes);
            Equal(usage.TemporaryFiles, restored.TemporaryFiles);
            File.Delete(Path.Combine(directory, "wal", "data", "0"));
            Equal(0, StorageUsage.Capture(directory).DataPages);
        }
        finally
        {
            Directory.Delete(directory, recursive: true);
        }
    }

    [Fact]
    public static void LinuxResourceSampleCountsDescriptorsRemovedAfterEnumeration()
    {
        if (!OperatingSystem.IsLinux())
            return;

        var directory = Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString());
        try
        {
            Directory.CreateDirectory(directory);
            var socket = Path.Combine(directory, "socket");
            var vanished = Path.Combine(directory, "vanished");
            var file = Path.Combine(directory, "file");
            File.CreateSymbolicLink(socket, "socket:[123]");
            File.CreateSymbolicLink(vanished, "/dev/null");
            File.CreateSymbolicLink(file, "/dev/null");
            var paths = Directory.GetFiles(directory);
            File.Delete(vanished);
            Null(new FileInfo(vanished).LinkTarget);

            var counts = ResourceUsage.CountDescriptors(paths);
            Equal(3, counts.Descriptors);
            Equal(1, counts.Sockets);
            Equal(1, counts.Vanished);
        }
        finally
        {
            Directory.Delete(directory, recursive: true);
        }
    }

    [Fact]
    public static void LinuxResourceSampleReportsTheCurrentProcessAndSockets()
    {
        if (!OperatingSystem.IsLinux())
            return;

        using var socket = new Socket(AddressFamily.InterNetwork, SocketType.Stream, ProtocolType.Tcp);
        socket.Bind(new IPEndPoint(IPAddress.Loopback, 0));
        socket.Listen(1);
        var usage = ResourceUsage.Capture();
        Equal(Environment.ProcessId, usage.Pid);
        True(usage.WorkingSetBytes > 0L);
        True(usage.ManagedBytes > 0L);
        True(usage.Threads > 0);
        True(usage.SocketDescriptors > 0);
        True(usage.FileDescriptors >= usage.SocketDescriptors);
        Equal(3, usage.GcCollections.Length);
        var restored = JsonSerializer.Deserialize<ResourceUsage>(JsonSerializer.Serialize(usage, ControlApi.Json), ControlApi.Json);
        NotNull(restored);
        Equal(usage.Pid, restored.Pid);
        Equal(usage.SocketDescriptors, restored.SocketDescriptors);
    }

    [Fact]
    public static void FollowerIsNeverTheLeader()
    {
        var random = new Random(1);
        for (var leader = 0; leader < 3; leader++)
        {
            for (var i = 0; i < 100; i++)
                NotEqual(leader, Schedule.PickFollower(random, 3, leader));
        }
    }

    [Fact]
    public static void UnknownOptionIsAUsageError()
    {
        var line = new CommandLine(["--seed", "3", "--sede", "4"]);
        Equal(3, line.GetInt32("seed", 1));
        Throws<UsageException>(line.RequireAllRead);
        Throws<UsageException>(static () => new CommandLine(["--seed"]));
    }

    [Theory]
    [InlineData("NaN")]
    [InlineData("Infinity")]
    [InlineData("-Infinity")]
    [InlineData("0.1")]
    [InlineData("241")]
    public static void NumberOutOfRangeIsAUsageError(string text)
    {
        var line = new CommandLine(["--max-duration", text]);
        Throws<UsageException>(() => line.GetDouble("max-duration", 10D, 0.5D, 240D));
    }

    [Fact]
    public static void NumberInRangeIsParsed()
    {
        Equal(1.5D, new CommandLine(["--max-duration", "1.5"]).GetDouble("max-duration", 10D, 0.5D, 240D));
        Equal(10D, new CommandLine([]).GetDouble("max-duration", 10D, 0.5D, 240D));
    }

    [Fact]
    public static void IsolationCutsBothDirectionsOfEveryLinkOfTheNode()
    {
        using var network = new PartitionNetwork([1, 2, 3], [4, 5, 6], static _ => null);
        False(network.IsCut(0, 1));
        False(network.IsCut(-1, 1));

        Equal(new[] { 0, 2 }, network.Isolate(1));
        True(network.IsCut(1, 0));
        True(network.IsCut(0, 1));
        True(network.IsCut(2, 1));
        True(network.IsCut(1, 2));
        False(network.IsCut(0, 2));
        False(network.IsCut(2, 0));

        // A connection whose source is unknown never crosses a partition.
        True(network.IsCut(-1, 0));

        // The partition-leak injection leaves one link of the isolated node up.
        Equal(new[] { 2 }, network.Isolate(0, leak: 1));
        False(network.IsCut(0, 1));
        True(network.IsCut(2, 0));

        network.Heal();
        False(network.IsCut(1, 0));
        False(network.IsCut(-1, 0));
    }

    [Fact]
    public static void SocketInodeIsFoundByLocalAndRemotePort()
    {
        string[] table =
        [
            "  sl  local_address rem_address   st tx_queue rx_queue tr tm->when retrnsmt   uid  timeout inode",
            "   0: 0100007F:1F90 00000000:0000 0A 00000000:00000000 00:00000000 00000000  1000        0 11111 1 0000000000000000 100 0 0 10 0",
            "   1: 0100007F:C350 0100007F:1F90 01 00000000:00000000 00:00000000 00000000  1000        0 22222 1 0000000000000000 20 4 30 10 -1",
            "   2: 0100007F:C351 0100007F:1F90 06 00000000:00000000 03:00000000 00000000     0        0 0 3 0000000000000000",
        ];

        Equal(22222L, SocketOwner.FindInode(table, 0xC350, 0x1F90));

        // A socket in TIME_WAIT has no inode and no owner.
        Null(SocketOwner.FindInode(table, 0xC351, 0x1F90));
        Null(SocketOwner.FindInode(table, 0x1F90, 0xC350));
    }

    [Fact(Timeout = TestTimeouts.Default)]
    public static async Task ProxyDropsTrafficAcrossTheCutAndResetsTheConnectionAtHeal()
    {
        var token = TestContext.Current.CancellationToken;
        using var upstream = new TcpListener(IPAddress.Loopback, 0);
        upstream.Start();
        var upstreamPort = ((IPEndPoint)upstream.LocalEndpoint).Port;
        int[] proxyPorts = [FreePort(), FreePort()];

        // This process plays node 0, the client; node 1 is the server behind its proxy.
        using var network = new PartitionNetwork(proxyPorts, [FreePort(), upstreamPort], static n => n is 0 ? Environment.ProcessId : null);
        network.Start();

        using var client = new Socket(AddressFamily.InterNetwork, SocketType.Stream, ProtocolType.Tcp);
        await client.ConnectAsync(new IPEndPoint(IPAddress.Loopback, proxyPorts[1]), token);
        using var server = await upstream.AcceptSocketAsync(token);
        var buffer = new byte[16];

        await client.SendAsync("a"u8.ToArray(), token);
        Equal(1, await server.ReceiveAsync(buffer, token));
        Equal(1, network.Statistics.ActiveConnections);

        network.Isolate(0);
        await client.SendAsync("b"u8.ToArray(), token);
        using (var quiet = CancellationTokenSource.CreateLinkedTokenSource(token))
        {
            quiet.CancelAfter(500);
            await ThrowsAnyAsync<OperationCanceledException>(async () => await server.ReceiveAsync(buffer, quiet.Token));
        }

        network.Heal();
        var reset = await Record.ExceptionAsync(async () =>
        {
            while (await client.ReceiveAsync(buffer, token) > 0)
            {
            }
        });

        Equal(SocketError.ConnectionReset, IsType<SocketException>(reset).SocketErrorCode);
        var statistics = network.Statistics;
        Equal(1L, statistics.Connections);
        Equal(0L, statistics.UnattributedConnections);
        Equal(1L, statistics.DroppedBytes);
        Equal(1L, statistics.ResetAtHeal);
        while (network.Statistics.ActiveConnections is not 0)
            await Task.Delay(20, token);
    }

    [Fact(Timeout = TestTimeouts.Default)]
    public static async Task ProxyHoldsACloseAcrossTheCutUntilHeal()
    {
        var token = TestContext.Current.CancellationToken;
        using var upstream = new TcpListener(IPAddress.Loopback, 0);
        upstream.Start();
        var upstreamPort = ((IPEndPoint)upstream.LocalEndpoint).Port;
        int[] proxyPorts = [FreePort(), FreePort()];

        using var network = new PartitionNetwork(proxyPorts, [FreePort(), upstreamPort], static n => n is 0 ? Environment.ProcessId : null);
        network.Start();

        using var client = new Socket(AddressFamily.InterNetwork, SocketType.Stream, ProtocolType.Tcp);
        await client.ConnectAsync(new IPEndPoint(IPAddress.Loopback, proxyPorts[1]), token);
        using var server = await upstream.AcceptSocketAsync(token);
        var buffer = new byte[16];

        network.Isolate(0);
        client.Shutdown(SocketShutdown.Send);
        using (var quiet = CancellationTokenSource.CreateLinkedTokenSource(token))
        {
            quiet.CancelAfter(500);
            await ThrowsAnyAsync<OperationCanceledException>(async () => await server.ReceiveAsync(buffer, quiet.Token));
        }

        network.Heal();
        var reset = await Record.ExceptionAsync(async () =>
        {
            while (await server.ReceiveAsync(buffer, token) > 0)
            {
            }
        });

        Equal(SocketError.ConnectionReset, IsType<SocketException>(reset).SocketErrorCode);
        Equal(1L, network.Statistics.ResetAtHeal);
    }

    private static int FreePort()
    {
        using var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        return ((IPEndPoint)listener.LocalEndpoint).Port;
    }

    [Fact(Timeout = TestTimeouts.Default)]
    public static async Task IsolatedNodeMustNotAcknowledgeOrApplyWritesInAStrictEpisode()
    {
        var oracle = new PartitionOracle();
        oracle.Begin(1, isolated: 0, strict: true, static () => 0);
        await oracle.OnSubmittingAsync(0, Key(0, 1), TestToken);
        await oracle.OnSubmittingAsync(1, Key(1, 1), TestToken);
        await oracle.OnSubmittingAsync(0, Key(0, 2), TestToken);
        oracle.OnOutcome(Key(0, 1), WriteOutcome.Rejected);
        oracle.OnOutcome(Key(1, 1), WriteOutcome.Acknowledged);
        oracle.OnOutcome(Key(0, 2), WriteOutcome.Unknown);
        Equal(0, await oracle.EndAsync(static () => { }, DefaultTimeout, TestToken));
        await oracle.OnSubmittingAsync(0, Key(0, 3), TestToken);

        oracle.OnOutcome(Key(0, 3), WriteOutcome.Acknowledged);
        oracle.Check([Log(Key(1, 1), Key(0, 3))]);
        Null(oracle.Violation);

        var counts = oracle.Count(1);
        Equal(2, counts.Sent);
        Equal(1, counts.Rejected);
        Equal(1, counts.Unknown);
        Equal(0, counts.Acknowledged);

        oracle.Check([Log(Key(1, 1)), Log(Key(1, 1), Key(0, 2))]);
        Equal(PartitionOracle.MinorityWriteApplied, oracle.Violation?.Oracle);
        Contains("on node 1", oracle.Violation?.Message, StringComparison.Ordinal);

        oracle = new PartitionOracle();
        oracle.Begin(2, isolated: 2, strict: true, static () => 0);
        await oracle.OnSubmittingAsync(2, Key(4, 1), TestToken);
        oracle.OnOutcome(Key(4, 1), WriteOutcome.Acknowledged);
        Equal(PartitionOracle.MinorityAcknowledgment, oracle.Violation?.Oracle);
    }

    [Fact(Timeout = TestTimeouts.Default)]
    public static async Task WritesToTheIsolatedNodeAreOnlyCountedInANonStrictEpisode()
    {
        // The partition heals before the majority commits in a new term: the old leader may still commit them.
        var oracle = new PartitionOracle();
        oracle.Begin(3, isolated: 1, strict: false, static () => 0);
        await oracle.OnSubmittingAsync(1, Key(0, 1), TestToken);
        oracle.OnOutcome(Key(0, 1), WriteOutcome.Acknowledged);
        Equal(0, await oracle.EndAsync(static () => { }, DefaultTimeout, TestToken));
        oracle.Check([Log(Key(0, 1))]);

        Null(oracle.Violation);
        Equal(1, oracle.Count(3).Acknowledged);
    }

    [Fact(Timeout = TestTimeouts.Default)]
    public static async Task HealWaitsForTheOutcomeOfEveryRecordedWrite()
    {
        var oracle = new PartitionOracle();
        oracle.Begin(4, isolated: 0, strict: true, static () => 0);
        await oracle.OnSubmittingAsync(0, Key(0, 1), TestToken);

        var healed = 0;
        var end = oracle.EndAsync(() => healed++, DefaultTimeout, TestToken);
        await Task.Delay(50, TestToken);
        False(end.IsCompleted);

        // A new write to the isolated node waits for the heal and is not recorded.
        var gated = oracle.OnSubmittingAsync(0, Key(0, 2), TestToken).AsTask();
        False(gated.IsCompleted);

        oracle.OnOutcome(Key(0, 1), WriteOutcome.Unknown);
        Equal(0, await end.WaitAsync(DefaultTimeout, TestToken));
        Equal(1, healed);
        await gated.WaitAsync(DefaultTimeout, TestToken);
        oracle.OnOutcome(Key(0, 2), WriteOutcome.Acknowledged);
        Null(oracle.Violation);
        Equal(1, oracle.Count(4).Sent);

        // A write with no outcome within the bound is only counted: the healed node may acknowledge it.
        oracle.Begin(5, isolated: 1, strict: true, static () => 0);
        await oracle.OnSubmittingAsync(1, Key(1, 1), TestToken);
        Equal(1, await oracle.EndAsync(static () => { }, TimeSpan.FromMilliseconds(50), TestToken));
        oracle.OnOutcome(Key(1, 1), WriteOutcome.Acknowledged);
        oracle.Check([Log(Key(1, 1))]);
        Null(oracle.Violation);
        Equal(1, oracle.Count(5).Acknowledged);
    }
}
