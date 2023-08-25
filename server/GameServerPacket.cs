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

        public static Packet MakeSpawnPlayerListPacket(List<PlayerObj> player_list)
        {
            Packet packet = Packet.Create(PROTOCOL.S_TO_C_PLAYER_SPAWN_LIST);
            S_TO_C_PLAYER_SPAWN_LIST body = new() { player_list = player_list };
            packet.SetBody(MessagePackSerializer.Serialize(body));

            return packet;
        }

        public static Packet MakeMapInfoObject(List<PlayerObj> player_list)
        {
            Packet packet = Packet.Create(PROTOCOL.S_TO_C_MAP_INFO);
            S_TO_C_MAP_INFO body = new() { player_list = player_list };
            packet.SetBody(MessagePackSerializer.Serialize(body));

            return packet;
        }

        public static Packet MakeDestroyPlayerListPacket(List<long> player_id_list)
        {
            Packet packet = Packet.Create(PROTOCOL.S_TO_C_PLAYER_DESTROY_LIST);
            S_TO_C_PLAYER_DESTROY_LIST body = new() { player_id_list = player_id_list };
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

        public static Packet MakeMoveListPacket(List<MoveObj> move_obj_list)
        {
            Packet packet = Packet.Create(PROTOCOL.S_TO_C_MOVE_LIST);

            S_TO_C_MOVE_ALL_LIST body = new() { move_obj_list = move_obj_list };
            packet.SetBody(MessagePackSerializer.Serialize(body));

            return packet;
        }
    }
}
