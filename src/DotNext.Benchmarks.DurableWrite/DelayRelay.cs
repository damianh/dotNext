using System.Buffers;
using System.Net;
using System.Net.Sockets;

namespace DotNext.Benchmarks.DurableWrite;

/// <summary>
/// A loopback TCP relay in front of one node. Every chunk it forwards, in either direction, is delayed, and
/// <see cref="Pause"/> holds all traffic for a while, so the node falls behind the leader.
/// </summary>
/// <remarks>
/// The other nodes know the slow node by the address of the relay, so every request to it and every response from it
/// goes through the relay. Nothing leaves the loopback interface.
/// </remarks>
internal sealed class DelayRelay : IAsyncDisposable
{
    private const int BufferSize = 64 * 1024;

    private readonly TcpListener listener;
    private readonly IPEndPoint target;
    private readonly TimeSpan delay;
    private readonly CancellationTokenSource lifetime = new();
    private readonly List<Task> pumps = [];
    private readonly Lock sync = new();
    private Task? acceptLoop;
    private long pausedUntil, chunks, bytes;
    private int connections;

    internal DelayRelay(IPEndPoint target, TimeSpan delay)
    {
        this.target = target;
        this.delay = delay;
        listener = new(IPAddress.Loopback, 0);
    }

    internal IPEndPoint EndPoint => (IPEndPoint)listener.LocalEndpoint;

    internal long Chunks => Interlocked.Read(ref chunks);

    internal long Bytes => Interlocked.Read(ref bytes);

    internal int Connections => Volatile.Read(in connections);

    internal void Start()
    {
        listener.Start();
        acceptLoop = AcceptAsync(lifetime.Token);
    }

    /// <summary>
    /// Holds all traffic through the relay for <paramref name="duration"/>.
    /// </summary>
    internal void Pause(TimeSpan duration)
        => Interlocked.Exchange(ref pausedUntil, Environment.TickCount64 + (long)duration.TotalMilliseconds);

    private async Task AcceptAsync(CancellationToken token)
    {
        while (!token.IsCancellationRequested)
        {
            Socket inbound;
            try
            {
                inbound = await listener.AcceptSocketAsync(token).ConfigureAwait(false);
            }
            catch (Exception e) when (e is OperationCanceledException or ObjectDisposedException or SocketException)
            {
                break;
            }

            Interlocked.Increment(ref connections);
            lock (sync)
            {
                pumps.RemoveAll(static t => t.IsCompleted);
                pumps.Add(ConnectAsync(inbound, token));
            }
        }
    }

    private async Task ConnectAsync(Socket inbound, CancellationToken token)
    {
        using (inbound)
        {
            using var outbound = new Socket(target.AddressFamily, SocketType.Stream, ProtocolType.Tcp) { NoDelay = true };
            inbound.NoDelay = true;
            try
            {
                await outbound.ConnectAsync(target, token).ConfigureAwait(false);
            }
            catch (Exception e) when (e is OperationCanceledException or SocketException)
            {
                return;
            }

            using var connection = CancellationTokenSource.CreateLinkedTokenSource(token);
            var forward = PumpAsync(inbound, outbound, connection);
            var backward = PumpAsync(outbound, inbound, connection);
            await Task.WhenAll(forward, backward).ConfigureAwait(false);
        }
    }

    private async Task PumpAsync(Socket source, Socket destination, CancellationTokenSource connection)
    {
        var token = connection.Token;
        var buffer = ArrayPool<byte>.Shared.Rent(BufferSize);
        try
        {
            while (true)
            {
                var count = await source.ReceiveAsync(buffer, SocketFlags.None, token).ConfigureAwait(false);
                if (count is 0)
                    break;

                await Task.Delay(delay, token).ConfigureAwait(false);
                for (long remaining; (remaining = Interlocked.Read(ref pausedUntil) - Environment.TickCount64) > 0L;)
                    await Task.Delay(TimeSpan.FromMilliseconds(long.Min(remaining, 100L)), token).ConfigureAwait(false);

                for (var offset = 0; offset < count;)
                    offset += await destination.SendAsync(buffer.AsMemory(offset, count - offset), SocketFlags.None, token).ConfigureAwait(false);

                Interlocked.Increment(ref chunks);
                Interlocked.Add(ref bytes, count);
            }

            destination.Shutdown(SocketShutdown.Send);
        }
        catch (Exception e) when (e is OperationCanceledException or SocketException or ObjectDisposedException)
        {
            // the connection or the relay is closed
        }
        finally
        {
            ArrayPool<byte>.Shared.Return(buffer);
            connection.Cancel();
        }
    }

    public async ValueTask DisposeAsync()
    {
        await lifetime.CancelAsync().ConfigureAwait(false);
        listener.Stop();
        if (acceptLoop is not null)
            await acceptLoop.ConfigureAwait(false);

        Task[] running;
        lock (sync)
            running = pumps.ToArray();

        await Task.WhenAll(running).ConfigureAwait(false);
        listener.Dispose();
        lifetime.Dispose();
    }
}
