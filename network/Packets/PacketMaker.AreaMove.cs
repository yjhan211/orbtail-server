using MessagePack;
using network.common;
using network.common.data.models;

namespace network.packets;

// ========== 구역 이동 프로토콜 (GDD v0.0.8) ==========

public static partial class PacketMaker
{
    public static Packet G_TO_C_AREA_MOVE_RESULT(ErrorCode errorCode, AreaType area, int spawnCellX, int spawnCellY,
        int staminaCost, int remainingStamina)
    {
        var packet = Packet.Create((int)Protocol.G_TO_C_AREA_MOVE_RESULT);
        G_TO_C_AREA_MOVE_RESULT body = new()
        {
            ErrorCode = errorCode,
            Area = area,
            SpawnCellX = spawnCellX,
            SpawnCellY = spawnCellY,
            StaminaCost = staminaCost,
            RemainingStamina = remainingStamina
        };
        packet.SetBody(MessagePackSerializer.Serialize(body));
        return packet;
    }

    public static Packet G_TO_C_ROOM_ENTRY_EVENT(int eventId)
    {
        var packet = Packet.Create((int)Protocol.G_TO_C_ROOM_ENTRY_EVENT);
        G_TO_C_ROOM_ENTRY_EVENT body = new()
        {
            EventId = eventId
        };
        packet.SetBody(MessagePackSerializer.Serialize(body));
        return packet;
    }
}
