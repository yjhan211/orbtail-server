using network.common;
using network.packets;

namespace demo_regression_tests;

public class NetworkPacketPoolTests
{
    [Fact]
    public void CopyTo_OnlyOverwritesTheUsedPacketBytes()
    {
        using var source = Packet.Create((int)Protocol.G_TO_C_HEART_BEAT, 77);
        source.SetBody([1, 2, 3, 4]);

        var target = PacketBufferPool.Pop();
        try
        {
            Array.Fill(target.Buffer, (byte)0x5A);
            source.CopyTo(target);

            Assert.Equal(source.Position, target.Position);
            Assert.Equal((byte)0x5A, target.Buffer[target.Position]);
            Assert.Equal(source.ToBytes(), target.ToBytes());
        }
        finally
        {
            target.Dispose();
        }
    }

    [Fact]
    public void SetBody_RejectsPayloadThatDoesNotLeaveRoomForPacketMetadata()
    {
        using var packet = Packet.Create((int)Protocol.G_TO_C_HEART_BEAT);

        Assert.ThrowsAny<Exception>(() => packet.SetBody(new byte[Config.BUFFER_SIZE]));
    }

    [Fact]
    public void CreateForReading_ReturnsOnlyTheReceivedBody()
    {
        byte[] raw;
        using (var outgoing = Packet.Create((int)Protocol.G_TO_C_HEART_BEAT, 91))
        {
            outgoing.SetBody([9, 8, 7]);
            outgoing.RecordSize();
            raw = outgoing.ToBytes();
        }

        using var incoming = Packet.Create(new network.utils.Const<byte[]>(raw));

        Assert.Equal((int)Protocol.G_TO_C_HEART_BEAT, incoming.PopProtocolId());
        Assert.Equal(91, incoming.PopPlayerId());
        Assert.Equal(new byte[] { 9, 8, 7 }, incoming.PopBody());
    }
}
