using MessagePack;
using network.common;
using network.common.data.models;

namespace network.packets;

// ========== 보유 오브 동기화 프로토콜 ==========

public static partial class PacketMaker
{
    public static Packet G_TO_C_ORB_LIST(List<InGameItemInfo> items)
    {
        var packet = Packet.Create((int)Protocol.G_TO_C_ORB_LIST);
        G_TO_C_ORB_LIST body = new() { Items = items };

        packet.SetBody(MessagePackSerializer.Serialize(body));
        return packet;
    }

    public static Packet G_TO_C_ORB_UPDATE(List<InGameItemInfo> items)
    {
        var packet = Packet.Create((int)Protocol.G_TO_C_ORB_UPDATE);
        G_TO_C_ORB_UPDATE body = new() { Items = items };

        packet.SetBody(MessagePackSerializer.Serialize(body));
        return packet;
    }


}
