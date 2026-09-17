using MessagePack;
using network.common;
using network.common.data.models;
using network.packets;

namespace server_tests;

public sealed class UserServerErrorPacketTests
{
    [Fact]
    public void ErrorPacket_ContainsOnlyErrorCode()
    {
        using var packet = PacketMaker.U_TO_C_ERROR(ErrorCode.ALREADY_AUTHENTICATED);
        using var wire = Packet.Create(packet.ToBytes());
        Assert.Equal((int)Protocol.U_TO_C_ERROR, wire.PopProtocolId());
        wire.PopPlayerId();
        var error = MessagePackSerializer.Deserialize<U_TO_C_ERROR>(wire.PopBody());
        Assert.Equal(ErrorCode.ALREADY_AUTHENTICATED, error.ErrorCode);
        Assert.Null(typeof(U_TO_C_ERROR).GetProperty("Message"));
        Assert.Equal("{\"errorCode\":106}",
            MessagePackSerializer.ConvertToJson(MessagePackSerializer.Serialize(error)));
    }
}
