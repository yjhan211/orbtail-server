using network;

namespace game_server
{
    using MessagePack;

    public partial class GameServer
    {
        public static Packet MakeLoginPacket(Player player)
        {
            Packet packet = Packet.Create(PROTOCOL.S_TO_C_LOGIN);
            S_TO_C_LOGIN body =
                new()
                {
                    object_msg = player.ParseObjectMsg(),
                    player_msg = player.ParsePlayerMsg(),
                    name = player.name
                };
            packet.SetBody(MessagePackSerializer.Serialize(body));

            return packet;
        }

        public static Packet MakeMapInfoPacket(
            List<GameObjectMsg> game_object_list,
            List<long> delete_object_list,
            bool send_delete_list
        )
        {
            Packet packet = Packet.Create(PROTOCOL.S_TO_C_MAP_INFO);
            S_TO_C_MAP_INFO body =
                new()
                {
                    object_list = game_object_list,
                    delete_object_list = send_delete_list ? delete_object_list : new()
                };

            packet.SetBody(MessagePackSerializer.Serialize(body));
            return packet;
        }

        public static Packet MakePlayerInfoPacket(List<PlayerMsg> player_msg_list)
        {
            Packet packet = Packet.Create(PROTOCOL.S_TO_C_PLAYER_INFO);
            S_TO_C_PLAYER_INFO body = new() { player_msg = player_msg_list };

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
