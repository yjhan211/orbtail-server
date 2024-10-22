using MessagePack;
using network.common;

namespace network.packets
{
    public static class PacketMaker
    {
        public static Packet U_TO_C_HEART_BEAT(DateTime utcNow)
        {
            Packet packet = Packet.Create((int)PROTOCOL.U_TO_C_HEART_BEAT);
            U_TO_C_HEART_BEAT body = new() { UtcNow = utcNow };

            packet.SetBody(MessagePackSerializer.Serialize(body));
            return packet;
        }

        public static Packet U_TO_C_LOGIN(PlayerInfo player_info, LabInfo lab_info)
        {
            Packet packet = Packet.Create((int)PROTOCOL.U_TO_C_LOGIN);
            U_TO_C_LOGIN body =
                new()
                {
                    ObjectInfo = player_info.ObjectInfo,
                    PlayerInfo = player_info,
                    JobInfo = player_info.JobInfo,
                    LabInfo = lab_info,
                };

            packet.SetBody(MessagePackSerializer.Serialize(body));
            return packet;
        }

        public static Packet U_TO_C_MAP_UPDATE(List<GameObjectInfo> objectList, DateTime utcnow)
        {
            Packet packet = Packet.Create((int)PROTOCOL.U_TO_C_MAP_UPDATE);
            U_TO_C_MAP_UPDATE body = new() { ObjectList = objectList, ServerTimestamp = utcnow };

            packet.SetBody(MessagePackSerializer.Serialize(body));
            return packet;
        }

        public static Packet U_TO_C_INVENTORY_ITEM_LIST(Dictionary<long, ItemInfo> itemDict, bool isEnd)
        {
            Packet packet = Packet.Create((int)PROTOCOL.U_TO_C_INVENTORY_ITEM_LIST);
            U_TO_C_INVENTORY_ITEM_LIST body = new() { ItemDict = itemDict, IsEnd = isEnd };

            packet.SetBody(MessagePackSerializer.Serialize(body));
            return packet;
        }

        public static Packet U_TO_U_LAB_INVENTORY(Dictionary<long, ItemInfo> itemDict)
        {
            Packet packet = Packet.Create((int)PROTOCOL.U_TO_U_LAB_INVENTORY);
            U_TO_U_LAB_INVENTORY body = new() { ItemDict = itemDict };

            packet.SetBody(MessagePackSerializer.Serialize(body));
            return packet;
        }

        public static Packet U_TO_C_LAB_INVENTORY(Dictionary<long, ItemInfo> itemDict, bool isEnd)
        {
            Packet packet = Packet.Create((int)PROTOCOL.U_TO_C_LAB_INVENTORY);
            U_TO_C_LAB_INVENTORY body = new() { ItemDict = itemDict, IsEnd = isEnd };

            packet.SetBody(MessagePackSerializer.Serialize(body));
            return packet;
        }

        public static Packet U_TO_C_WEAR_ITEM(PlayerInfo playerInfo)
        {
            Packet packet = Packet.Create((int)PROTOCOL.U_TO_C_WEAR_ITEM);
            U_TO_C_WEAR_ITEM body = new() { PlayerInfo = playerInfo };

            packet.SetBody(MessagePackSerializer.Serialize(body));
            return packet;
        }

        public static Packet U_TO_C_USE_ITEM(JobInfo jobInfo)
        {
            Packet packet = Packet.Create((int)PROTOCOL.U_TO_C_USE_ITEM);
            U_TO_C_USE_ITEM body = new() { JobInfo = jobInfo };

            packet.SetBody(MessagePackSerializer.Serialize(body));
            return packet;
        }

        public static Packet U_TO_U_PLAYER_INFO(PlayerInfo playerInfo)
        {
            Packet packet = Packet.Create((int)PROTOCOL.U_TO_U_PLAYER_INFO);
            U_TO_U_PLAYER_INFO body = new() { PlayerInfo = playerInfo };

            packet.SetBody(MessagePackSerializer.Serialize(body));
            return packet;
        }

        public static Packet U_TO_C_PLAYER_INFO(List<PlayerInfo> playerInfoList)
        {
            Packet packet = Packet.Create((int)PROTOCOL.U_TO_C_PLAYER_INFO);
            U_TO_C_PLAYER_INFO body = new() { PlayerInfoList = playerInfoList };

            packet.SetBody(MessagePackSerializer.Serialize(body));
            return packet;
        }

        public static Packet U_TO_C_EXPLORE_TARGET_INFO(List<ExploreTargetInfo> exploreTargetList)
        {
            Packet packet = Packet.Create((int)PROTOCOL.U_TO_C_EXPLORE_TARGET_INFO);
            U_TO_C_EXPLORE_TARGET_INFO body = new() { ExploreTargetList = exploreTargetList };

            packet.SetBody(MessagePackSerializer.Serialize(body));
            return packet;
        }

