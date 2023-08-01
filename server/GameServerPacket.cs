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

        static Packet MakeSpawnPlayerListPacket(List<PlayerObj> player_list)
        {
            Packet packet = Packet.Create(PROTOCOL.S_TO_C_PLAYER_SPAWN_LIST);
            S_TO_C_PLAYER_SPAWN_LIST body = new() { player_list = player_list };
            packet.SetBody(MessagePackSerializer.Serialize(body));

            return packet;
        }

        static Packet MakeDistroyPlayerListPacket(List<PlayerObj> player_list)
        {
            Packet packet = Packet.Create(PROTOCOL.S_TO_C_PLAYER_DESTROY_LIST);
            S_TO_C_PLAYER_DESTROY_LIST body = new() { player_list = player_list };
            packet.SetBody(MessagePackSerializer.Serialize(body));

            return packet;
        }

        static Packet MakeChatPacket(Player player, string chat_message)
        {
            Packet packet = Packet.Create(PROTOCOL.S_TO_C_CHAT_MSG_ALL);
            S_TO_C_CHAT_MSG_ALL body =
                new() { player_id = player.player_id, chat_message = chat_message };
            packet.SetBody(MessagePackSerializer.Serialize(body));

            return packet;
        }

        public static Packet MakeMovePacket(Player player)
        {
            Packet packet = Packet.Create(PROTOCOL.S_TO_C_MOVE_ALL);
            S_TO_C_MOVE_ALL body =
                new()
                {
                    player_id = player.player_id,
                    current_cell = player.current_cell,
                    target_cell = player.target_cell,
                    move_timestamp = player.move_timestamp,
                    is_flip = player.is_flip,
                };
            packet.SetBody(MessagePackSerializer.Serialize(body));

            return packet;
        }

        public static Packet MakeMoveListPacket(List<S_TO_C_MOVE_ALL> move_obj_list)
        {
            Packet packet = Packet.Create(PROTOCOL.S_TO_C_MOVE_ALL_LIST);

            S_TO_C_MOVE_ALL_LIST body = new() { move_obj_list = move_obj_list };
            packet.SetBody(MessagePackSerializer.Serialize(body));

            return packet;
        }
    }
}
