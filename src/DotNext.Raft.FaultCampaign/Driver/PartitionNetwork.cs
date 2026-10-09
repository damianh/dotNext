using System.Globalization;
using System.Net;
using System.Net.Sockets;

namespace DotNext.Raft.FaultCampaign.Driver;

/// <summary>
/// The network between the nodes: one userspace TCP proxy per node on loopback, in front of its Raft port. Every node
/// advertises its proxy port as its endpoint, so every Raft connection to node <c>d</c> passes through the proxy of
/// <c>d</c>, and the proxy attributes the connection to the node process that opened it.
/// </summary>
/// <remarks>
/// A cut link drops the bytes in both directions of every connection between its two nodes and keeps the connections
/// open, so a request across the cut times out as it does when a real network splits; a close or reset during the cut
/// doesn't cross it either. When the network heals, the connections that dropped bytes or a close are reset: their
/// streams lost data, so they never forward again. New connections across a cut are accepted and then dropped the same
/// way. Only the campaign's own ports are involved; nothing outside the process changes.
/// </remarks>
internal sealed class PartitionNetwork : IDisposable
{
    private readonly LinkProxy[] proxies;
    private readonly Func<int, int?> pidOf;

    // Bit (a * nodes + b) is set if the link between a and b is cut; the mask is symmetric.
    private long cut;

    /// <param name="listenPorts">Per node, the proxy port that the node advertises.</param>
    /// <param name="upstreamPorts">Per node, the port the node listens on.</param>
    /// <param name="pidOf">The process id of a node, or <see langword="null"/> if it is not running.</param>
    internal PartitionNetwork(int[] listenPorts, int[] upstreamPorts, Func<int, int?> pidOf)
    {
        if (listenPorts.Length != upstreamPorts.Length || listenPorts.Length * listenPorts.Length > 64)
            throw new ArgumentOutOfRangeException(nameof(listenPorts));

        this.pidOf = pidOf;
        proxies = new LinkProxy[listenPorts.Length];
        for (var i = 0; i < proxies.Length; i++)
            proxies[i] = new(this, i, listenPorts[i], upstreamPorts[i]);
    }

    internal int Nodes => proxies.Length;

    internal void Start()
    {
        foreach (var proxy in proxies)
            proxy.Start();
    }

    internal bool IsPartitioned => Volatile.Read(in cut) is not 0L;

    /// <summary>
    /// Cuts every link of <paramref name="node"/>, except the link to <paramref name="leak"/>.
    /// </summary>
    /// <returns>The peers that lost contact with the node.</returns>
    internal int[] Isolate(int node, int? leak = null)
    {
        var peers = Enumerable.Range(0, Nodes).Where(p => p != node && p != leak).ToArray();
        var mask = 0L;
        foreach (var peer in peers)
            mask |= Bit(node, peer) | Bit(peer, node);

        Volatile.Write(ref cut, mask);
        return peers;
    }

    /// <summary>
    /// Restores every link and resets the connections that dropped bytes.
    /// </summary>
    internal void Heal()
    {
        Interlocked.Exchange(ref cut, 0L);
        foreach (var proxy in proxies)
            proxy.ResetDropped();
    }

    // A connection whose source is unknown is treated as cut while any link is cut, so isolation never leaks.
    internal bool IsCut(int source, int destination)
    {
        var mask = Volatile.Read(in cut);
        return mask is not 0L && (source < 0 || (mask & Bit(source, destination)) is not 0L);
    }

    private long Bit(int a, int b) => 1L << ((a * Nodes) + b);

    internal ProxyStatistics Statistics => new()
    {
        Connections = proxies.Sum(static p => p.Connections),
        UnattributedConnections = proxies.Sum(static p => p.Unattributed),
        DroppedBytes = proxies.Sum(static p => p.DroppedBytes),
        ResetAtHeal = proxies.Sum(static p => p.ResetAtHeal),
    };

    // The node whose process owns the client end of a connection to a proxy, or -1.
    internal int FindSource(int clientPort, int proxyPort)
    {
        if (SocketOwner.FindInode(clientPort, proxyPort) is not { } inode)
            return -1;

        for (var i = 0; i < Nodes; i++)
        {
            if (pidOf(i) is { } pid && SocketOwner.Owns(pid, inode))
                return i;
        }

        return -1;
    }

    public void Dispose()
    {
        foreach (var proxy in proxies)
            proxy.Dispose();
    }
}

