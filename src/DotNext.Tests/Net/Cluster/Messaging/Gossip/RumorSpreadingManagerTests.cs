using System.Buffers.Binary;
using System.Net;

namespace DotNext.Net.Cluster.Messaging.Gossip;

public sealed class RumorSpreadingManagerTests : Test
{
    [Fact]
    public static void MissingEndPoint()
    {
        var manager = new RumorSpreadingManager();
        False(manager.CheckOrder(new IPEndPoint(IPAddress.Loopback, 80), default));
    }

    [Fact]
    public static void MessageOrder()
    {
        var manager = new RumorSpreadingManager();
        var endPoint = new IPEndPoint(IPAddress.Loopback, 80);
        var id = manager.Tick();

        True(manager.TryEnableControl(endPoint));
        True(manager.CheckOrder(endPoint, id));
        False(manager.CheckOrder(endPoint, id));
        False(manager.CheckOrder(endPoint, id));

        id = manager.Tick();
        True(manager.CheckOrder(endPoint, id));

        id = manager.Tick();
        True(manager.TryDisableControl(endPoint));
        False(manager.CheckOrder(endPoint, id));
    }

    [Fact]
    public static void EarlierClockEpochIsRejected()
    {
        var manager = new RumorSpreadingManager();
        var endPoint = new IPEndPoint(IPAddress.Loopback, 80);
        var earlier = manager.Tick();
        // Model a sender initialized one millisecond later without reading the wall clock.
        Span<byte> bytes = stackalloc byte[RumorTimestamp.Size];
        earlier.Format(bytes);
        BinaryPrimitives.WriteInt64LittleEndian(bytes, BinaryPrimitives.ReadInt64LittleEndian(bytes) + 1L);
        var later = new RumorTimestamp(bytes);

        True(manager.TryEnableControl(endPoint));
        True(manager.CheckOrder(endPoint, later));
        False(manager.CheckOrder(endPoint, later));
        False(manager.CheckOrder(endPoint, earlier));
        False(manager.CheckOrder(endPoint, manager.Tick()));
        True(manager.CheckOrder(endPoint, later.Increment()));
    }
}