using MessagePack;
using network.common;
using network.common.data.models;

namespace network.packets;

// ========== 플레이어 상태/스탯 프로토콜 ==========

public static partial class PacketMaker
{
    public static Packet G_TO_C_PLAYER_STATE(long playerId, PlayerState state)
    {
        var packet = Packet.Create((int)Protocol.G_TO_C_PLAYER_STATE);
        G_TO_C_PLAYER_STATE body = new()
        {
            PlayerId = playerId,
            State = state
        };

        packet.SetBody(MessagePackSerializer.Serialize(body));
        return packet;
    }

    public static Packet G_TO_C_PLAYER_STATS_UPDATE(int health, int healthDelta)
    {
        var packet = Packet.Create((int)Protocol.G_TO_C_PLAYER_STATS_UPDATE);
        G_TO_C_PLAYER_STATS_UPDATE body = new()
        {
            Health = health,
            HealthDelta = healthDelta
        };

        packet.SetBody(MessagePackSerializer.Serialize(body));
        return packet;
    }
}