internal sealed class ProxyStatistics
{
    public long Connections { get; init; }
    public long UnattributedConnections { get; init; }
    public long DroppedBytes { get; init; }
    public long ResetAtHeal { get; init; }
}

/// <summary>
/// The inbound proxy of one node.
/// </summary>
internal sealed class LinkProxy(PartitionNetwork network, int node, int listenPort, int upstreamPort) : IDisposable
{
    private const int BufferSize = 64 * 1024;

    private readonly TcpListener listener = new(IPAddress.Loopback, listenPort);
    private readonly CancellationTokenSource stop = new();
    private readonly Lock sync = new();
    private readonly HashSet<Connection> connections = [];
    private Task? accepting;
    private long accepted, unattributed, droppedBytes, resetAtHeal;

    internal long Connections => Interlocked.Read(in accepted);

    internal long Unattributed => Interlocked.Read(in unattributed);

    internal long DroppedBytes => Interlocked.Read(in droppedBytes);

    internal long ResetAtHeal => Interlocked.Read(in resetAtHeal);

    internal void Start()
    {
        listener.Start(backlog: 64);
        accepting = AcceptAsync(stop.Token);
    }

    private async Task AcceptAsync(CancellationToken token)
    {
        while (!token.IsCancellationRequested)
        {
            Socket client;
            try
            {
                client = await listener.AcceptSocketAsync(token).ConfigureAwait(false);
            }
            catch (Exception e) when (e is OperationCanceledException or ObjectDisposedException or SocketException && token.IsCancellationRequested)
            {
                break;
            }

            Interlocked.Increment(ref accepted);
            _ = ServeAsync(client, token);
        }
    }

    private async Task ServeAsync(Socket client, CancellationToken token)
    {
        client.NoDelay = true;
        var source = network.FindSource(((IPEndPoint)client.RemoteEndPoint!).Port, listenPort);
        if (source < 0)
            Interlocked.Increment(ref unattributed);

        var upstream = new Socket(AddressFamily.InterNetwork, SocketType.Stream, ProtocolType.Tcp) { NoDelay = true };
        try
        {
            await upstream.ConnectAsync(new IPEndPoint(IPAddress.Loopback, upstreamPort), token).ConfigureAwait(false);
        }
        catch (Exception)
        {
            // The node is down: the peer sees a reset, as it sees a refused connection without the proxy.
            Abort(client);
            upstream.Dispose();
            return;
        }

        var connection = new Connection(client, upstream);
        lock (sync)
        {
            if (token.IsCancellationRequested)
            {
                connection.Abort();
                return;
            }

            connections.Add(connection);
        }

        try
        {
            await Task.WhenAll(
                PumpAsync(connection, client, upstream, source, node),
                PumpAsync(connection, upstream, client, source, node)).ConfigureAwait(false);
        }
        finally
        {
            lock (sync)
                connections.Remove(connection);

            connection.Close();
        }
    }

    private async Task PumpAsync(Connection connection, Socket from, Socket to, int source, int destination)
    {
        var buffer = new byte[BufferSize];
        try
        {
            while (true)
            {
                int count;
                try
                {
                    count = await from.ReceiveAsync(buffer, SocketFlags.None).ConfigureAwait(false);
                }
                catch (Exception e) when (e is SocketException or ObjectDisposedException or IOException)
                {
                    count = -1;
                }

                // A stream that lost bytes never forwards again, whichever order the heal and this pump run in: once the
                // link is no longer cut, it is reset. A chunk received intact before the heal but checked after it is
                // forwarded late, as TCP retransmits it when a real network heals; nothing crosses while the link is cut.
                if (network.IsCut(source, destination) || connection.Dropped)
                {
                    connection.MarkDropped();
                    if (count > 0)
                        Interlocked.Add(ref droppedBytes, count);

                    if (network.IsCut(source, destination))
                    {
                        if (count > 0)
                            continue;

                        // A close or reset doesn't cross the cut either: the other end stays open until the heal resets it.
                        await connection.Closed.ConfigureAwait(false);
                        break;
                    }

                    if (connection.Abort())
                        Interlocked.Increment(ref resetAtHeal);

                    break;
                }

                if (count < 0)
                {
                    connection.Abort();
                    break;
                }

                if (count is 0)
                {
                    to.Shutdown(SocketShutdown.Send);
                    break;
                }

                for (var sent = 0; sent < count;)
                    sent += await to.SendAsync(buffer.AsMemory(sent, count - sent), SocketFlags.None).ConfigureAwait(false);
            }
        }
        catch (Exception e) when (e is SocketException or ObjectDisposedException or IOException)
        {
            connection.Abort();
        }
    }

