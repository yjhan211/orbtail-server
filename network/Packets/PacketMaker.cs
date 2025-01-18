using MessagePack;
using network.common;
using network.common.data.models;

namespace network.packets;

public static class PacketMaker
{
    public static Packet U_TO_C_HEART_BEAT(DateTime utcNow)
    {
        var packet = Packet.Create((int)Protocol.U_TO_C_HEART_BEAT);
        U_TO_C_HEART_BEAT body = new() { UtcNow = utcNow };

        packet.SetBody(MessagePackSerializer.Serialize(body));
        return packet;
    }

    public static Packet U_TO_C_LOGIN(PlayerInfo playerInfo, LabInfo labInfo)
    {
        var packet = Packet.Create((int)Protocol.U_TO_C_LOGIN);
        U_TO_C_LOGIN body =
            new()
            {
                ObjectInfo = playerInfo.ObjectInfo,
                PlayerInfo = playerInfo,
                JobInfo = playerInfo.JobInfo,
                LabInfo = labInfo
            };

        packet.SetBody(MessagePackSerializer.Serialize(body));
        return packet;
    }

    public static Packet U_TO_C_MAP_UPDATE(List<GameObjectInfo> objectList, DateTime utcNow)
    {
        var packet = Packet.Create((int)Protocol.U_TO_C_MAP_UPDATE);
        U_TO_C_MAP_UPDATE body = new() { ObjectList = objectList, ServerTimestamp = utcNow };

        packet.SetBody(MessagePackSerializer.Serialize(body));
        return packet;
    }

    public static Packet U_TO_C_INVENTORY_ITEM_LIST(Dictionary<long, ItemInfo> itemDict, bool isEnd)
    {
        var packet = Packet.Create((int)Protocol.U_TO_C_INVENTORY_ITEM_LIST);
        U_TO_C_INVENTORY_ITEM_LIST body = new() { ItemDict = itemDict, IsEnd = isEnd };

        packet.SetBody(MessagePackSerializer.Serialize(body));
        return packet;
    }

    public static Packet U_TO_U_LAB_INVENTORY(Dictionary<long, ItemInfo> itemDict)
    {
        var packet = Packet.Create((int)Protocol.U_TO_U_LAB_INVENTORY);
        U_TO_U_LAB_INVENTORY body = new() { ItemDict = itemDict };

        packet.SetBody(MessagePackSerializer.Serialize(body));
        return packet;
    }

    public static Packet U_TO_C_LAB_INVENTORY(Dictionary<long, ItemInfo> itemDict, bool isEnd)
    {
        var packet = Packet.Create((int)Protocol.U_TO_C_LAB_INVENTORY);
        U_TO_C_LAB_INVENTORY body = new() { ItemDict = itemDict, IsEnd = isEnd };

        packet.SetBody(MessagePackSerializer.Serialize(body));
        return packet;
    }

    public static Packet U_TO_C_WEAR_ITEM(PlayerInfo playerInfo)
    {
        var packet = Packet.Create((int)Protocol.U_TO_C_WEAR_ITEM);
        U_TO_C_WEAR_ITEM body = new() { PlayerInfo = playerInfo };

        packet.SetBody(MessagePackSerializer.Serialize(body));
        return packet;
    }

    public static Packet U_TO_C_USE_ITEM(JobInfo jobInfo)
    {
        var packet = Packet.Create((int)Protocol.U_TO_C_USE_ITEM);
        U_TO_C_USE_ITEM body = new() { JobInfo = jobInfo };

        packet.SetBody(MessagePackSerializer.Serialize(body));
        return packet;
    }

    public static Packet U_TO_C_PLAYER_INFO(List<PlayerInfo> playerInfoList)
    {
        var packet = Packet.Create((int)Protocol.U_TO_C_PLAYER_INFO);
        U_TO_C_PLAYER_INFO body = new() { PlayerInfoList = playerInfoList };

        packet.SetBody(MessagePackSerializer.Serialize(body));
        return packet;
    }