        public static Packet U_TO_C_JOB_RESOURCE_INFO(List<JobResourceInfo> jobResourceInfoList)
        {
            Packet packet = Packet.Create((int)PROTOCOL.U_TO_C_JOB_RESOURCE_INFO);
            U_TO_C_JOB_RESOURCE_INFO body = new() { JobResourceInfoList = jobResourceInfoList };

            packet.SetBody(MessagePackSerializer.Serialize(body));
            return packet;
        }

        public static Packet U_TO_C_CHAT_MSG(ChatType chatType, string name, string chatMessage)
        {
            Packet packet = Packet.Create((int)PROTOCOL.U_TO_C_CHAT_MSG);
            U_TO_C_CHAT_MSG body = new() { ChatType = chatType, Name = name, ChatMessage = chatMessage };

            packet.SetBody(MessagePackSerializer.Serialize(body));
            return packet;
        }

        public static Packet U_TO_C_UPGRADE_JOB(long playerId, ErrorCode errorCode, JobInfo? jobInfo = null)
        {
            Packet packet = Packet.Create((int)PROTOCOL.U_TO_C_UPGRADE_JOB, playerId);
            U_TO_C_UPGRADE_JOB body = new() { ErrorCode = errorCode };
            if (jobInfo != null)
            {
                body.JobInfo = jobInfo;
            }

            packet.SetBody(MessagePackSerializer.Serialize(body));
            return packet;
        }

        public static Packet U_TO_C_ADD_SELL_ITEM(long playerId)
        {
            Packet packet = Packet.Create((int)PROTOCOL.U_TO_C_ADD_SELL_ITEM, playerId);
            return packet;
        }

        public static Packet U_TO_C_DELETE_SELL_ITEM(long playerId)
        {
            Packet packet = Packet.Create((int)PROTOCOL.U_TO_C_DELETE_SELL_ITEM, playerId);
            return packet;
        }

        public static Packet U_TO_C_BUY_ITEM(long playerId, PlayerInfo playerInfo)
        {
            Packet packet = Packet.Create((int)PROTOCOL.U_TO_C_BUY_ITEM, playerId);
            U_TO_C_BUY_ITEM body = new() { PlayerInfo = playerInfo };

            packet.SetBody(MessagePackSerializer.Serialize(body));
            return packet;
        }

        public static Packet U_TO_G_MOVE(long playerId, GameObjectInfo objectInfo, Cell targetCell)
        {
            Packet packet = Packet.Create((int)PROTOCOL.U_TO_G_MOVE, playerId);
            U_TO_G_MOVE body = new() { ObjectInfo = objectInfo, TargetCell = targetCell };

            packet.SetBody(MessagePackSerializer.Serialize(body));
            return packet;
        }

        public static Packet U_TO_C_MOVE(long playerId, ErrorCode errorCode, GameObjectInfo objectInfo)
        {
            Packet packet = Packet.Create((int)PROTOCOL.U_TO_C_MOVE, playerId);
            U_TO_C_MOVE body = new() { ErrorCode = errorCode, ObjectInfo = objectInfo };

            packet.SetBody(MessagePackSerializer.Serialize(body));
            return packet;
        }

        public static Packet G_TO_U_MOVE(GameObjectInfo objectInfo)
        {
            Packet packet = Packet.Create((int)PROTOCOL.G_TO_U_UPDATE_OBJECT, objectInfo.ObjectId);
            G_TO_U_MOVE body = new() { ObjectInfo = objectInfo };

            packet.SetBody(MessagePackSerializer.Serialize(body));
            return packet;
        }

        public static Packet G_TO_U_PLAYER_INFO(PlayerInfo playerInfo)
        {
            Packet packet = Packet.Create((int)PROTOCOL.G_TO_U_PLAYER_INFO, playerInfo.PlayerId);
            G_TO_U_PLAYER_INFO body = new() { PlayerInfo = playerInfo };

            packet.SetBody(MessagePackSerializer.Serialize(body));
            return packet;
        }

        public static Packet G_TO_U_EXPLORE_TARGET_INFO(ExploreTargetInfo exploreTargetInfo)
        {
            Packet packet = Packet.Create((int)PROTOCOL.G_TO_U_EXPLORE_TARGET_INFO);
            G_TO_U_EXPLORE_TARGET_INFO body = new() { ExploreTargetInfo = exploreTargetInfo };

            packet.SetBody(MessagePackSerializer.Serialize(body));
            return packet;
        }

        public static Packet G_TO_U_JOB_RESOURCE_INFO(JobResourceInfo jobResourceInfo)
        {
            Packet packet = Packet.Create((int)PROTOCOL.G_TO_U_JOB_RESOURCE_INFO);
            G_TO_U_JOB_RESOURCE_INFO body = new() { JobResourceInfo = jobResourceInfo };

            packet.SetBody(MessagePackSerializer.Serialize(body));
            return packet;
        }

