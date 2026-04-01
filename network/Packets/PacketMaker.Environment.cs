using MessagePack;
using network.common;
using network.common.data.models;

namespace network.packets;

// ========== 환경 오브젝트 프로토콜 ==========

public static partial class PacketMaker
{
    public static Packet G_TO_C_DOOR_STATE_UPDATE(int doorId, bool isOpen, ErrorCode errorCode = ErrorCode.SUCCESS, long openerPlayerId = 0)
    {
        var packet = Packet.Create((int)Protocol.G_TO_C_DOOR_STATE_UPDATE);
        G_TO_C_DOOR_STATE_UPDATE body = new()
        {
            DoorId = doorId,
            IsOpen = isOpen,
            ErrorCode = errorCode,
            OpenerPlayerId = openerPlayerId
        };

        packet.SetBody(MessagePackSerializer.Serialize(body));
        return packet;
    }

    public static Packet G_TO_C_DOOR_STATE_LIST(List<int> openDoorIds)
    {
        var packet = Packet.Create((int)Protocol.G_TO_C_DOOR_STATE_LIST);
        G_TO_C_DOOR_STATE_LIST body = new()
        {
            OpenDoorIds = openDoorIds
        };

        packet.SetBody(MessagePackSerializer.Serialize(body));
        return packet;
    }

    public static Packet G_TO_C_CORRIDOR_BELL(List<BellEvent> bells)
    {
        var packet = Packet.Create((int)Protocol.G_TO_C_CORRIDOR_BELL);
        G_TO_C_CORRIDOR_BELL body = new()
        {
            Bells = bells
        };

        packet.SetBody(MessagePackSerializer.Serialize(body));
        return packet;
    }
}