    public static Packet U_TO_C_EXPLORE_TARGET_INFO(List<ExploreTargetInfo> exploreTargetList)
    {
        var packet = Packet.Create((int)Protocol.U_TO_C_EXPLORE_TARGET_INFO);
        U_TO_C_EXPLORE_TARGET_INFO body = new() { ExploreTargetList = exploreTargetList };

        packet.SetBody(MessagePackSerializer.Serialize(body));
        return packet;
    }

    public static Packet U_TO_C_JOB_RESOURCE_INFO(List<JobResourceInfo> jobResourceInfoList)
    {
        var packet = Packet.Create((int)Protocol.U_TO_C_JOB_RESOURCE_INFO);
        U_TO_C_JOB_RESOURCE_INFO body = new() { JobResourceInfoList = jobResourceInfoList };

        packet.SetBody(MessagePackSerializer.Serialize(body));
        return packet;
    }

    public static Packet U_TO_C_CHAT_MSG(ChatType chatType, string name, string chatMessage)
    {
        var packet = Packet.Create((int)Protocol.U_TO_C_CHAT_MSG);
        U_TO_C_CHAT_MSG body = new() { ChatType = chatType, Name = name, ChatMessage = chatMessage };

        packet.SetBody(MessagePackSerializer.Serialize(body));
        return packet;
    }

    public static Packet U_TO_C_UPGRADE_JOB(long playerId, ErrorCode errorCode, JobInfo? jobInfo = null)
    {
        var packet = Packet.Create((int)Protocol.U_TO_C_UPGRADE_JOB, playerId);
        U_TO_C_UPGRADE_JOB body = new() { ErrorCode = errorCode };
        if (jobInfo != null) body.JobInfo = jobInfo;

        packet.SetBody(MessagePackSerializer.Serialize(body));
        return packet;
    }

    public static Packet U_TO_C_ADD_SELL_ITEM(long playerId)
    {
        var packet = Packet.Create((int)Protocol.U_TO_C_ADD_SELL_ITEM, playerId);
        return packet;
    }

    public static Packet U_TO_C_DELETE_SELL_ITEM(long playerId)
    {
        var packet = Packet.Create((int)Protocol.U_TO_C_DELETE_SELL_ITEM, playerId);
        return packet;
    }

    public static Packet U_TO_C_BUY_ITEM(long playerId, PlayerInfo playerInfo)
    {
        var packet = Packet.Create((int)Protocol.U_TO_C_BUY_ITEM, playerId);
        U_TO_C_BUY_ITEM body = new() { PlayerInfo = playerInfo };

        packet.SetBody(MessagePackSerializer.Serialize(body));
        return packet;
    }

    public static Packet U_TO_G_MOVE(long playerId, GameObjectInfo objectInfo, Cell targetCell)
    {
        var packet = Packet.Create((int)Protocol.U_TO_G_MOVE, playerId);
        U_TO_G_MOVE body = new() { ObjectInfo = objectInfo, TargetCell = targetCell };

        packet.SetBody(MessagePackSerializer.Serialize(body));
        return packet;
    }

    public static Packet U_TO_C_MOVE(long playerId, ErrorCode errorCode, GameObjectInfo objectInfo)
    {
        var packet = Packet.Create((int)Protocol.U_TO_C_MOVE, playerId);
        U_TO_C_MOVE body = new() { ErrorCode = errorCode, ObjectInfo = objectInfo };

        packet.SetBody(MessagePackSerializer.Serialize(body));
        return packet;
    }

    public static Packet G_TO_U_MOVE(GameObjectInfo objectInfo)
    {
        var packet = Packet.Create((int)Protocol.G_TO_U_UPDATE_OBJECT, objectInfo.ObjectId);
        G_TO_U_MOVE body = new() { ObjectInfo = objectInfo };

        packet.SetBody(MessagePackSerializer.Serialize(body));
        return packet;
    }

    public static Packet G_TO_U_PLAYER_INFO(PlayerInfo playerInfo)
    {
        var packet = Packet.Create((int)Protocol.G_TO_U_PLAYER_INFO, playerInfo.PlayerId);
        G_TO_U_PLAYER_INFO body = new() { PlayerInfo = playerInfo };

        packet.SetBody(MessagePackSerializer.Serialize(body));
        return packet;
    }

