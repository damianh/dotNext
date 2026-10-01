using System.Buffers.Binary;
using System.Diagnostics;
using System.Net;
using System.Net.Sockets;
using System.Text;
using System.Text.RegularExpressions;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Connections;
using Microsoft.AspNetCore.Hosting;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;

namespace DotNext.Net.Cluster.Consensus.Raft.Http;

using IO;
using Membership;
using StateMachine;

/// <summary>
/// Guards for issue #22 that exercise the HTTP consensus endpoint with raw HTTP/1.1 requests.
/// </summary>
[Collection(TestCollections.AllocationBudget)]
public sealed class ProtocolInputBudgetHttpTests : RaftTest
{
    private const int Port = 3262;
    private const int PeerPort = 3263;
    private const string ProtocolPath = "/cluster-consensus/raft";
    private const long SenderTerm = 5L;
    private const int MetadataSize = 21; // see NetworkTransport.LogEntryMetadata
    private const int DeliveredPayloadLength = 16;

    private static readonly TimeSpan RequestTimeout = TimeSpan.FromSeconds(1);

    // Bounds every request of a test. The slack absorbs scheduling delays on a loaded CI agent.
    private static readonly TimeSpan ReleaseDeadline = RequestTimeout + TimeSpan.FromSeconds(2);

    // X-Raft-Config-Length that is negative or exceeds the known Content-Length is a protocol error:
    // the request is rejected, nothing is staged or installed, and the node keeps serving requests.
    [Theory(Timeout = TestTimeouts.Default)]
    [InlineData("-1")]
    [InlineData("-2147483648")]
    [InlineData("1024")]
    public static async Task InconsistentConfigurationLengthIsRejected(string configLength)
    {
        using var host = CreateHost();
        await host.StartAsync(TestToken);
        try
        {
            var sender = new ClusterMemberId(Random.Shared);
            var response = await SendAsync(FormatRequest(InstallSnapshotHeaders(sender, configLength), "application/octet-stream",
                contentLength: DeliveredPayloadLength, new byte[DeliveredPayloadLength])).WaitAsync(ReleaseDeadline, TestToken);
            TestContext.Current.TestOutputHelper?.WriteLine($"InstallSnapshot: {StatusLine(response)}");
            DoesNotMatch(@"^HTTP/1\.1 2\d\d", response);

            await AssertAvailableAsync(host, sender);
        }
        finally
        {
            await host.StopAsync(TestToken);
        }
    }

    // Control for the cases above: a valid InstallSnapshot of unknown length (chunked) is accepted.
    [Fact(Timeout = TestTimeouts.Default)]
    public static async Task ChunkedInstallSnapshotIsAccepted()
    {
        using var host = CreateHost();
        await host.StartAsync(TestToken);
        try
        {
            var sender = new ClusterMemberId(Random.Shared);
            var response = await SendAsync(FormatRequest(InstallSnapshotHeaders(sender, configLength: EmptyConfiguration.Length.ToString()), "application/octet-stream",
                contentLength: null, Chunked([.. EmptyConfiguration, .. new byte[DeliveredPayloadLength]]))).WaitAsync(ReleaseDeadline, TestToken);
            TestContext.Current.TestOutputHelper?.WriteLine($"InstallSnapshot: {StatusLine(response)}");

            StartsWith("HTTP/1.1 200", response);
            Equal(10L, AuditTrail(host).LastEntryIndex);
        }
        finally
        {
            await host.StopAsync(TestToken);
        }
    }

