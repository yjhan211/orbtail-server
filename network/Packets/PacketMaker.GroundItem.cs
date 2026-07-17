using MessagePack;
using network.common;
using network.common.data.models;

namespace network.packets;

public static partial class PacketMaker
{
    public static Packet G_TO_C_GROUND_ITEM_SNAPSHOT(int areaType, int remainingNaturalStock,
        List<GroundItemInfo> items)
    {
        var packet = Packet.Create((int)Protocol.G_TO_C_GROUND_ITEM_SNAPSHOT);
        packet.SetBody(MessagePackSerializer.Serialize(new G_TO_C_GROUND_ITEM_SNAPSHOT
        {
            AreaType = areaType,
            RemainingNaturalStock = remainingNaturalStock,
            Items = items
        }));
        return packet;
    }

    public static Packet G_TO_C_GROUND_ITEM_SPAWN(int areaType, int remainingNaturalStock,
        List<GroundItemInfo> items)
    {
        var packet = Packet.Create((int)Protocol.G_TO_C_GROUND_ITEM_SPAWN);
        packet.SetBody(MessagePackSerializer.Serialize(new G_TO_C_GROUND_ITEM_SPAWN
        {
            AreaType = areaType,
            RemainingNaturalStock = remainingNaturalStock,
            Items = items
        }));
        return packet;
    }

    public static Packet G_TO_C_GROUND_ITEM_REMOVED(long groundItemUid, long pickerPlayerId, bool autoUsed)
    {
        var packet = Packet.Create((int)Protocol.G_TO_C_GROUND_ITEM_REMOVED);
        packet.SetBody(MessagePackSerializer.Serialize(new G_TO_C_GROUND_ITEM_REMOVED
        {
            GroundItemUid = groundItemUid,
            PickerPlayerId = pickerPlayerId,
            AutoUsed = autoUsed
        }));
        return packet;
    }

    public static Packet G_TO_C_GROUND_ITEM_PICKUP_RESULT(long groundItemUid, int itemId, bool success,
        bool autoUsed, ErrorCode errorCode)
    {
        var packet = Packet.Create((int)Protocol.G_TO_C_GROUND_ITEM_PICKUP_RESULT);
        packet.SetBody(MessagePackSerializer.Serialize(new G_TO_C_GROUND_ITEM_PICKUP_RESULT
        {
            GroundItemUid = groundItemUid,
            ItemId = itemId,
            Success = success,
            AutoUsed = autoUsed,
            ErrorCode = errorCode
        }));
        return packet;
    }
}
