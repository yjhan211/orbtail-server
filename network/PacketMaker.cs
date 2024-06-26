namespace user_server
{
    using MessagePack;
    using network;

    public static class PacketMaker
    {
        public static Packet U_TO_C_HEART_BEAT(DateTime utc_now)
        {
            Packet packet = Packet.Create((int)PROTOCOL.U_TO_C_HEART_BEAT);
            U_TO_C_HEART_BEAT body = new() { utc_now = utc_now };

            packet.SetBody(MessagePackSerializer.Serialize(body));
            return packet;
        }

        public static Packet U_TO_C_LOGIN(PlayerInfo player_info, LabInfo lab_info)
        {
            Packet packet = Packet.Create((int)PROTOCOL.U_TO_C_LOGIN);
            U_TO_C_LOGIN body =
                new()
                {
                    object_info = player_info.object_info,
                    player_info = player_info,
                    job_info = player_info.job_info,
                    lab_info = lab_info,
                };

            packet.SetBody(MessagePackSerializer.Serialize(body));
            return packet;
        }

        public static Packet U_TO_C_MAP_UPDATE(List<GameObjectInfo> object_list, DateTime utcnow)
        {
            Packet packet = Packet.Create((int)PROTOCOL.U_TO_C_MAP_UPDATE);
            U_TO_C_MAP_UPDATE body = new() { object_list = object_list, server_timestamp = utcnow };

            packet.SetBody(MessagePackSerializer.Serialize(body));
            return packet;
        }

        public static Packet U_TO_C_INVENTORY_ITEM_LIST(
            Dictionary<long, ItemInfo> item_dict,
            bool is_end
        )
        {
            Packet packet = Packet.Create((int)PROTOCOL.U_TO_C_INVENTORY_ITEM_LIST);
            U_TO_C_INVENTORY_ITEM_LIST body = new() { item_dict = item_dict, is_end = is_end };

            packet.SetBody(MessagePackSerializer.Serialize(body));
            return packet;
        }

        public static Packet U_TO_U_LAB_INVENTORY(Dictionary<long, ItemInfo> item_list)
        {
            Packet packet = Packet.Create((int)PROTOCOL.U_TO_U_LAB_INVENTORY);
            U_TO_U_LAB_INVENTORY body = new() { item_list = item_list };

            packet.SetBody(MessagePackSerializer.Serialize(body));
            return packet;
        }

        public static Packet U_TO_C_LAB_INVENTORY(Dictionary<long, ItemInfo> item_dict, bool is_end)
        {
            Packet packet = Packet.Create((int)PROTOCOL.U_TO_C_LAB_INVENTORY);
            U_TO_C_LAB_INVENTORY body = new() { item_dict = item_dict, is_end = is_end };

            packet.SetBody(MessagePackSerializer.Serialize(body));
            return packet;
        }

        public static Packet U_TO_C_WEAR_ITEM(PlayerInfo player_info)
        {
            Packet packet = Packet.Create((int)PROTOCOL.U_TO_C_WEAR_ITEM);
            U_TO_C_WEAR_ITEM body = new() { player_info = player_info };

            packet.SetBody(MessagePackSerializer.Serialize(body));
            return packet;
        }

        public static Packet U_TO_C_USE_ITEM(JobInfo job_info)
        {
            Packet packet = Packet.Create((int)PROTOCOL.U_TO_C_USE_ITEM);
            U_TO_C_USE_ITEM body = new() { job_info = job_info };

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

        public static Packet U_TO_C_EXPLORE_TARGET_INFO(List<ExploreTargetInfo> explore_target_list)
        {
            Packet packet = Packet.Create((int)PROTOCOL.U_TO_C_EXPLORE_TARGET_INFO);
            U_TO_C_EXPLORE_TARGET_INFO body =
                new() { explore_target_info_list = explore_target_list };

            packet.SetBody(MessagePackSerializer.Serialize(body));
            return packet;
        }

        public static Packet U_TO_C_JOB_RESOURCE_INFO(List<JobResourceInfo> job_resource_list)
        {
            Packet packet = Packet.Create((int)PROTOCOL.U_TO_C_JOB_RESOURCE_INFO);
            U_TO_C_JOB_RESOURCE_INFO body = new() { job_resource_info_list = job_resource_list };

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

        public static Packet U_TO_C_UPGRADE_JOB(
            long player_id,
            ErrorCode error_code,
            JobInfo? job_info = null
        )
        {
            Packet packet = Packet.Create((int)PROTOCOL.U_TO_C_UPGRADE_JOB, player_id);
            U_TO_C_UPGRADE_JOB body;
            if (error_code == ErrorCode.SUCCESS)
            {
                body = new() { error_code = error_code, job_info = job_info! };
            }
            else
            {
                body = new() { error_code = error_code };
            }

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

        public static Packet U_TO_C_MOVE(
            long player_id,
            ErrorCode error_code,
            GameObjectInfo object_info
        )
        {
            Packet packet = Packet.Create((int)PROTOCOL.U_TO_C_MOVE, player_id);
            U_TO_C_MOVE body = new() { error_code = error_code, object_info = object_info };

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

        public static Packet G_TO_U_EXPLORE_TARGET_INFO(ExploreTargetInfo explore_target_info)
        {
            Packet packet = Packet.Create((int)PROTOCOL.G_TO_U_EXPLORE_TARGET_INFO);
            G_TO_U_EXPLORE_TARGET_INFO body = new() { explore_target_info = explore_target_info };

            packet.SetBody(MessagePackSerializer.Serialize(body));
            return packet;
        }

        public static Packet G_TO_U_JOB_RESOURCE_INFO(JobResourceInfo job_resource_info)
        {
            Packet packet = Packet.Create((int)PROTOCOL.G_TO_U_JOB_RESOURCE_INFO);
            G_TO_U_JOB_RESOURCE_INFO body = new() { job_resource_info = job_resource_info };

            packet.SetBody(MessagePackSerializer.Serialize(body));
            return packet;
        }

        public static Packet G_TO_U_CAMP_INFO(CampInfo camp_info)
        {
            Packet packet = Packet.Create((int)PROTOCOL.G_TO_U_CAMP_INFO);
            G_TO_U_CAMP_INFO body = new() { camp_info = camp_info };

            packet.SetBody(MessagePackSerializer.Serialize(body));
            return packet;
        }

        public static Packet U_TO_C_CAMP_INFO(List<CampInfo> camp_info_list)
        {
            Packet packet = Packet.Create((int)PROTOCOL.U_TO_C_CAMP_INFO);
            U_TO_C_CAMP_INFO body = new() { camp_info_list = camp_info_list };

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

        public static Packet U_TO_C_CHANGE_MAP(
            MapID map_id,
            long map_sub_id,
            Cell spawn_cell,
            bool is_flip
        )
        {
            Packet packet = Packet.Create((int)PROTOCOL.U_TO_C_CHANGE_MAP);
            U_TO_C_CHANGE_MAP body =
                new()
                {
                    map_id = map_id,
                    map_sub_id = map_sub_id,
                    spawn_cell = spawn_cell,
                    is_flip = is_flip
                };

            packet.SetBody(MessagePackSerializer.Serialize(body));
            return packet;
        }

        public static Packet U_TO_C_EXPLORE(ErrorCode error_code, JobInfo? job_info = null)
        {
            Packet packet = Packet.Create((int)PROTOCOL.U_TO_C_EXPLORE);
            U_TO_C_EXPLORE body = new() { error_code = error_code, job_info = job_info };

            packet.SetBody(MessagePackSerializer.Serialize(body));
            return packet;
        }

        public static Packet U_TO_C_EXPLORE_COMPLETE(bool is_success, JobInfo? job_info = null)
        {
            Packet packet = Packet.Create((int)PROTOCOL.U_TO_C_EXPLORE_COMPLETE);
            U_TO_C_EXPLORE_COMPLETE body = new() { is_success = is_success, job_info = job_info };

            packet.SetBody(MessagePackSerializer.Serialize(body));
            return packet;
        }

        public static Packet U_TO_C_USE_SKILL(ErrorCode error_code, JobInfo? job_info = null)
        {
            Packet packet = Packet.Create((int)PROTOCOL.U_TO_C_USE_SKILL);
            U_TO_C_USE_SKILL body = new() { error_code = error_code, job_info = job_info };

            packet.SetBody(MessagePackSerializer.Serialize(body));
            return packet;
        }

        public static Packet U_TO_C_USE_SKILL_COMPLETE(
            bool is_success,
            ItemInfo? item_info,
            JobInfo job_info
        )
        {
            Packet packet = Packet.Create((int)PROTOCOL.U_TO_C_USE_SKILL_COMPLETE);
            U_TO_C_USE_SKILL_COMPLETE body =
                new()
                {
                    is_success = is_success,
                    item_info = item_info ?? new(),
                    job_info = job_info
                };

            packet.SetBody(MessagePackSerializer.Serialize(body));
            return packet;
        }

        public static Packet U_TO_C_CREATE_LAB(
            long player_id,
            PlayerInfo player_info,
            LabInfo lab_info
        )
        {
            Packet packet = Packet.Create((int)PROTOCOL.U_TO_C_CREATE_LAB, player_id);
            U_TO_C_CREATE_LAB body = new() { player_info = player_info, lab_info = lab_info };

            packet.SetBody(MessagePackSerializer.Serialize(body));
            return packet;
        }

        public static Packet G_TO_U_CREATE_INSTANCE_SUCCESS(MapID map_id, long map_sub_id)
        {
            Packet packet = Packet.Create((int)PROTOCOL.G_TO_U_CREATE_INSTANCE_SUCCESS);
            G_TO_U_CREATE_INSTANCE_SUCCESS body =
                new() { map_id = map_id, map_sub_id = map_sub_id };

            packet.SetBody(MessagePackSerializer.Serialize(body));
            return packet;
        }

        public static Packet U_TO_C_UPGRADE_RESEARCH(
            Dictionary<int, ResearchInfo> research_info_dict
        )
        {
            Packet packet = Packet.Create((int)PROTOCOL.U_TO_C_UPGRADE_RESEARCH);
            U_TO_C_UPGRADE_RESEARCH body = new() { reserach_info_dict = research_info_dict };

            packet.SetBody(MessagePackSerializer.Serialize(body));
            return packet;
        }

        public static Packet U_TO_C_MAKE(bool is_success)
        {
            Packet packet = Packet.Create((int)PROTOCOL.U_TO_C_MAKE);
            U_TO_C_MAKE body = new() { is_success = is_success };

            packet.SetBody(MessagePackSerializer.Serialize(body));
            return packet;
        }

        public static Packet U_TO_C_WRITE_LAB_HIRE(ErrorCode error_code)
        {
            Packet packet = Packet.Create((int)PROTOCOL.U_TO_C_WRITE_LAB_HIRE);
            U_TO_C_WRITE_LAB_HIRE body = new() { error_code = error_code };

            packet.SetBody(MessagePackSerializer.Serialize(body));
            return packet;
        }

        public static Packet U_TO_C_LAB_HIRE_LIST(List<(long, string, string)> hire_list)
        {
            Packet packet = Packet.Create((int)PROTOCOL.U_TO_C_LAB_HIRE_LIST);
            U_TO_C_LAB_HIRE_LIST body = new() { hire_list = hire_list };

            packet.SetBody(MessagePackSerializer.Serialize(body));
            return packet;
        }

        public static Packet U_TO_C_LAB_INFO(PlayerInfo join_player_info, LabInfo lab_info)
        {
            Packet packet = Packet.Create((int)PROTOCOL.U_TO_C_LAB_INFO);
            U_TO_C_LAB_INFO body =
                new() { join_player_info = join_player_info, lab_info = lab_info };

            packet.SetBody(MessagePackSerializer.Serialize(body));
            return packet;
        }

        public static Packet U_TO_C_UPDATE_HP(int add_hp, int current_hp)
        {
            Packet packet = Packet.Create((int)PROTOCOL.U_TO_C_UPDATE_HP);
            U_TO_C_UPDATE_HP body = new() { add_hp = add_hp, current_hp = current_hp };

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