    // A declared entry count beyond the delivered entries must not make the follower commit or acknowledge
    // entries it has not received; the node stays available. The follower has an uncommitted tail that the leader
    // has not confirmed, so a commit index derived from the declared count would commit it.
    // ConsensusOnlyState with multipart entries is not covered: it violates this contract (see RAFT-REVIEW.md, #22).
    [Theory(Timeout = TestTimeouts.Default)]
    [InlineData(true, true)]
    [InlineData(false, true)]
    [InlineData(false, false)]
    public static async Task EntryCountBeyondDeliveredEntriesIsNotAcknowledged(bool multipart, bool useWal)
    {
        const int DeclaredCount = 4;
        const long StaleTerm = 1L;

        using var host = CreateHost(useWal);
        await host.StartAsync(TestToken);
        try
        {
            // an uncommitted tail from a former leader; the new leader agrees with index 1 only
            var log = AuditTrail(host);
            for (var i = 0; i < 3; i++)
                await log.AppendAsync(new EmptyLogEntry { Term = StaleTerm }, TestToken);

            var sender = new ClusterMemberId(Random.Shared);
            var payload = new byte[DeliveredPayloadLength];
            byte[] request;
            if (multipart)
            {
                const string Boundary = "entries";
                byte[] body = [
                    .. Encoding.ASCII.GetBytes($"--{Boundary}\r\nX-Raft-Record-Term: {StaleTerm}\r\nX-Raft-Configuration: false\r\n\r\n"),
                    .. payload,
                    .. Encoding.ASCII.GetBytes($"\r\n--{Boundary}--\r\n"),
                ];
                request = FormatRequest(AppendEntriesHeaders(sender, DeclaredCount, commitIndex: DeclaredCount),
                    $"multipart/mixed; boundary=\"{Boundary}\"", body.Length, body);
            }
            else
            {
                var body = new byte[MetadataSize + DeliveredPayloadLength];
                WriteMetadata(body, StaleTerm, DeliveredPayloadLength);
                request = FormatRequest(AppendEntriesHeaders(sender, DeclaredCount, commitIndex: DeclaredCount),
                    "application/octet-stream", body.Length, body);
            }

            var response = await SendAsync(request).WaitAsync(ReleaseDeadline, TestToken);
            var lastIndexHeader = Regex.Match(response, @"X-Raft-Last-Index: (\d+)", RegexOptions.IgnoreCase);
            TestContext.Current.TestOutputHelper?.WriteLine(
                $"AppendEntries: {StatusLine(response)}, acknowledged last index {(lastIndexHeader.Success ? lastIndexHeader.Groups[1].Value : "none")}, last entry index {log.LastEntryIndex}, committed {log.LastCommittedEntryIndex}");

            if (lastIndexHeader.Success && response.StartsWith("HTTP/1.1 200", StringComparison.Ordinal))
                True(long.Parse(lastIndexHeader.Groups[1].Value) <= 1L, "Acknowledged entries that were not delivered");

            True(log.LastCommittedEntryIndex <= 1L, "Committed entries that were not delivered");

            StartsWith("HTTP/1.1 200", await SendAsync(HeartbeatRequest(sender)).WaitAsync(ReleaseDeadline, TestToken));
            True(await IsLogUsableAsync(log), "The log rejects appends");
        }
        finally
        {
            await host.StopAsync(TestToken);
        }
    }

    // A declared entry length beyond the delivered payload must not drive allocation or persist a truncated entry.
    // Whether the log stays usable after the failed read is not covered here (see RAFT-REVIEW.md, #22).
    [Theory(Timeout = TestTimeouts.Default)]
    [InlineData(1L << 30)]
    [InlineData(long.MaxValue)]
    public static async Task DeclaredEntryLengthDoesNotDriveAllocation(long declaredLength)
    {
        const long AllocationBudget = 32L << 20;

        using var host = CreateHost();
        await host.StartAsync(TestToken);
        try
        {
            var log = AuditTrail(host);
            var sender = new ClusterMemberId(Random.Shared);
            var body = new byte[MetadataSize + DeliveredPayloadLength];
            WriteMetadata(body, SenderTerm, declaredLength);
            var request = FormatRequest(AppendEntriesHeaders(sender, entriesCount: 1L, commitIndex: 1L), "application/octet-stream", body.Length, body);

            var before = GC.GetTotalAllocatedBytes(precise: true);
            var response = await SendAsync(request).WaitAsync(ReleaseDeadline, TestToken);
            var allocated = GC.GetTotalAllocatedBytes(precise: true) - before;
            TestContext.Current.TestOutputHelper?.WriteLine(
                $"AppendEntries: {StatusLine(response)}, declared {declaredLength} bytes, allocated {allocated} bytes, last entry index {log.LastEntryIndex}, committed {log.LastCommittedEntryIndex}");

            DoesNotContain("HTTP/1.1 2", StatusLine(response), StringComparison.Ordinal);
            True(allocated < AllocationBudget, $"Allocated {allocated} bytes for {DeliveredPayloadLength} delivered bytes");
            Equal(0L, log.LastEntryIndex);
            Equal(0L, log.LastCommittedEntryIndex);
        }
        finally
        {
            await host.StopAsync(TestToken);
        }
    }

