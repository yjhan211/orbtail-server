namespace user_server
{
    using MessagePack;
    using network;

    public static class PacketMaker
    {
        public static Packet U_TO_C_LOGIN(PlayerInfo player_info)
        {
            Packet packet = Packet.Create((int)PROTOCOL.U_TO_C_LOGIN);
            U_TO_C_LOGIN body =
                new() { object_info = player_info.object_info, player_info = player_info };

            packet.SetBody(MessagePackSerializer.Serialize(body));
            return packet;
        }

        public static Packet U_TO_C_MAP_INFO(List<string> object_key_list, bool is_ended)
        {
            Packet packet = Packet.Create((int)PROTOCOL.U_TO_C_MAP_INFO);
            U_TO_C_MAP_INFO body = new() { object_key_list = object_key_list, is_ended = is_ended };

            packet.SetBody(MessagePackSerializer.Serialize(body));
            return packet;
        }

        public static Packet G_TO_U_MAP_INFO(List<string> object_key_list)
        {
            Packet packet = Packet.Create((int)PROTOCOL.G_TO_U_MAP_INFO);
            G_TO_U_MAP_INFO body = new() { object_key_list = object_key_list };

            packet.SetBody(MessagePackSerializer.Serialize(body));
            return packet;
        }

        public static Packet U_TO_C_MAP_UPDATE(List<GameObjectInfo> object_list)
        {
            Packet packet = Packet.Create((int)PROTOCOL.U_TO_C_MAP_UPDATE);
            U_TO_C_MAP_UPDATE body = new() { object_list = object_list };

            packet.SetBody(MessagePackSerializer.Serialize(body));
            return packet;
        }

        public static Packet U_TO_C_PLAYER_INFO(List<PlayerInfo> player_info_list)
        {
            Packet packet = Packet.Create((int)PROTOCOL.U_TO_C_PLAYER_INFO);
            U_TO_C_PLAYER_INFO body = new() { player_info_list = player_info_list };

            packet.SetBody(MessagePackSerializer.Serialize(body));
            return packet;
        }

        static Packet U_TO_C_CHAT_MSG(PlayerInfo player, string chat_message)
        {
            Packet packet = Packet.Create((int)PROTOCOL.U_TO_C_CHAT_MSG);
            U_TO_C_CHAT_MSG body = new() { chat_message = chat_message };

            packet.SetBody(MessagePackSerializer.Serialize(body));
            return packet;
        }

        public static Packet U_TO_G_MOVE(long player_id, DirectionType direction)
        {
            Packet packet = Packet.Create((int)PROTOCOL.U_TO_G_MOVE, player_id);
            U_TO_G_MOVE body = new() { direction = direction };

            packet.SetBody(MessagePackSerializer.Serialize(body));
            return packet;
        }

        public static Packet G_TO_U_MOVE(GameObjectInfo object_info)
        {
            Packet packet = Packet.Create((int)PROTOCOL.G_TO_U_MOVE, object_info.object_id);
            G_TO_U_MOVE body = new() { object_info = object_info };

            packet.SetBody(MessagePackSerializer.Serialize(body));
            return packet;
        }

        public static Packet U_TO_G_LOGOUT(long player_id)
        {
            Packet packet = Packet.Create((int)PROTOCOL.U_TO_G_LOGOUT, player_id);
            U_TO_G_LOGOUT body = new() { player_id = player_id };

            packet.SetBody(MessagePackSerializer.Serialize(body));
            return packet;
        }

        public static Packet MakeBoundTilePacket(List<Cell> tile_list)
        {
            Packet packet = Packet.Create((int)PROTOCOL.U_TO_C_BOUND_TILE_INFO);
            U_TO_C_BOUND_TILE_INFO body = new() { tile_list = tile_list };

            packet.SetBody(MessagePackSerializer.Serialize(body));
            return packet;
        }
    }
}
