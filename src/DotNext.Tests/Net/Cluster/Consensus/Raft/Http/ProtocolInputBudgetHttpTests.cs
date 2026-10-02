using System.Buffers.Binary;
using System.Diagnostics;
using System.Net;
using System.Net.Sockets;
using System.Reflection;
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
    private const int DeclaredPayloadLength = 1024;
    private const int DeliveredPayloadLength = 16;

    private static readonly TimeSpan RequestTimeout = TimeSpan.FromSeconds(1);

    // Longer than Kestrel's MinRequestBodyDataRate grace period (5 seconds by default), so that Kestrel cuts off a stalled body first.
    private static readonly TimeSpan LongRequestTimeout = TimeSpan.FromSeconds(10);

    // Bounds every request of a test. The node's own request timeout bounds a stalled request (#91).
    // The slack absorbs scheduling delays on a loaded CI agent, and the deadline stays below Kestrel's
    // 5 second MinRequestBodyDataRate grace period.
    private static readonly TimeSpan ReleaseDeadline = RequestTimeout + TimeSpan.FromSeconds(2);

    // Bounds a request that only Kestrel's MinRequestBodyDataRate releases, and how long a test watches a request
    // that is not released, to report the actual release time.
    private static readonly TimeSpan ObservationLimit = TimeSpan.FromSeconds(15);

    // Gives the server time to parse the headers and enter the handler before the peer disconnects or the probe is sent.
    private static readonly TimeSpan StallLeadTime = TimeSpan.FromMilliseconds(300);

    // The node's own RequestTimeout must bound how long a request that stalls in the body holds the transition lock,
    // as the TCP server does with its receive timeout (#91). This must hold without Kestrel's MinRequestBodyDataRate.
    // The partially received entry or snapshot is rolled back, the log stays usable, and the stalled request
    // does not get a 2xx response.
    [Theory(Timeout = TestTimeouts.Default)]
    [InlineData(false, true)]
    [InlineData(false, false)]
    [InlineData(true, true)]
    [InlineData(true, false)]
    public static async Task StalledBodyReleasesTransitionLockWithinRequestTimeout(bool snapshot, bool kestrelMinDataRate)
    {
        using var host = CreateHost(kestrelMinDataRate: kestrelMinDataRate);
        await host.StartAsync(TestToken);
        var sender = new ClusterMemberId(Random.Shared);
        var peer = await ConnectAsync();
        Task<string> probe = null;
        try
        {
            await peer.SendAsync(PartialRequest(sender, snapshot), SocketFlags.None, TestToken);
            await Task.Delay(StallLeadTime, TestToken);

            // a heartbeat always acquires the transition lock
            var timer = Stopwatch.StartNew();
            probe = SendAsync(HeartbeatRequest(sender));
            string response;
            try
            {
                response = await probe.WaitAsync(ObservationLimit, TestToken);
            }
            catch (TimeoutException)
            {
                Fail($"The transition lock was not released within {ObservationLimit + StallLeadTime}, RequestTimeout is {RequestTimeout}");
                throw;
            }

            timer.Stop();
            TestContext.Current.TestOutputHelper?.WriteLine($"Probe acquired the transition lock after {timer.Elapsed}: {StatusLine(response)}");

            // the stalled request held the lock, otherwise the probe would not test anything
            True(timer.Elapsed >= TimeSpan.FromMilliseconds(250), $"The probe was not blocked ({timer.Elapsed})");
            True(timer.Elapsed <= ReleaseDeadline, $"The transition lock was held for {timer.Elapsed + StallLeadTime}, RequestTimeout is {RequestTimeout}");
            StartsWith("HTTP/1.1 200", response);

            var stalled = await ReceiveOutcomeAsync(peer).WaitAsync(ReleaseDeadline, TestToken);
            var log = AuditTrail(host);
            TestContext.Current.TestOutputHelper?.WriteLine($"Stalled request: {stalled}, last entry index: {log.LastEntryIndex}");

            DoesNotMatch(@"^HTTP/1\.1 2\d\d", stalled);
            Equal(0L, log.LastEntryIndex);
            True(await IsLogUsableAsync(log), "The log rejects appends");
            StartsWith("HTTP/1.1 200", await SendAsync(HeartbeatRequest(sender)).WaitAsync(ReleaseDeadline, TestToken));
        }
        finally
        {
            // reset the stalled connection so that the host can stop
            Reset(peer);
            if (probe is not null)
                await probe.ContinueWith(static _ => { }, TestToken);

            await host.StopAsync(TestToken);
        }
    }

    // A peer that disconnects in the middle of the body causes a transport failure, not a cancellation.
    // The follower must stay available, and the partially received entry or snapshot must not be in the log (#90).
    [Theory(Timeout = TestTimeouts.Default)]
    [InlineData(false, false)]
    [InlineData(false, true)]
    [InlineData(true, false)]
    [InlineData(true, true)]
    public static async Task PeerDisconnectMidBodyLeavesLogUsable(bool snapshot, bool reset)
    {
        using var host = CreateHost();
        await host.StartAsync(TestToken);
        try
        {
            var sender = new ClusterMemberId(Random.Shared);
            using (var peer = await ConnectAsync())
            {
                await peer.SendAsync(PartialRequest(sender, snapshot), SocketFlags.None, TestToken);
                await Task.Delay(StallLeadTime, TestToken);

                if (reset)
                {
                    Reset(peer);
                }
                else
                {
                    peer.Shutdown(SocketShutdown.Send);
                    peer.Close();
                }
            }

            var response = await SendAsync(HeartbeatRequest(sender)).WaitAsync(ReleaseDeadline, TestToken);
            var log = AuditTrail(host);
            TestContext.Current.TestOutputHelper?.WriteLine($"Probe: {StatusLine(response)}, last entry index: {log.LastEntryIndex}");

            StartsWith("HTTP/1.1 200", response);
            Equal(0L, log.LastEntryIndex);
            True(await IsLogUsableAsync(log), "The log rejects appends");
        }
        finally
        {
            await host.StopAsync(TestToken);
        }
    }

    // A peer that stalls in the middle of the body is cut off by Kestrel's MinRequestBodyDataRate.
    // That timeout must cost the request only, as a disconnect does (#90). The node's RequestTimeout is longer
    // than Kestrel's grace period here, so that Kestrel cuts off the request before the node's own deadline (#91) does.
    [Theory(Timeout = TestTimeouts.Default)]
    [InlineData(false)]
    [InlineData(true)]
    public static async Task MinRequestBodyDataRateMidBodyLeavesLogUsable(bool snapshot)
    {
        using var host = CreateHost(requestTimeout: LongRequestTimeout);
        await host.StartAsync(TestToken);
        var peer = await ConnectAsync();
        try
        {
            var sender = new ClusterMemberId(Random.Shared);
            await peer.SendAsync(PartialRequest(sender, snapshot), SocketFlags.None, TestToken);
            await Task.Delay(StallLeadTime, TestToken);

            // a heartbeat always acquires the transition lock, so it completes after the stalled request is released
            var timer = Stopwatch.StartNew();
            var response = await SendAsync(HeartbeatRequest(sender)).WaitAsync(ObservationLimit, TestToken);
            timer.Stop();
            var log = AuditTrail(host);
            TestContext.Current.TestOutputHelper?.WriteLine($"Probe after {timer.Elapsed}: {StatusLine(response)}, last entry index: {log.LastEntryIndex}");

            StartsWith("HTTP/1.1 200", response);
            Equal(0L, log.LastEntryIndex);
            True(await IsLogUsableAsync(log), "The log rejects appends");
        }
        finally
        {
            Reset(peer);
            await host.StopAsync(TestToken);
        }
    }

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

    // A valid configuration of unknown length (chunked) that is actually delivered is staged and applied with the snapshot.
    [Fact(Timeout = TestTimeouts.Default)]
    public static async Task ChunkedInstallSnapshotWithConfigurationIsAccepted()
    {
        using var host = CreateHost();
        await host.StartAsync(TestToken);
        try
        {
            var storage = host.Services.GetRequiredService<IClusterConfigurationStorage<UriEndPoint>>();
            var (current, _) = await ((IClusterConfigurationStorage)storage).LoadConfigurationAsync(TestToken);
            var configuration = await current.ToByteArrayAsync(token: TestToken);
            True(configuration.Length > EmptyConfiguration.Length);

            var sender = new ClusterMemberId(Random.Shared);
            var response = await SendAsync(FormatRequest(InstallSnapshotHeaders(sender, configuration.Length.ToString(), configVersion: "5"), "application/octet-stream",
                contentLength: null, Chunked([.. configuration, .. new byte[DeliveredPayloadLength]]))).WaitAsync(ReleaseDeadline, TestToken);
            TestContext.Current.TestOutputHelper?.WriteLine($"InstallSnapshot: {StatusLine(response)}, configuration {configuration.Length} bytes");

            StartsWith("HTTP/1.1 200", response);
            Equal(10L, AuditTrail(host).LastEntryIndex);
            Equal(2, (await storage.LoadConfigurationAsync(TestToken)).Members.Count);
        }
        finally
        {
            await host.StopAsync(TestToken);
        }
    }

    // A singleton protocol header that appears twice is ambiguous. The request must be rejected (non-2xx)
    // without changing the node state, even when both values are identical or one of them is unparseable.
    // "{sender}" and "{other}" stand for the sender ID and an unrelated member ID.
    [Theory(Timeout = TestTimeouts.Default)]
    [InlineData(false, "X-Raft-Term", "5", "6")]
    [InlineData(false, "X-Raft-Term", "5", "5")]
    [InlineData(false, "X-Raft-Term", "invalid", "5")]
    [InlineData(false, "X-Raft-Node-ID", "{sender}", "{other}")]
    [InlineData(false, "X-Raft-Message-Type", "AppendEntries", "Vote")]
    [InlineData(false, "X-Raft-Preceding-Record-Index", "0", "1")]
    [InlineData(false, "X-Raft-Commit-Index", "0", "1")]
    [InlineData(false, "X-Raft-Entries-Count", "0", "1")]
    [InlineData(true, "X-Raft-Config-Length", "4", "0")]
    [InlineData(true, "X-Raft-Snapshot-Index", "10", "20")]
    public static async Task DuplicateSingletonHeaderIsRejected(bool snapshot, string header, string first, string second)
    {
        var sender = new ClusterMemberId(Random.Shared);
        var other = new ClusterMemberId(Random.Shared);
        using var host = CreateHost();
        await host.StartAsync(TestToken);
        try
        {
            var log = AuditTrail(host);
            var term = log.Term;
            var response = await SendAsync(SingletonHeaderRequest(sender, snapshot, header, Substitute(first, sender, other), Substitute(second, sender, other)))
                .WaitAsync(ReleaseDeadline, TestToken);
            TestContext.Current.TestOutputHelper?.WriteLine(
                $"{header}: {first}, {second} -> {StatusLine(response)}, term {term} -> {log.Term}, last entry index {log.LastEntryIndex}");

            DoesNotContain("HTTP/1.1 2", StatusLine(response), StringComparison.Ordinal);
            Equal(term, log.Term);
            Equal(0L, log.LastEntryIndex);

            await AssertAvailableAsync(host, sender);
        }
        finally
        {
            await host.StopAsync(TestToken);
        }
    }

    // Control for the cases above: the same request with a single value of the header is accepted.
    [Theory(Timeout = TestTimeouts.Default)]
    [InlineData(false, "X-Raft-Term", "5")]
    [InlineData(false, "X-Raft-Node-ID", "{sender}")]
    [InlineData(false, "X-Raft-Message-Type", "AppendEntries")]
    [InlineData(false, "X-Raft-Preceding-Record-Index", "0")]
    [InlineData(false, "X-Raft-Commit-Index", "0")]
    [InlineData(false, "X-Raft-Entries-Count", "0")]
    [InlineData(true, "X-Raft-Config-Length", "4")]
    [InlineData(true, "X-Raft-Snapshot-Index", "10")]
    public static async Task SingleSingletonHeaderIsAccepted(bool snapshot, string header, string value)
    {
        var sender = new ClusterMemberId(Random.Shared);
        using var host = CreateHost();
        await host.StartAsync(TestToken);
        try
        {
            var response = await SendAsync(SingletonHeaderRequest(sender, snapshot, header, Substitute(value, sender, sender)))
                .WaitAsync(ReleaseDeadline, TestToken);
            TestContext.Current.TestOutputHelper?.WriteLine($"{header}: {value} -> {StatusLine(response)}");

            StartsWith("HTTP/1.1 200", response);
            Equal(snapshot ? 10L : 0L, AuditTrail(host).LastEntryIndex);
        }
        finally
        {
            await host.StopAsync(TestToken);
        }
    }

    // The same rule applies to the headers of a multipart log entry section.
    [Theory(Timeout = TestTimeouts.Default)]
    [InlineData("X-Raft-Record-Term", "5", "1")]
    [InlineData("X-Raft-Configuration", "false", "true")]
    public static async Task DuplicateEntrySectionHeaderIsRejected(string header, string first, string second)
    {
        using var host = CreateHost();
        await host.StartAsync(TestToken);
        try
        {
            var log = AuditTrail(host);
            var sender = new ClusterMemberId(Random.Shared);
            var response = await SendAsync(EntrySectionRequest(sender, header, first, second)).WaitAsync(ReleaseDeadline, TestToken);
            TestContext.Current.TestOutputHelper?.WriteLine(
                $"{header}: {first}, {second} -> {StatusLine(response)}, last entry index {log.LastEntryIndex}");

            DoesNotContain("HTTP/1.1 2", StatusLine(response), StringComparison.Ordinal);
            Equal(0L, log.LastEntryIndex);
            await AssertAvailableAsync(host, sender);
        }
        finally
        {
            await host.StopAsync(TestToken);
        }
    }

    // Control for the cases above: the same entry with a single value of the section header is accepted.
    [Theory(Timeout = TestTimeouts.Default)]
    [InlineData("X-Raft-Record-Term", "5")]
    [InlineData("X-Raft-Configuration", "false")]
    public static async Task SingleEntrySectionHeaderIsAccepted(string header, string value)
    {
        using var host = CreateHost();
        await host.StartAsync(TestToken);
        try
        {
            var sender = new ClusterMemberId(Random.Shared);
            var response = await SendAsync(EntrySectionRequest(sender, header, value)).WaitAsync(ReleaseDeadline, TestToken);
            TestContext.Current.TestOutputHelper?.WriteLine($"{header}: {value} -> {StatusLine(response)}");

            StartsWith("HTTP/1.1 200", response);
            Equal(1L, AuditTrail(host).LastEntryIndex);
        }
        finally
        {
            await host.StopAsync(TestToken);
        }
    }

    // The leader parses the headers of the follower's response with the same rule.
    [Theory(Timeout = TestTimeouts.Default)]
    [InlineData("X-Raft-Term", "5", "6")]
    [InlineData("X-Raft-Term", "5", "5")]
    [InlineData("X-Raft-Term", "invalid", "5")]
    [InlineData("X-Raft-Last-Index", "10", "20")]
    [InlineData("X-Raft-Last-Index", "10", "10")]
    public static async Task DuplicateResponseHeaderIsRejected(string header, string first, string second)
        => await ThrowsAsync<RaftProtocolException>(ParseAppendEntriesResponseAsync(header, first, second));

    // Control for the cases above: a single value of the response header is parsed.
    [Theory(Timeout = TestTimeouts.Default)]
    [InlineData("X-Raft-Term", "5")]
    [InlineData("X-Raft-Last-Index", "42")]
    public static async Task SingleResponseHeaderIsAccepted(string header, string value)
    {
        var result = await ParseAppendEntriesResponseAsync(header, value);

        Equal(header is "X-Raft-Term" ? 5L : 1L, result.Term);
        Equal(header is "X-Raft-Last-Index" ? 42L : 10L, result.Value.LastIndex);
    }
    // The memory reserved for the received configuration must be proportional to the bytes received,
    // not to the declared X-Raft-Config-Length. The claims use distinct power-of-two sizes,
    // so a buffer returned to ArrayPool.Shared by one case cannot hide the allocation of another.
    [Theory(Timeout = TestTimeouts.Default)]
    [InlineData(false, 1 << 27)]
    [InlineData(true, 1 << 28)]
    public static async Task DeclaredConfigurationLengthDoesNotDriveAllocation(bool chunked, int declaredLength)
    {
        const long AllocationBudget = 32L << 20;

        using var host = CreateHost();
        await host.StartAsync(TestToken);
        try
        {
            var sender = new ClusterMemberId(Random.Shared);
            var delivered = new byte[8];
            var request = FormatRequest(InstallSnapshotHeaders(sender, declaredLength.ToString()), "application/octet-stream",
                chunked ? null : delivered.Length, chunked ? Chunked(delivered) : delivered);

            var before = GC.GetTotalAllocatedBytes(precise: true);
            var response = await SendAsync(request).WaitAsync(ReleaseDeadline, TestToken);
            var allocated = GC.GetTotalAllocatedBytes(precise: true) - before;
            TestContext.Current.TestOutputHelper?.WriteLine($"InstallSnapshot: {StatusLine(response)}, declared {declaredLength} bytes, delivered {delivered.Length} bytes, allocated {allocated} bytes");

            DoesNotMatch(@"^HTTP/1\.1 2\d\d", response);
            True(allocated < AllocationBudget, $"Allocated {allocated} bytes for {delivered.Length} delivered bytes");

            await AssertAvailableAsync(host, sender);
        }
        finally
        {
            await host.StopAsync(TestToken);
        }
    }
    // A snapshot configuration whose member count is negative or exceeds the payload is not a valid configuration.
    // It must not replace the applied configuration, which stays decodable, and the node keeps serving requests.
    [Theory(Timeout = TestTimeouts.Default)]
    [InlineData(-1)]
    [InlineData(1000)]
    public static async Task MalformedSnapshotConfigurationIsNotApplied(int memberCount)
    {
        using var host = CreateHost();
        await host.StartAsync(TestToken);
        try
        {
            var storage = host.Services.GetRequiredService<IClusterConfigurationStorage<UriEndPoint>>();
            var (_, versionBefore) = await ((IClusterConfigurationStorage)storage).LoadConfigurationAsync(TestToken);
            Equal(2, (await storage.LoadConfigurationAsync(TestToken)).Members.Count);

            var configuration = new byte[sizeof(int)];
            BinaryPrimitives.WriteInt32LittleEndian(configuration, memberCount);
            var sender = new ClusterMemberId(Random.Shared);
            var response = await SendAsync(FormatRequest(InstallSnapshotHeaders(sender, configuration.Length.ToString(), configVersion: "5"),
                "application/octet-stream", contentLength: null, Chunked([.. configuration, .. new byte[DeliveredPayloadLength]])))
                .WaitAsync(ReleaseDeadline, TestToken);

            var memberCountAfter = -1;
            var versionAfter = -1L;
            var load = await Record.ExceptionAsync(async () =>
            {
                memberCountAfter = (await storage.LoadConfigurationAsync(TestToken)).Members.Count;
                versionAfter = (await ((IClusterConfigurationStorage)storage).LoadConfigurationAsync(TestToken)).Version;
            });
            TestContext.Current.TestOutputHelper?.WriteLine(
                $"InstallSnapshot: {StatusLine(response)}, applied configuration: {memberCountAfter} members, version {versionBefore} -> {versionAfter}, load: {load?.GetType().Name ?? "OK"}");

            Null(load);
            Equal(2, memberCountAfter);
            Equal(versionBefore, versionAfter);
            StartsWith("HTTP/1.1 200", await SendAsync(HeartbeatRequest(sender)).WaitAsync(ReleaseDeadline, TestToken));
        }
        finally
        {
            await host.StopAsync(TestToken);
        }
    }

    // A declared entry count beyond the delivered entries must not make the follower commit or acknowledge
    // entries it has not received; the node stays available. The follower has an uncommitted tail that the leader
    // has not confirmed, so a commit index derived from the declared count would commit it.
    [Theory(Timeout = TestTimeouts.Default)]
    [InlineData(true, true)]
    [InlineData(true, false)]
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

    private static string Substitute(string value, ClusterMemberId sender, ClusterMemberId other) => value
        .Replace("{sender}", sender.ToString(), StringComparison.Ordinal)
        .Replace("{other}", other.ToString(), StringComparison.Ordinal);

    // the request with the header repeated for every value
    private static byte[] SingletonHeaderRequest(ClusterMemberId sender, bool snapshot, string header, params string[] values)
    {
        var headers = (snapshot ? InstallSnapshotHeaders(sender, EmptyConfiguration.Length.ToString()) : AppendEntriesHeaders(sender, entriesCount: 0L))
            .Where(h => !string.Equals(h.Item1, header, StringComparison.OrdinalIgnoreCase))
            .Concat(values.Select(v => (header, v)));

        return snapshot
            ? FormatRequest(headers, "application/octet-stream", contentLength: null, Chunked([.. EmptyConfiguration, .. new byte[DeliveredPayloadLength]]))
            : FormatRequest(headers, contentType: null, contentLength: 0L, []);
    }

    // AppendEntries with one multipart entry section, whose header is repeated for every value
    private static byte[] EntrySectionRequest(ClusterMemberId sender, string header, params string[] values)
    {
        const string Boundary = "entries";
        var sectionHeaders = new List<(string Name, string Value)>
        {
            ("X-Raft-Record-Term", SenderTerm.ToString()),
            ("X-Raft-Configuration", "false"),
        };
        sectionHeaders.RemoveAll(h => h.Name == header);
        sectionHeaders.AddRange(values.Select(v => (header, v)));

        byte[] body = [
            .. Encoding.ASCII.GetBytes($"--{Boundary}\r\n{string.Concat(sectionHeaders.Select(static h => $"{h.Name}: {h.Value}\r\n"))}\r\n"),
            .. new byte[DeliveredPayloadLength],
            .. Encoding.ASCII.GetBytes($"\r\n--{Boundary}--\r\n"),
        ];
        return FormatRequest(AppendEntriesHeaders(sender, entriesCount: 1L), $"multipart/mixed; boundary=\"{Boundary}\"", body.Length, body);
    }

    // parses a follower's response to AppendEntries that has the given header values (replaces the default value of that header)
    private static async Task<Result<ReplicationStatus>> ParseAppendEntriesResponseAsync(string header, params string[] values)
    {
        var assembly = typeof(RequestJournalConfiguration).Assembly;
        var messageType = assembly.GetType("DotNext.Net.Cluster.Consensus.Raft.Http.AppendEntriesMessage`2", throwOnError: true)!
            .MakeGenericType(typeof(EmptyLogEntry), typeof(EmptyLogEntry[]));
        var contract = assembly.GetType("DotNext.Net.Cluster.Consensus.Raft.Http.IHttpMessage`1", throwOnError: true)!
            .MakeGenericType(typeof(Result<ReplicationStatus>));
        var map = messageType.GetInterfaceMap(contract);
        var parseResponse = map.TargetMethods[Array.FindIndex(map.InterfaceMethods, static m => m.Name is "ParseResponseAsync")];
        var message = messageType.GetConstructor(
            BindingFlags.Instance | BindingFlags.NonPublic,
            [typeof(ClusterMemberId), typeof(long), typeof(long), typeof(long), typeof(long), typeof(EmptyLogEntry[]), typeof(int)])!
            .Invoke([new ClusterMemberId(Random.Shared), 1L, 10L, 1L, 10L, Array.Empty<EmptyLogEntry>(), 0]);

        using var response = new HttpResponseMessage(HttpStatusCode.OK)
        {
            Content = new StringContent(nameof(HeartbeatResult.Replicated)),
        };

        if (header is not "X-Raft-Term")
            response.Headers.Add("X-Raft-Term", "1");

        foreach (var value in values)
            response.Headers.Add(header, value);

        return await (Task<Result<ReplicationStatus>>)parseResponse.Invoke(message, [response, TestToken])!;
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

    // one entry (or a snapshot) announcing 1 KiB of payload, but only 16 bytes are sent
    private static byte[] PartialRequest(ClusterMemberId sender, bool snapshot)
    {
        if (snapshot)
        {
            return FormatRequest(InstallSnapshotHeaders(sender, configLength: "0"), "application/octet-stream",
                contentLength: DeclaredPayloadLength, new byte[DeliveredPayloadLength]);
        }

        var body = new byte[MetadataSize + DeliveredPayloadLength];
        WriteMetadata(body, SenderTerm, DeclaredPayloadLength);
        return FormatRequest(AppendEntriesHeaders(sender, entriesCount: 1), "application/octet-stream",
            contentLength: MetadataSize + DeclaredPayloadLength, body);
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

    private static void Reset(Socket socket)
    {
        socket.LingerState = new(true, 0);
        socket.Close();
    }

    private static IHost CreateHost(bool useWal = true, bool kestrelMinDataRate = true, TimeSpan? requestTimeout = null) => new HostBuilder()
        .ConfigureWebHost(webHost =>
        {
            webHost.UseKestrel(options =>
            {
                options.ListenLocalhost(Port);
                if (!kestrelMinDataRate)
                    options.Limits.MinRequestBodyDataRate = null;
            });

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
            ["requestTimeout"] = (requestTimeout ?? RequestTimeout).ToString(),
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

    // the status line that the server sends on a connection, or how the server closed the connection
    private static async Task<string> ReceiveOutcomeAsync(Socket socket)
    {
        var buffer = new byte[4096];
        int count;
        try
        {
            count = await socket.ReceiveAsync(buffer, SocketFlags.None, TestToken);
        }
        catch (SocketException e)
        {
            return $"connection {e.SocketErrorCode}";
        }

        return count is 0 ? "connection closed" : StatusLine(Encoding.ASCII.GetString(buffer, 0, count));
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
