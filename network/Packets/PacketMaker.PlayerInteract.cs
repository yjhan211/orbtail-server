using MessagePack;
using network.common;
using network.common.data.models;

namespace network.packets;

// ========== 플레이어 상호작용 프로토콜 ==========

public static partial class PacketMaker
{
    public static Packet G_TO_C_PLAYER_INTERACT_REQUEST(long playerId, ErrorCode errorCode)
    {
        var packet = Packet.Create((int)Protocol.G_TO_C_PLAYER_INTERACT_REQUEST);
        G_TO_C_PLAYER_INTERACT_REQUEST body = new()
        {
            PlayerId = playerId,
            ErrorCode = errorCode
        };

        packet.SetBody(MessagePackSerializer.Serialize(body));
        return packet;
    }

    public static Packet G_TO_C_PLAYER_INTERACT_RESULT(bool accepted, long playerId, ErrorCode errorCode)
    {
        var packet = Packet.Create((int)Protocol.G_TO_C_PLAYER_INTERACT_RESULT);
        G_TO_C_PLAYER_INTERACT_RESULT body = new()
        {
            Accepted = accepted,
            PlayerId = playerId,
            ErrorCode = errorCode
        };

        packet.SetBody(MessagePackSerializer.Serialize(body));
        return packet;
    }

    public static Packet G_TO_C_PLAYER_INTERACT_USE_ITEM_RESULT(bool success, ErrorCode errorCode, int itemId, long targetPlayerId = 0)
    {
        var packet = Packet.Create((int)Protocol.G_TO_C_PLAYER_INTERACT_USE_ITEM_RESULT);
        G_TO_C_PLAYER_INTERACT_USE_ITEM_RESULT body = new()
        {
            Success = success,
            ErrorCode = errorCode,
            ItemId = itemId,
            TargetPlayerId = targetPlayerId
        };

        packet.SetBody(MessagePackSerializer.Serialize(body));
        return packet;
    }

    public static Packet G_TO_C_PLAYER_INTERACT_SHARE_RULE_RESULT(bool success, ErrorCode errorCode, int ruleId, long targetPlayerId = 0, long originalDiscovererPlayerId = 0)
    {
        var packet = Packet.Create((int)Protocol.G_TO_C_PLAYER_INTERACT_SHARE_RULE_RESULT);
        G_TO_C_PLAYER_INTERACT_SHARE_RULE_RESULT body = new()
        {
            Success = success,
            ErrorCode = errorCode,
            RuleId = ruleId,
            TargetPlayerId = targetPlayerId,
            OriginalDiscovererPlayerId = originalDiscovererPlayerId
        };

        packet.SetBody(MessagePackSerializer.Serialize(body));
        return packet;
    }

    public static Packet G_TO_C_PLAYER_INTERACT_END(long playerId)
    {
        var packet = Packet.Create((int)Protocol.G_TO_C_PLAYER_INTERACT_END);
        G_TO_C_PLAYER_INTERACT_END body = new()
        {
            PlayerId = playerId
        };

        packet.SetBody(MessagePackSerializer.Serialize(body));
        return packet;
    }
}
