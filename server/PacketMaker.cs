using network;

namespace game_server
{
    using MessagePack;

    public static class PacketMaker
    {
        public static Packet MakeLoginPacket(PlayerInfo player_info)
        {
            Packet packet = Packet.Create((int)PROTOCOL.S_TO_C_LOGIN);
            S_TO_C_LOGIN body =
                new() { object_info = player_info.object_info, player_info = player_info };

            packet.SetBody(MessagePackSerializer.Serialize(body));
            return packet;
        }

        public static Packet MakeMapInfoPacket(
            List<GameObjectInfo> game_object_list,
            List<string> delete_object_list,
            bool send_delete_list
        )
        {
            Packet packet = Packet.Create((int)PROTOCOL.S_TO_C_MAP_INFO);
            S_TO_C_MAP_INFO body =
                new()
                {
                    object_list = game_object_list,
                    delete_object_list = send_delete_list ? delete_object_list : new()
                };

            packet.SetBody(MessagePackSerializer.Serialize(body));
            return packet;
        }

        public static Packet MakePlayerInfoPacket(List<PlayerInfo> player_info_list)
        {
            Packet packet = Packet.Create((int)PROTOCOL.S_TO_C_PLAYER_INFO);
            S_TO_C_PLAYER_INFO body = new() { player_info_list = player_info_list };

            packet.SetBody(MessagePackSerializer.Serialize(body));
            return packet;
        }

        static Packet MakeChatPacket(PlayerInfo player, string chat_message)
        {
            Packet packet = Packet.Create((int)PROTOCOL.S_TO_C_CHAT_MSG_ALL);
            S_TO_C_CHAT_MSG_ALL body =
                new() { player_id = player.player_id, chat_message = chat_message };

            packet.SetBody(MessagePackSerializer.Serialize(body));
            return packet;
        }

        public static Packet MakeBoundTilePacket(List<BoundTile> tile_list)
        {
            Packet packet = Packet.Create((int)PROTOCOL.S_TO_C_BOUND_TILE_INFO);
            S_TO_C_BOUND_TILE_INFO body = new() { tile_list = tile_list };

            packet.SetBody(MessagePackSerializer.Serialize(body));
            return packet;
        }
    }
}
