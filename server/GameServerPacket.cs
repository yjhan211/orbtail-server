using network;

namespace game_server
{
    using MessagePack;

    public partial class GameServer
    {
        public static Packet MakeLoginPacket(Player player)
        {
            Packet packet = Packet.Create(PROTOCOL.S_TO_C_LOGIN);
            S_TO_C_LOGIN body = new() { player = player.ConvertObj() };
            packet.SetBody(MessagePackSerializer.Serialize(body));

            return packet;
        }

        public static Packet MakeMapInfoObject(List<MapObject> map_object_list)
        {
            Packet packet = Packet.Create(PROTOCOL.S_TO_C_MAP_INFO);
            S_TO_C_MAP_INFO body = new() { map_object_list = map_object_list };
            packet.SetBody(MessagePackSerializer.Serialize(body));

            return packet;
        }

        static Packet MakeChatPacket(Player player, string chat_message)
        {
            Packet packet = Packet.Create(PROTOCOL.S_TO_C_CHAT_MSG_ALL);
            S_TO_C_CHAT_MSG_ALL body =
                new() { player_id = player.object_id, chat_message = chat_message };
            packet.SetBody(MessagePackSerializer.Serialize(body));

            return packet;
        }

        public static Packet MakeBoundTilePacket(List<BoundTile> tile_list)
        {
            Packet packet = Packet.Create(PROTOCOL.S_TO_C_BOUND_TILE_INFO);

            S_TO_C_BOUND_TILE_INFO body = new() { tile_list = tile_list };
            packet.SetBody(MessagePackSerializer.Serialize(body));

            return packet;
        }
    }
}