    public static Packet G_TO_U_EXPLORE_TARGET_INFO(ExploreTargetInfo exploreTargetInfo)
    {
        var packet = Packet.Create((int)Protocol.G_TO_U_EXPLORE_TARGET_INFO);
        G_TO_U_EXPLORE_TARGET_INFO body = new() { ExploreTargetInfo = exploreTargetInfo };

        packet.SetBody(MessagePackSerializer.Serialize(body));
        return packet;
    }

    public static Packet G_TO_U_JOB_RESOURCE_INFO(JobResourceInfo jobResourceInfo)
    {
        var packet = Packet.Create((int)Protocol.G_TO_U_JOB_RESOURCE_INFO);
        G_TO_U_JOB_RESOURCE_INFO body = new() { JobResourceInfo = jobResourceInfo };

        packet.SetBody(MessagePackSerializer.Serialize(body));
        return packet;
    }

    public static Packet G_TO_U_CAMP_INFO(CampInfo campInfo)
    {
        var packet = Packet.Create((int)Protocol.G_TO_U_CAMP_INFO);
        G_TO_U_CAMP_INFO body = new() { CampInfo = campInfo };

        packet.SetBody(MessagePackSerializer.Serialize(body));
        return packet;
    }

    public static Packet U_TO_C_CAMP_INFO(List<CampInfo> campInfoList)
    {
        var packet = Packet.Create((int)Protocol.U_TO_C_CAMP_INFO);
        U_TO_C_CAMP_INFO body = new() { CampInfoList = campInfoList };

        packet.SetBody(MessagePackSerializer.Serialize(body));
        return packet;
    }

    public static Packet G_TO_U_SPAWN(List<string> objectKeyList, List<Cell> cellsToRemove)
    {
        var packet = Packet.Create((int)Protocol.G_TO_U_SPAWN);
        G_TO_U_SPAWN body = new() { ObjectKeyList = objectKeyList, CellsToRemove = cellsToRemove };

        packet.SetBody(MessagePackSerializer.Serialize(body));
        return packet;
    }

    public static Packet U_TO_C_SPAWN(List<string> objectKeyList, bool isEnd, List<Cell> cellsToRemove)
    {
        var packet = Packet.Create((int)Protocol.U_TO_C_SPAWN);
        U_TO_C_SPAWN body = new() { ObjectKeyList = objectKeyList, IsEnd = isEnd, CellsToRemove = cellsToRemove };

        packet.SetBody(MessagePackSerializer.Serialize(body));
        return packet;
    }

    public static Packet G_TO_U_DESTROY(string objectKey)
    {
        var packet = Packet.Create((int)Protocol.G_TO_U_DESTROY);
        G_TO_U_DESTROY body = new() { ObjectKey = objectKey };

        packet.SetBody(MessagePackSerializer.Serialize(body));
        return packet;
    }

    public static Packet U_TO_C_DESTROY(string objectKey)
    {
        var packet = Packet.Create((int)Protocol.U_TO_C_DESTROY);
        U_TO_C_DESTROY body = new() { ObjectKey = objectKey };

        packet.SetBody(MessagePackSerializer.Serialize(body));
        return packet;
    }

    public static Packet U_TO_C_CHANGE_MAP(MapId mapId, long mapSubId, Cell spawnCell, bool isFlip)
    {
        var packet = Packet.Create((int)Protocol.U_TO_C_CHANGE_MAP);
        U_TO_C_CHANGE_MAP body = new() { MapId = mapId, MapSubId = mapSubId, SpawnCell = spawnCell, IsFlip = isFlip };

        packet.SetBody(MessagePackSerializer.Serialize(body));
        return packet;
    }

    public static Packet U_TO_C_EXPLORE(ErrorCode errorCode, JobInfo? jobInfo = null)
    {
        var packet = Packet.Create((int)Protocol.U_TO_C_EXPLORE);
        U_TO_C_EXPLORE body = new() { ErrorCode = errorCode };
        if (jobInfo != null) body.JobInfo = jobInfo;

        packet.SetBody(MessagePackSerializer.Serialize(body));
        return packet;
    }

