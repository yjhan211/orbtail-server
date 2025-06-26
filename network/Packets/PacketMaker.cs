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
                CraftInfo = playerInfo.CraftInfo,
                CampInfo = playerInfo.CampInfo,
                LabInfo = labInfo,
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
    
    public static Packet U_TO_C_INVENTORY_UPDATE(List<ItemInfo> updateItems, bool isEnd)
    {
        var packet = Packet.Create((int)Protocol.U_TO_C_INVENTORY_UPDATE);
        U_TO_C_INVENTORY_UPDATE body = new() { UpdateItems = updateItems, IsEnd = isEnd };

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

    public static Packet U_TO_C_USE_ITEM(PlayerInfo playerInfo)
    {
        var packet = Packet.Create((int)Protocol.U_TO_C_USE_ITEM);
        U_TO_C_USE_ITEM body = new() { PlayerInfo = playerInfo, CraftInfo = playerInfo.CraftInfo };

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
    
    public static Packet U_TO_C_CHAT_MSG(ChatType chatType, long playerId, string name, string chatMessage)
    {
        var packet = Packet.Create((int)Protocol.U_TO_C_CHAT_MSG);
        U_TO_C_CHAT_MSG body = new() { ChatType = chatType, PlayerId = playerId, Name = name, ChatMessage = chatMessage };

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

    public static Packet G_TO_U_UPDATE_OBJECT(GameObjectInfo objectInfo)
    {
        var packet = Packet.Create((int)Protocol.G_TO_U_UPDATE_OBJECT, objectInfo.ObjectId);
        G_TO_U_UPDATE_OBJECT body = new() { ObjectInfo = objectInfo };

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

    public static Packet U_TO_C_CHANGE_MAP(ErrorCode errorCode)
    {
        var packet = Packet.Create((int)Protocol.U_TO_C_CHANGE_MAP);
        U_TO_C_CHANGE_MAP body = new() { ErrorCode = errorCode };

        packet.SetBody(MessagePackSerializer.Serialize(body));
        return packet;
    }
    
    public static Packet U_TO_C_CHANGE_MAP_SUCCESS(MapId lastMapId, MapId mapId, long mapSubId, Cell spawnCell, bool isFlip)
    {
        var packet = Packet.Create((int)Protocol.U_TO_C_CHANGE_MAP_SUCCESS);
        U_TO_C_CHANGE_MAP_SUCCESS body = new() { LastMapId = lastMapId, MapId = mapId, MapSubId = mapSubId, SpawnCell = spawnCell, IsFlip = isFlip };

        packet.SetBody(MessagePackSerializer.Serialize(body));
        return packet;
    }

    public static Packet U_TO_C_EXPLORE(ErrorCode errorCode)
    {
        var packet = Packet.Create((int)Protocol.U_TO_C_EXPLORE);
        U_TO_C_EXPLORE body = new() { ErrorCode = errorCode };

        packet.SetBody(MessagePackSerializer.Serialize(body));
        return packet;
    }

    public static Packet U_TO_C_EXPLORE_COMPLETE(bool isSuccess, int itemId)
    {
        var packet = Packet.Create((int)Protocol.U_TO_C_EXPLORE_COMPLETE);
        U_TO_C_EXPLORE_COMPLETE body = new() { IsSuccess = isSuccess, ItemId = itemId };

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

    public static Packet G_TO_U_ENTER_INSTANCE_SUCCESS(MapId mapId, long mapSubId)
    {
        var packet = Packet.Create((int)Protocol.G_TO_U_ENTER_INSTANCE_SUCCESS);
        G_TO_U_ENTER_INSTANCE_SUCCESS body = new() { MapId = mapId, MapSubId = mapSubId };

        packet.SetBody(MessagePackSerializer.Serialize(body));
        return packet;
    }

    public static Packet U_TO_C_UPGRADE_RESEARCH(Dictionary<int, ResearchInfo> researchInfoDict)
    {
        var packet = Packet.Create((int)Protocol.U_TO_C_UPGRADE_RESEARCH);
        U_TO_C_UPGRADE_RESEARCH body = new() { ResearchInfoDict = researchInfoDict };

        packet.SetBody(MessagePackSerializer.Serialize(body));
        return packet;
    }

    public static Packet U_TO_C_CRAFT(ErrorCode errorCode)
    {
        var packet = Packet.Create((int)Protocol.U_TO_C_CRAFT);
        U_TO_C_CRAFT body = new() { ErrorCode = errorCode };

        packet.SetBody(MessagePackSerializer.Serialize(body));
        return packet;
    }
    
    public static Packet U_TO_C_CRAFT_COMPLETE(bool isSuccess, List<SlotItem> slots)
    {
        var packet = Packet.Create((int)Protocol.U_TO_C_CRAFT_COMPLETE);
        U_TO_C_CRAFT_COMPLETE body = new() { IsSuccess = isSuccess, Slots = slots };

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

    public static Packet G_TO_U_SOCIAL_ACTION(long playerId, SocialActionType actionType)
    {
        var packet = Packet.Create((int)Protocol.G_TO_U_SOCIAL_ACTION);
        G_TO_U_SOCIAL_ACTION body = new() { PlayerId = playerId, SocialActionType = actionType };
        
        packet.SetBody(MessagePackSerializer.Serialize(body));
        return packet;
    }
    
    public static Packet U_TO_C_SOCIAL_ACTION(long playerId, SocialActionType actionType)
    {
        var packet = Packet.Create((int)Protocol.U_TO_C_SOCIAL_ACTION);
        U_TO_C_SOCIAL_ACTION body = new() { PlayerId = playerId, SocialActionType = actionType };
        
        packet.SetBody(MessagePackSerializer.Serialize(body));
        return packet;
    }

    public static Packet U_TO_C_QUEST_LIST(Dictionary<int, QuestInfo> questDict, bool isEnd)
    {
        var packet = Packet.Create((int)Protocol.U_TO_C_QUEST_LIST);
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
    
    public static Packet G_TO_U_TAKE_DAMAGE(long playerId, DamageType damageType, int damage)
    {
        var packet = Packet.Create((int)Protocol.G_TO_U_TAKE_DAMAGE);
        G_TO_U_TAKE_DAMAGE body = new() { PlayerId = playerId, DamageType = damageType, Damage = damage };

        packet.SetBody(MessagePackSerializer.Serialize(body));
        return packet;
    }

    public static Packet U_TO_C_TAKE_DAMAGE(long playerId, DamageType damageType, int damage)
    {
        var packet = Packet.Create((int)Protocol.U_TO_C_TAKE_DAMAGE);
        U_TO_C_TAKE_DAMAGE body = new() { PlayerId = playerId, DamageType = damageType, Damage = damage };

        packet.SetBody(MessagePackSerializer.Serialize(body));
        return packet;
    }

    public static Packet G_TO_U_ENVIRONMENT(DamageType damageType)
    {
        var packet = Packet.Create((int)Protocol.G_TO_U_ENVIRONMENT);
        G_TO_U_ENVIRONMENT body = new() { DamageType = damageType };

        packet.SetBody(MessagePackSerializer.Serialize(body));
        return packet;
    }

    public static Packet U_TO_C_PUT_MATERIAL(ErrorCode errorCode, List<SlotItem> slots)
    {
        var packet = Packet.Create((int)Protocol.U_TO_C_PUT_MATERIAL);
        U_TO_C_PUT_MATERIAL body = new() { ErrorCode = errorCode, Slots = slots }; 
        
        packet.SetBody(MessagePackSerializer.Serialize(body));
        return packet;
    }
    
    public static Packet U_TO_C_HANDLE_CRAFT(ErrorCode errorCode, List<SlotItem> slots)
    {
        var packet = Packet.Create((int)Protocol.U_TO_C_HANDLE_CRAFT);
        U_TO_C_HANDLE_CRAFT body = new() { ErrorCode = errorCode, Slots = slots }; 
        
        packet.SetBody(MessagePackSerializer.Serialize(body));
        return packet;
    }
}