    private static IPersistentState AuditTrail(IHost host) => host.Services.GetRequiredService<IRaftCluster>().AuditTrail;

    // the encoding of an empty configuration: a zero member count
    private static ReadOnlySpan<byte> EmptyConfiguration => [0, 0, 0, 0];

    private static byte[] Chunked(ReadOnlySpan<byte> body)
        => [.. Encoding.ASCII.GetBytes($"{body.Length:x}\r\n"), .. body, .. "\r\n0\r\n\r\n"u8];

    private static async Task AssertAvailableAsync(IHost host, ClusterMemberId sender)
    {
        var response = await SendAsync(HeartbeatRequest(sender)).WaitAsync(ReleaseDeadline, TestToken);
        StartsWith("HTTP/1.1 200", response);

        var log = AuditTrail(host);
        Equal(0L, log.LastEntryIndex);
        True(await IsLogUsableAsync(log), "The log rejects appends");
    }

    private static byte[] HeartbeatRequest(ClusterMemberId sender)
        => FormatRequest(AppendEntriesHeaders(sender, entriesCount: 0), contentType: null, contentLength: 0L, []);

    private static string StatusLine(string response)
    {
        var end = response.IndexOf("\r\n", StringComparison.Ordinal);
        return end >= 0 ? response[..end] : response;
    }

    private static async Task<bool> IsLogUsableAsync(IPersistentState log)
    {
        try
        {
            await log.AppendAsync(new EmptyLogEntry { Term = SenderTerm }, TestToken).AsTask().WaitAsync(ReleaseDeadline, TestToken);
            return true;
        }
        catch (Exception e)
        {
            TestContext.Current.TestOutputHelper?.WriteLine($"Append failed: {e.GetType().Name}: {e.Message}");
            return false;
        }
    }

    private static IHost CreateHost(bool useWal = true) => new HostBuilder()
        .ConfigureWebHost(webHost =>
        {
            webHost.UseKestrel(static options => options.ListenLocalhost(Port));

            if (useWal)
                webHost.UseStartup<DrainingStartup>();
            else
                webHost.UseStartup<ConsensusOnlyStartup>();
        })
        .ConfigureHostOptions(static options => options.ShutdownTimeout = DefaultTimeout)
        .ConfigureAppConfiguration(builder => builder.AddInMemoryCollection(new Dictionary<string, string>
        {
            ["publicEndPoint"] = $"http://localhost:{Port}",
            ["coldStart"] = "false",

            // a standby node never starts elections, so its term stays below the term of the requests in the tests
            ["standby"] = "true",
            ["requestTimeout"] = RequestTimeout.ToString(),
        }))
        .JoinCluster()
        .Build();

    private static IEnumerable<(string, string)> CommonHeaders(ClusterMemberId sender, string messageType)
    {
        yield return ("X-Raft-Message-Type", messageType);
        yield return ("X-Raft-Node-ID", sender.ToString());
        yield return ("X-Request-ID", Guid.NewGuid().ToString("N"));
        yield return ("X-Raft-Term", SenderTerm.ToString());
    }

    private static IEnumerable<(string, string)> AppendEntriesHeaders(ClusterMemberId sender, long entriesCount, long commitIndex = 0L)
        => CommonHeaders(sender, "AppendEntries").Concat([
            ("X-Raft-Preceding-Record-Index", "0"),
            ("X-Raft-Preceding-Record-Term", "0"),
            ("X-Raft-Commit-Index", commitIndex.ToString()),
            ("X-Raft-Entries-Count", entriesCount.ToString()),
        ]);