    public static Packet U_TO_C_EXPLORE_COMPLETE(bool isSuccess, JobInfo? jobInfo = null)
    {
        var packet = Packet.Create((int)Protocol.U_TO_C_EXPLORE_COMPLETE);
        U_TO_C_EXPLORE_COMPLETE body = new() { IsSuccess = isSuccess };
        if (jobInfo != null) body.JobInfo = jobInfo;

        packet.SetBody(MessagePackSerializer.Serialize(body));
        return packet;
    }

    public static Packet U_TO_C_USE_SKILL(ErrorCode errorCode, JobInfo? jobInfo = null)
    {
        var packet = Packet.Create((int)Protocol.U_TO_C_USE_SKILL);
        U_TO_C_USE_SKILL body = new() { ErrorCode = errorCode };
        if (jobInfo != null) body.JobInfo = jobInfo;

        packet.SetBody(MessagePackSerializer.Serialize(body));
        return packet;
    }

    public static Packet U_TO_C_USE_SKILL_COMPLETE(bool isSuccess, ItemInfo? itemInfo, JobInfo jobInfo)
    {
        var packet = Packet.Create((int)Protocol.U_TO_C_USE_SKILL_COMPLETE);
        U_TO_C_USE_SKILL_COMPLETE body =
            new()
            {
                IsSuccess = isSuccess,
                ItemInfo = itemInfo ?? new ItemInfo(),
                JobInfo = jobInfo
            };

        packet.SetBody(MessagePackSerializer.Serialize(body));
        return packet;
    }

    public static Packet U_TO_C_CREATE_LAB(long playerId, PlayerInfo playerInfo, LabInfo labInfo)
    {
        var packet = Packet.Create((int)Protocol.U_TO_C_CREATE_LAB, playerId);
        U_TO_C_CREATE_LAB body = new() { PlayerInfo = playerInfo, LabInfo = labInfo };

        packet.SetBody(MessagePackSerializer.Serialize(body));
        return packet;
    }

    public static Packet G_TO_U_CREATE_INSTANCE_SUCCESS(MapId mapId, long mapSubId)
    {
        var packet = Packet.Create((int)Protocol.G_TO_U_CREATE_INSTANCE_SUCCESS);
        G_TO_U_CREATE_INSTANCE_SUCCESS body = new() { MapId = mapId, MapSubId = mapSubId };

        packet.SetBody(MessagePackSerializer.Serialize(body));
        return packet;
    }

    public static Packet U_TO_C_UPGRADE_RESEARCH(Dictionary<int, ResearchInfo> researchInfoDict, JobInfo jobInfo)
    {
        var packet = Packet.Create((int)Protocol.U_TO_C_UPGRADE_RESEARCH);
        U_TO_C_UPGRADE_RESEARCH body = new() { ResearchInfoDict = researchInfoDict, JobInfo = jobInfo };

        packet.SetBody(MessagePackSerializer.Serialize(body));
        return packet;
    }

    public static Packet U_TO_C_MAKE(bool isSuccess)
    {
        var packet = Packet.Create((int)Protocol.U_TO_C_MAKE);
        U_TO_C_MAKE body = new() { IsSuccess = isSuccess };

        packet.SetBody(MessagePackSerializer.Serialize(body));
        return packet;
    }

    public static Packet U_TO_C_WRITE_LAB_HIRE(ErrorCode errorCode)
    {
        var packet = Packet.Create((int)Protocol.U_TO_C_WRITE_LAB_HIRE);
        U_TO_C_WRITE_LAB_HIRE body = new() { ErrorCode = errorCode };

        packet.SetBody(MessagePackSerializer.Serialize(body));
        return packet;
    }

    public static Packet U_TO_C_LAB_HIRE_LIST(List<(long, string, string)> hireList)
    {
        var packet = Packet.Create((int)Protocol.U_TO_C_LAB_HIRE_LIST);
        U_TO_C_LAB_HIRE_LIST body = new() { HireList = hireList };

        packet.SetBody(MessagePackSerializer.Serialize(body));
        return packet;
    }