    internal void ResetDropped()
    {
        Connection[] dropped;
        lock (sync)
            dropped = connections.Where(static c => c.Dropped).ToArray();

        foreach (var connection in dropped)
        {
            if (connection.Abort())
                Interlocked.Increment(ref resetAtHeal);
        }
    }

    private static void Abort(Socket socket)
    {
        try
        {
            socket.LingerState = new(true, 0);
        }
        catch (Exception e) when (e is SocketException or ObjectDisposedException)
        {
            // already closed
        }

        socket.Dispose();
    }

    public void Dispose()
    {
        stop.Cancel();
        listener.Dispose();
        Connection[] open;
        lock (sync)
        {
            open = [.. connections];
            connections.Clear();
        }

        foreach (var connection in open)
            connection.Abort();

        accepting?.Wait(TimeSpan.FromSeconds(5));
        stop.Dispose();
    }

    private sealed class Connection(Socket client, Socket upstream)
    {
        private readonly TaskCompletionSource closed = new(TaskCreationOptions.RunContinuationsAsynchronously);
        private int state;
        private int dropped;

        // Bytes were dropped while a link was cut: the stream lost data and is reset when the network heals.
        internal bool Dropped => Volatile.Read(in dropped) is not 0;

        // A full fence: either the pump sees the heal, or the heal sees this flag and resets the connection.
        internal void MarkDropped() => Interlocked.Exchange(ref dropped, 1);

        internal Task Closed => closed.Task;

        internal bool Abort()
        {
            if (Interlocked.Exchange(ref state, 1) is not 0)
                return false;

            LinkProxy.Abort(client);
            LinkProxy.Abort(upstream);
            closed.TrySetResult();
            return true;
        }

        internal void Close()
        {
            if (Interlocked.Exchange(ref state, 1) is 0)
            {
                client.Dispose();
                upstream.Dispose();
                closed.TrySetResult();
            }
        }
    }
}

/// <summary>
/// Finds the process that owns the client end of a loopback TCP connection, from <c>/proc</c>: the socket inode in
/// <c>/proc/net/tcp</c>, then the descriptor that links to it in <c>/proc/&lt;pid&gt;/fd</c>. The node processes run
/// as the same user as the driver, so no privileges are needed.
/// </summary>
internal static class SocketOwner
{
    internal static long? FindInode(int localPort, int remotePort)
    {
        foreach (var table in (ReadOnlySpan<string>)["/proc/net/tcp", "/proc/net/tcp6"])
        {
            if (FindInode(table, localPort, remotePort) is { } inode)
                return inode;
        }

        return null;
    }

    private static long? FindInode(string table, int localPort, int remotePort)
    {
        string[] lines;
        try
        {
            lines = File.ReadAllLines(table);
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            return null;
        }

        return FindInode(lines, localPort, remotePort);
    }

    // Lines of /proc/net/tcp: "sl local_address rem_address st ... uid timeout inode ...", addresses as HEXIP:HEXPORT.
    internal static long? FindInode(IEnumerable<string> lines, int localPort, int remotePort)
    {
        foreach (var line in lines)
        {
            var fields = line.Split(' ', StringSplitOptions.RemoveEmptyEntries);
            if (fields.Length < 10
                || Port(fields[1]) != localPort
                || Port(fields[2]) != remotePort
                || !long.TryParse(fields[9], NumberStyles.None, CultureInfo.InvariantCulture, out var inode)
                || inode is 0L)
            {
                continue;
            }

            return inode;
        }

        return null;

        static int Port(string address)
            => address.LastIndexOf(':') is var colon and >= 0
               && int.TryParse(address.AsSpan(colon + 1), NumberStyles.AllowHexSpecifier, CultureInfo.InvariantCulture, out var port)
                ? port
                : -1;
    }

    internal static bool Owns(int pid, long inode)
    {
        var target = string.Create(CultureInfo.InvariantCulture, $"socket:[{inode}]");
        try
        {
            foreach (var fd in Directory.EnumerateFileSystemEntries(string.Create(CultureInfo.InvariantCulture, $"/proc/{pid}/fd")))
            {
                try
                {
                    if (string.Equals(new FileInfo(fd).LinkTarget, target, StringComparison.Ordinal))
                        return true;
                }
                catch (Exception e) when (e is IOException or UnauthorizedAccessException)
                {
                    // the descriptor was closed while enumerating
                }
            }
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            // the process exited
        }

        return false;
    }
}
