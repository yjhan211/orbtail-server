using MessagePack;
using network.common;
using network.common.data.models;

namespace network.packets;

// ========== 인게임 인벤토리 프로토콜 ==========

public static partial class PacketMaker
{
    public static Packet G_TO_C_INGAME_INVENTORY_LIST(List<InGameItemInfo> items)
    {
        var packet = Packet.Create((int)Protocol.G_TO_C_INGAME_INVENTORY_LIST);
        G_TO_C_INGAME_INVENTORY_LIST body = new() { Items = items };

        packet.SetBody(MessagePackSerializer.Serialize(body));
        return packet;
    }

    public static Packet G_TO_C_INGAME_INVENTORY_UPDATE(List<InGameItemInfo> items)
    {
        var packet = Packet.Create((int)Protocol.G_TO_C_INGAME_INVENTORY_UPDATE);
        G_TO_C_INGAME_INVENTORY_UPDATE body = new() { Items = items };

        packet.SetBody(MessagePackSerializer.Serialize(body));
        return packet;
    }

    public static Packet G_TO_C_USE_INGAME_ITEM_RESULT(bool success, long itemUid, ErrorCode errorCode, int ruleId = 0)
    {
        var packet = Packet.Create((int)Protocol.G_TO_C_USE_INGAME_ITEM_RESULT);
        G_TO_C_USE_INGAME_ITEM_RESULT body = new()
        {
            Success = success,
            ItemUid = itemUid,
            ErrorCode = errorCode,
            RuleId = ruleId
        };

        packet.SetBody(MessagePackSerializer.Serialize(body));
        return packet;
    }
}