        public static Packet G_TO_U_CAMP_INFO(CampInfo campInfo)
        {
            Packet packet = Packet.Create((int)PROTOCOL.G_TO_U_CAMP_INFO);
            G_TO_U_CAMP_INFO body = new() { CampInfo = campInfo };

            packet.SetBody(MessagePackSerializer.Serialize(body));
            return packet;
        }

        public static Packet U_TO_C_CAMP_INFO(List<CampInfo> campInfoList)
        {
            Packet packet = Packet.Create((int)PROTOCOL.U_TO_C_CAMP_INFO);
            U_TO_C_CAMP_INFO body = new() { CampInfoList = campInfoList };

            packet.SetBody(MessagePackSerializer.Serialize(body));
            return packet;
        }

        public static Packet G_TO_U_SPAWN(List<string> objectKeyList)
        {
            Packet packet = Packet.Create((int)PROTOCOL.G_TO_U_SPAWN);
            G_TO_U_SPAWN body = new() { ObjectKeyList = objectKeyList };

            packet.SetBody(MessagePackSerializer.Serialize(body));
            return packet;
        }

        public static Packet U_TO_C_SPAWN(List<string> objectKeyList, bool isEnd)
        {
            Packet packet = Packet.Create((int)PROTOCOL.U_TO_C_SPAWN);
            U_TO_C_SPAWN body = new() { ObjectKeyList = objectKeyList, IsEnd = isEnd };

            packet.SetBody(MessagePackSerializer.Serialize(body));
            return packet;
        }

        public static Packet G_TO_U_DESTROY(string objectKey)
        {
            Packet packet = Packet.Create((int)PROTOCOL.G_TO_U_DESTROY);
            G_TO_U_DESTROY body = new() { ObjectKey = objectKey };

            packet.SetBody(MessagePackSerializer.Serialize(body));
            return packet;
        }

        public static Packet U_TO_C_DESTROY(string objectKey)
        {
            Packet packet = Packet.Create((int)PROTOCOL.U_TO_C_DESTROY);
            U_TO_C_DESTROY body = new() { ObjectKey = objectKey };

            packet.SetBody(MessagePackSerializer.Serialize(body));
            return packet;
        }

        public static Packet U_TO_C_CHANGE_MAP(MapID mapId, long mapSubId, Cell spawnCell, bool isFlip)
        {
            Packet packet = Packet.Create((int)PROTOCOL.U_TO_C_CHANGE_MAP);
            U_TO_C_CHANGE_MAP body = new() { MapId = mapId, MapSubId = mapSubId, SpawnCell = spawnCell, IsFlip = isFlip };

            packet.SetBody(MessagePackSerializer.Serialize(body));
            return packet;
        }

        public static Packet U_TO_C_EXPLORE(ErrorCode errorCode, JobInfo? jobInfo = null)
        {
            Packet packet = Packet.Create((int)PROTOCOL.U_TO_C_EXPLORE);
            U_TO_C_EXPLORE body = new() { ErrorCode = errorCode };
            if (jobInfo != null)
            {
                body.JobInfo = jobInfo;
            }

            packet.SetBody(MessagePackSerializer.Serialize(body));
            return packet;
        }

        public static Packet U_TO_C_EXPLORE_COMPLETE(bool isSuccess, JobInfo? jobInfo = null)
        {
            Packet packet = Packet.Create((int)PROTOCOL.U_TO_C_EXPLORE_COMPLETE);
            U_TO_C_EXPLORE_COMPLETE body = new() { IsSuccess = isSuccess };
            if (jobInfo != null)
            {
                body.JobInfo = jobInfo;
            }

            packet.SetBody(MessagePackSerializer.Serialize(body));
            return packet;
        }

        public static Packet U_TO_C_USE_SKILL(ErrorCode errorCode, JobInfo? jobInfo = null)
        {
            Packet packet = Packet.Create((int)PROTOCOL.U_TO_C_USE_SKILL);
            U_TO_C_USE_SKILL body = new() { ErrorCode = errorCode };
            if (jobInfo != null)
            {
                body.JobInfo = jobInfo;
            }

            packet.SetBody(MessagePackSerializer.Serialize(body));
            return packet;
        }

        public static Packet U_TO_C_USE_SKILL_COMPLETE(bool isSuccess, ItemInfo? itemInfo, JobInfo jobInfo)
        {
            Packet packet = Packet.Create((int)PROTOCOL.U_TO_C_USE_SKILL_COMPLETE);
            U_TO_C_USE_SKILL_COMPLETE body =
                new()
                {
                    IsSuccess = isSuccess,
                    ItemInfo = itemInfo ?? new(),
                    JobInfo = jobInfo
                };

            packet.SetBody(MessagePackSerializer.Serialize(body));
            return packet;
        }

