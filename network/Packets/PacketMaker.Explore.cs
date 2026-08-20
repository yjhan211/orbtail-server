using MessagePack;
using network.common;
using network.common.data.models;

namespace network.packets;

// ========== 탐색 프로토콜 ==========

public static partial class PacketMaker
{
    public static Packet G_TO_C_EXPLORE_START(long playerId, int interactId)
    {
        var packet = Packet.Create((int)Protocol.G_TO_C_EXPLORE_START);
        G_TO_C_EXPLORE_START body = new()
        {
            PlayerId = playerId,
            InteractId = interactId
        };

        packet.SetBody(MessagePackSerializer.Serialize(body));
        return packet;
    }

    public static Packet G_TO_C_EXPLORE_END(long playerId)
    {
        var packet = Packet.Create((int)Protocol.G_TO_C_EXPLORE_END);
        G_TO_C_EXPLORE_END body = new()
        {
            PlayerId = playerId
        };

        packet.SetBody(MessagePackSerializer.Serialize(body));
        return packet;
    }

    public static Packet G_TO_C_INTERACTABLE_STATE_CHANGE(int interactId, int newState)
    {
        var packet = Packet.Create((int)Protocol.G_TO_C_INTERACTABLE_STATE_CHANGE);
        G_TO_C_INTERACTABLE_STATE_CHANGE body = new()
        {
            InteractId = interactId,
            NewState = newState
        };

        packet.SetBody(MessagePackSerializer.Serialize(body));
        return packet;
    }
}