    public static Packet U_TO_C_LAB_INFO(PlayerInfo joinPlayerInfo, LabInfo labInfo)
    {
        var packet = Packet.Create((int)Protocol.U_TO_C_LAB_INFO);
        U_TO_C_LAB_INFO body = new() { JoinPlayerInfo = joinPlayerInfo, LabInfo = labInfo };

        packet.SetBody(MessagePackSerializer.Serialize(body));
        return packet;
    }

    public static Packet U_TO_C_UPDATE_HP(int addHp, int currentHp)
    {
        var packet = Packet.Create((int)Protocol.U_TO_C_UPDATE_HP);
        U_TO_C_UPDATE_HP body = new() { AddHp = addHp, CurrentHp = currentHp };

        packet.SetBody(MessagePackSerializer.Serialize(body));
        return packet;
    }

    public static Packet U_TO_C_SET_NAME(ErrorCode errorCode, PlayerInfo playerInfo)
    {
        var packet = Packet.Create((int)Protocol.U_TO_C_SET_NAME);
        U_TO_C_SET_NAME body = new() { ErrorCode = errorCode, PlayerInfo = playerInfo };

        packet.SetBody(MessagePackSerializer.Serialize(body));
        return packet;
    }

    public static Packet U_TO_C_UPDATE_TUTORIAL(PlayerInfo playerInfo, JobInfo jobInfo)
    {
        var packet = Packet.Create((int)Protocol.U_TO_C_UPDATE_TUTORIAL);
        U_TO_C_UPDATE_TUTORIAL body = new() { PlayerInfo = playerInfo, JobInfo = jobInfo };

        packet.SetBody(MessagePackSerializer.Serialize(body));
        return packet;
    }

    public static Packet U_TO_C_QUEST_LIST(Dictionary<int, QuestInfo> questDict, bool isEnd)
    {
        var packet = Packet.Create((int)Protocol.U_TO_C_QUESTS);
        U_TO_C_QUEST_LIST body = new() { QuestDict = questDict, IsEnd = isEnd };

        packet.SetBody(MessagePackSerializer.Serialize(body));
        return packet;
    }

    public static Packet U_TO_C_QUEST_UPDATE(QuestInfo questInfo)
    {
        var packet = Packet.Create((int)Protocol.U_TO_C_QUEST_UPDATE);
        U_TO_C_QUEST_UPDATE body = new() { QuestInfo = questInfo };

        packet.SetBody(MessagePackSerializer.Serialize(body));
        return packet;
    }

    public static Packet U_TO_C_QUEST_SUCCESS(int questId, ErrorCode errorCode)
    {
        var packet = Packet.Create((int)Protocol.U_TO_C_QUEST_SUCCESS);
        U_TO_C_QUEST_SUCCESS body = new() { QuestId = questId, ErrorCode = errorCode };

        packet.SetBody(MessagePackSerializer.Serialize(body));
        return packet;
    }
    
    public static Packet U_TO_C_MAIL_LIST(Dictionary<long, MailInfo> mailDict, bool isEnd)
    {
        var packet = Packet.Create((int)Protocol.U_TO_C_MAIL_LIST);
        U_TO_C_MAIL_LIST body = new() { MailDict = mailDict, IsEnd = isEnd };

        packet.SetBody(MessagePackSerializer.Serialize(body));
        return packet;
    }

    public static Packet U_TO_C_MAIL_RECEIVE(long mailUid, ErrorCode errorCode)
    {
        var packet = Packet.Create((int)Protocol.U_TO_C_MAIL_RECEIVE);
        U_TO_C_MAIL_RECEIVE body = new() { MailUid = mailUid, ErrorCode = errorCode };

        packet.SetBody(MessagePackSerializer.Serialize(body));
        return packet;
    }

    public static Packet U_TO_G_LOGOUT(long playerId)
    {
        var packet = Packet.Create((int)Protocol.U_TO_G_LOGOUT, playerId);
        U_TO_G_LOGOUT body = new() { PlayerId = playerId };

        packet.SetBody(MessagePackSerializer.Serialize(body));
        return packet;
    }
}