    private static IEnumerable<(string, string)> InstallSnapshotHeaders(ClusterMemberId sender, string configLength, string configVersion = "0")
        => CommonHeaders(sender, "InstallSnapshot").Concat([
            ("X-Raft-Snapshot-Index", "10"),
            ("X-Raft-Snapshot-Term", SenderTerm.ToString()),
            ("X-Raft-Config-Length", configLength),
            ("X-Raft-Config-Version", configVersion),
        ]);

    private static byte[] FormatRequest(IEnumerable<(string Name, string Value)> headers, string contentType, long? contentLength, ReadOnlySpan<byte> body)
    {
        var builder = new StringBuilder();
        builder.Append($"POST {ProtocolPath} HTTP/1.1\r\nHost: localhost:{Port}\r\n");
        foreach (var (name, value) in headers)
            builder.Append($"{name}: {value}\r\n");

        if (contentType is not null)
            builder.Append($"Content-Type: {contentType}\r\n");

        builder.Append(contentLength is { } length ? $"Content-Length: {length}\r\n" : "Transfer-Encoding: chunked\r\n");
        builder.Append("\r\n");
        return [.. Encoding.ASCII.GetBytes(builder.ToString()), .. body];
    }

    private static void WriteMetadata(Span<byte> output, long term, long length)
    {
        // see LogEntryMetadata.Format: term, flags, command id, length
        BinaryPrimitives.WriteInt64LittleEndian(output, term);
        output[sizeof(long)] = 0;
        BinaryPrimitives.WriteInt32LittleEndian(output.Slice(sizeof(long) + 1), 0);
        BinaryPrimitives.WriteInt64LittleEndian(output.Slice(sizeof(long) + 1 + sizeof(int)), length);
    }

    private static async Task<Socket> ConnectAsync()
    {
        var socket = new Socket(AddressFamily.InterNetwork, SocketType.Stream, ProtocolType.Tcp);
        await socket.ConnectAsync(new IPEndPoint(IPAddress.Loopback, Port), TestToken);
        return socket;
    }

    // sends a complete request on a new connection and returns the response head
    private static async Task<string> SendAsync(byte[] request)
    {
        using var socket = await ConnectAsync();
        await socket.SendAsync(request, SocketFlags.None, TestToken);

        var buffer = new byte[4096];
        var received = 0;
        while (true)
        {
            var count = await socket.ReceiveAsync(buffer.AsMemory(received), SocketFlags.None, TestToken);
            if (count is 0)
                break;

            received += count;
            var text = Encoding.ASCII.GetString(buffer, 0, received);
            var headEnd = text.IndexOf("\r\n\r\n", StringComparison.Ordinal);
            if (headEnd >= 0 && (text.Contains("Content-Length: 0", StringComparison.OrdinalIgnoreCase) || received > headEnd + 4))
                break;
        }

        return Encoding.ASCII.GetString(buffer, 0, received);
    }

    private class ConsensusOnlyStartup
    {
        public void Configure(IApplicationBuilder app) => app.UseConsensusProtocolHandler();

        private protected virtual void AddState(IServiceCollection services)
            => services.AddSingleton<IPersistentState>(new ConsensusOnlyState());

        public void ConfigureServices(IServiceCollection services)
        {
            AddState(services);
            services.AddOptions()
                .UseInMemoryConfigurationStorage(static members =>
                {
                    members.Add(new UriEndPoint(new Uri($"http://localhost:{Port}", UriKind.Absolute)));
                    members.Add(new UriEndPoint(new Uri($"http://localhost:{PeerPort}", UriKind.Absolute)));
                });
        }
    }

    private sealed class DrainingStartup : ConsensusOnlyStartup
    {
        private protected override void AddState(IServiceCollection services)
            => services
                .AddSingleton<IStateMachine>(new DrainingStateMachine())
                .UsePersistentLog(new() { Location = GetTempPath() });
    }

    // reads the snapshot payload while the WAL and the transition lock are held
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