        public static Packet U_TO_C_CREATE_LAB(long playerId, PlayerInfo playerInfo, LabInfo labInfo)
        {
            Packet packet = Packet.Create((int)PROTOCOL.U_TO_C_CREATE_LAB, playerId);
            U_TO_C_CREATE_LAB body = new() { PlayerInfo = playerInfo, LabInfo = labInfo };

            packet.SetBody(MessagePackSerializer.Serialize(body));
            return packet;
        }

        public static Packet G_TO_U_CREATE_INSTANCE_SUCCESS(MapID mapId, long mapSubId)
        {
            Packet packet = Packet.Create((int)PROTOCOL.G_TO_U_CREATE_INSTANCE_SUCCESS);
            G_TO_U_CREATE_INSTANCE_SUCCESS body = new() { MapId = mapId, MapSubId = mapSubId };

            packet.SetBody(MessagePackSerializer.Serialize(body));
            return packet;
        }

        public static Packet U_TO_C_UPGRADE_RESEARCH(Dictionary<int, ResearchInfo> researchInfoDict, JobInfo jobInfo)
        {
            Packet packet = Packet.Create((int)PROTOCOL.U_TO_C_UPGRADE_RESEARCH);
            U_TO_C_UPGRADE_RESEARCH body = new() { ResearchInfoDict = researchInfoDict, JobInfo = jobInfo };

            packet.SetBody(MessagePackSerializer.Serialize(body));
            return packet;
        }

        public static Packet U_TO_C_MAKE(bool isSuccess)
        {
            Packet packet = Packet.Create((int)PROTOCOL.U_TO_C_MAKE);
            U_TO_C_MAKE body = new() { IsSuccess = isSuccess };

            packet.SetBody(MessagePackSerializer.Serialize(body));
            return packet;
        }

        public static Packet U_TO_C_WRITE_LAB_HIRE(ErrorCode errorCode)
        {
            Packet packet = Packet.Create((int)PROTOCOL.U_TO_C_WRITE_LAB_HIRE);
            U_TO_C_WRITE_LAB_HIRE body = new() { ErrorCode = errorCode };

            packet.SetBody(MessagePackSerializer.Serialize(body));
            return packet;
        }

        public static Packet U_TO_C_LAB_HIRE_LIST(List<(long, string, string)> hireList)
        {
            Packet packet = Packet.Create((int)PROTOCOL.U_TO_C_LAB_HIRE_LIST);
            U_TO_C_LAB_HIRE_LIST body = new() { HireList = hireList };

            packet.SetBody(MessagePackSerializer.Serialize(body));
            return packet;
        }

        public static Packet U_TO_C_LAB_INFO(PlayerInfo joinPlayerInfo, LabInfo labInfo)
        {
            Packet packet = Packet.Create((int)PROTOCOL.U_TO_C_LAB_INFO);
            U_TO_C_LAB_INFO body = new() { JoinPlayerInfo = joinPlayerInfo, LabInfo = labInfo };

            packet.SetBody(MessagePackSerializer.Serialize(body));
            return packet;
        }

        public static Packet U_TO_C_UPDATE_HP(int addHp, int currentHp)
        {
            Packet packet = Packet.Create((int)PROTOCOL.U_TO_C_UPDATE_HP);
            U_TO_C_UPDATE_HP body = new() { AddHp = addHp, CurrentHp = currentHp };

            packet.SetBody(MessagePackSerializer.Serialize(body));
            return packet;
        }

        public static Packet U_TO_C_SET_NAME(ErrorCode errorCode, PlayerInfo playerInfo)
        {
            Packet packet = Packet.Create((int)PROTOCOL.U_TO_C_SET_NAME);
            U_TO_C_SET_NAME body = new() { ErrorCode = errorCode, PlayerInfo = playerInfo };

            packet.SetBody(MessagePackSerializer.Serialize(body));
            return packet;
        }

        public static Packet U_TO_C_UPDATE_TUTORIAL(PlayerInfo playerInfo, JobInfo jobInfo)
        {
            Packet packet = Packet.Create((int)PROTOCOL.U_TO_C_UPDATE_TUTORIAL);
            U_TO_C_UPDATE_TUTORIAL body = new() { PlayerInfo = playerInfo, JobInfo = jobInfo };

            packet.SetBody(MessagePackSerializer.Serialize(body));
            return packet;
        }

        public static Packet U_TO_G_LOGOUT(long playerId)
        {
            Packet packet = Packet.Create((int)PROTOCOL.U_TO_G_LOGOUT, playerId);
            U_TO_G_LOGOUT body = new() { PlayerId = playerId };

            packet.SetBody(MessagePackSerializer.Serialize(body));
            return packet;
        }
    }
}
