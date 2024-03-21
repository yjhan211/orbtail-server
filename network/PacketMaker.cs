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
                new()
                {
                    object_info = player_info.object_info,
                    player_info = player_info,
                    job_info = player_info.job_info,
                    inventory_info = player_info.inventory_info,
                };

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

        public static Packet U_TO_C_WEAR_ITEM(PlayerInfo player_info)
        {
            Packet packet = Packet.Create((int)PROTOCOL.U_TO_C_WEAR_ITEM);
            U_TO_C_WEAR_ITEM body =
                new() { player_info = player_info, inventory_info = player_info.inventory_info, };

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

        public static Packet U_TO_C_CHAT_MSG(ChatType chat_type, string name, string chat_message)
        {
            Packet packet = Packet.Create((int)PROTOCOL.U_TO_C_CHAT_MSG);
            U_TO_C_CHAT_MSG body =
                new()
                {
                    chat_type = chat_type,
                    name = name,
                    chat_message = chat_message
                };

            packet.SetBody(MessagePackSerializer.Serialize(body));
            return packet;
        }

        public static Packet U_TO_C_GET_JOB(long player_id, ErrorCode error_code, JobInfo job_info)
        {
            Packet packet = Packet.Create((int)PROTOCOL.U_TO_C_GET_JOB, player_id);
            U_TO_C_GET_JOB body = new() { error_code = error_code, job_info = job_info };

            packet.SetBody(MessagePackSerializer.Serialize(body));
            return packet;
        }

        public static Packet U_TO_G_MOVE(
            long player_id,
            GameObjectInfo object_info,
            Cell target_cell
        )
        {
            Packet packet = Packet.Create((int)PROTOCOL.U_TO_G_MOVE, player_id);
            U_TO_G_MOVE body = new() { object_info = object_info, target_cell = target_cell };

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

        public static Packet G_TO_U_PLAYER_INFO(PlayerInfo player_info)
        {
            Packet packet = Packet.Create((int)PROTOCOL.G_TO_U_PLAYER_INFO, player_info.player_id);
            G_TO_U_PLAYER_INFO body = new() { player_info = player_info };

            packet.SetBody(MessagePackSerializer.Serialize(body));
            return packet;
        }

        public static Packet G_TO_U_SPAWN(List<string> object_key_list)
        {
            Packet packet = Packet.Create((int)PROTOCOL.G_TO_U_SPAWN);
            G_TO_U_SPAWN body = new() { object_key_list = object_key_list };

            packet.SetBody(MessagePackSerializer.Serialize(body));
            return packet;
        }

        public static Packet U_TO_C_SPAWN(List<string> object_key_list, bool is_ended)
        {
            Packet packet = Packet.Create((int)PROTOCOL.U_TO_C_SPAWN);
            U_TO_C_SPAWN body = new() { object_key_list = object_key_list, is_ended = is_ended };

            packet.SetBody(MessagePackSerializer.Serialize(body));
            return packet;
        }

        public static Packet G_TO_U_DESTROY(string object_key)
        {
            Packet packet = Packet.Create((int)PROTOCOL.G_TO_U_DESTROY);
            G_TO_U_DESTROY body = new() { object_key = object_key };

            packet.SetBody(MessagePackSerializer.Serialize(body));
            return packet;
        }

        public static Packet U_TO_C_DESTROY(string object_key)
        {
            Packet packet = Packet.Create((int)PROTOCOL.U_TO_C_DESTROY);
            U_TO_C_DESTROY body = new() { object_key = object_key };

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
    }
}
