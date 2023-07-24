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

        static Packet MakeSpawnPlayerPacket(Player player)
        {
            Packet packet = Packet.Create(PROTOCOL.S_TO_C_PLAYER_SPAWN);
            S_TO_C_PLAYER_SPAWN body = new() { player = player.ConvertObj() };
            packet.SetBody(MessagePackSerializer.Serialize(body));

            return packet;
        }

        static Packet MakeDistroyPlayerPacket(Player player)
        {
            Packet packet = Packet.Create(PROTOCOL.S_TO_C_PLAYER_DISTROY);
            S_TO_C_PLAYER_DISTROY body = new() { player = player.ConvertObj() };
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

        public static Packet MakeMovePacket(
            Player player,
            CellPosition current_cell,
            CellPosition target_cell,
            DateTime move_timestamp,
            bool is_flip
        )
        {
            Packet packet = Packet.Create(PROTOCOL.S_TO_C_MOVE_ALL);
            S_TO_C_MOVE_ALL body =
                new()
                {
                    player_id = player.player_id,
                    current_cell = current_cell,
                    target_cell = target_cell,
                    move_timestamp = move_timestamp,
                    is_flip = is_flip,
                };
            packet.SetBody(MessagePackSerializer.Serialize(body));

            return packet;
        }
    }
}
