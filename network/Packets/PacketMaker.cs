using MessagePack;
using network.common;
using network.common.data.models;

namespace network.packets;

public static class PacketMaker
{
    // ========== UserServer 프로토콜 ==========

    public static Packet U_TO_C_HEART_BEAT(DateTime utcNow)
    {
        var packet = Packet.Create((int)Protocol.U_TO_C_HEART_BEAT);
        U_TO_C_HEART_BEAT body = new() { UtcNow = utcNow };

        packet.SetBody(MessagePackSerializer.Serialize(body));
        return packet;
    }

    public static Packet U_TO_C_LOGIN(PlayerInfo playerInfo)
    {
        var packet = Packet.Create((int)Protocol.U_TO_C_LOGIN);
        U_TO_C_LOGIN body =
            new()
            {
                ObjectInfo = playerInfo.ObjectInfo,
                PlayerInfo = playerInfo,
            };

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

    public static Packet U_TO_C_CHAT_MSG(ChatType chatType, long playerId, string name, string chatMessage)
    {
        var packet = Packet.Create((int)Protocol.U_TO_C_CHAT_MSG);
        U_TO_C_CHAT_MSG body = new() { ChatType = chatType, PlayerId = playerId, Name = name, ChatMessage = chatMessage };

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
        U_TO_C_USE_ITEM body = new() { PlayerInfo = playerInfo };

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

    public static Packet U_TO_C_MATCHING(ErrorCode errorCode)
    {
        var packet = Packet.Create((int)Protocol.U_TO_C_MATCHING);
        U_TO_C_MATCHING body = new() { ErrorCode = errorCode };

        packet.SetBody(MessagePackSerializer.Serialize(body));
        return packet;
    }

    public static Packet U_TO_C_MATCHING_CANCEL(ErrorCode errorCode)
    {
        var packet = Packet.Create((int)Protocol.U_TO_C_MATCHING_CANCEL);
        U_TO_C_MATCHING_CANCEL body = new() { ErrorCode = errorCode };

        packet.SetBody(MessagePackSerializer.Serialize(body));
        return packet;
    }

    public static Packet U_TO_C_MATCHING_SUCCESS(long matchingId, MapId mapId, long mapSubId, Cell spawnPosition, string gameServerIp, int gameServerPort, long gameEndTimestamp)
    {
        var packet = Packet.Create((int)Protocol.U_TO_C_MATCHING_SUCCESS);
        U_TO_C_MATCHING_SUCCESS body = new()
        {
            MatchingId = matchingId,
            MapId = mapId,
            MapSubId = mapSubId,
            SpawnPosition = spawnPosition,
            GameServerIp = gameServerIp,
            GameServerPort = gameServerPort,
            GameEndTimestamp = gameEndTimestamp
        };

        packet.SetBody(MessagePackSerializer.Serialize(body));
        return packet;
    }

    public static Packet U_TO_C_MATCHING_FAILED(ErrorCode errorCode)
    {
        var packet = Packet.Create((int)Protocol.U_TO_C_MATCHING_FAILED);
        U_TO_C_MATCHING_FAILED body = new() { ErrorCode = errorCode };

        packet.SetBody(MessagePackSerializer.Serialize(body));
        return packet;
    }

    // ========== GameServer 프로토콜 ==========

    public static Packet G_TO_C_HEART_BEAT(DateTime utcNow)
    {
        var packet = Packet.Create((int)Protocol.G_TO_C_HEART_BEAT);
        G_TO_C_HEART_BEAT body = new() { UtcNow = utcNow };

        packet.SetBody(MessagePackSerializer.Serialize(body));
        return packet;
    }

    public static Packet G_TO_C_CONNECT_RESULT(bool success, ErrorCode errorCode, string? message = null)
    {
        var packet = Packet.Create((int)Protocol.G_TO_C_CONNECT_RESULT);
        G_TO_C_CONNECT_RESULT body = new() { Success = success, ErrorCode = errorCode, Message = message };

        packet.SetBody(MessagePackSerializer.Serialize(body));
        return packet;
    }

    public static Packet G_TO_C_PLAYER_INFO(List<PlayerInfo> playerInfoList)
    {
        var packet = Packet.Create((int)Protocol.G_TO_C_PLAYER_INFO);
        G_TO_C_PLAYER_INFO body = new() { PlayerInfoList = playerInfoList };

        packet.SetBody(MessagePackSerializer.Serialize(body));
        return packet;
    }

    public static Packet G_TO_C_MOVE(long playerId, Vector3f position, Vector3f velocity, float rotation, Cell cell, uint lastProcessedInput, long serverTimestamp)
    {
        var packet = Packet.Create((int)Protocol.G_TO_C_MOVE, playerId);
        G_TO_C_MOVE body = new()
        {
            PlayerId = playerId,
            Position = position,
            Velocity = velocity,
            Rotation = rotation,
            Cell = cell,
            LastProcessedInput = lastProcessedInput,
            ServerTimestamp = serverTimestamp
        };

        packet.SetBody(MessagePackSerializer.Serialize(body));
        return packet;
    }

    public static Packet G_TO_C_AREA_PLAYER_ENTER(PlayerInfo playerInfo, Cell cell)
    {
        var packet = Packet.Create((int)Protocol.G_TO_C_AREA_PLAYER_ENTER);
        G_TO_C_AREA_PLAYER_ENTER body = new()
        {
            PlayerInfo = playerInfo,
            Cell = cell
        };

        packet.SetBody(MessagePackSerializer.Serialize(body));
        return packet;
    }

    public static Packet G_TO_C_AREA_PLAYER_LEAVE(long playerId)
    {
        var packet = Packet.Create((int)Protocol.G_TO_C_AREA_PLAYER_LEAVE);
        G_TO_C_AREA_PLAYER_LEAVE body = new() { PlayerId = playerId };

        packet.SetBody(MessagePackSerializer.Serialize(body));
        return packet;
    }

    public static Packet G_TO_C_AREA_EXIT_BLOCKED(AreaType areaType, Cell correctedCell)
    {
        var packet = Packet.Create((int)Protocol.G_TO_C_AREA_EXIT_BLOCKED);
        G_TO_C_AREA_EXIT_BLOCKED body = new()
        {
            AreaType = areaType,
            CorrectedCell = correctedCell
        };

        packet.SetBody(MessagePackSerializer.Serialize(body));
        return packet;
    }

    public static Packet G_TO_C_INTERACTABLE_LIST(AreaType areaType, List<InteractableObjectState> objects, bool isEnd)
    {
        var packet = Packet.Create((int)Protocol.G_TO_C_INTERACTABLE_LIST);
        G_TO_C_INTERACTABLE_LIST body = new()
        {
            AreaType = areaType,
            Objects = objects,
            IsEnd = isEnd
        };

        packet.SetBody(MessagePackSerializer.Serialize(body));
        return packet;
    }

    public static Packet G_TO_C_INTERACTABLE_UPDATE(int interactId, int order, bool isExplored, long exploredBy)
    {
        var packet = Packet.Create((int)Protocol.G_TO_C_INTERACTABLE_UPDATE);
        G_TO_C_INTERACTABLE_UPDATE body = new()
        {
            InteractId = interactId,
            Order = order,
            IsExplored = isExplored,
            ExploredBy = exploredBy
        };

        packet.SetBody(MessagePackSerializer.Serialize(body));
        return packet;
    }

    public static Packet G_TO_C_GAME_TIME_WARNING(long matchingId, int remainingSeconds)
    {
        var packet = Packet.Create((int)Protocol.G_TO_C_GAME_TIME_WARNING);
        G_TO_C_GAME_TIME_WARNING body = new() { MatchingId = matchingId, RemainingSeconds = remainingSeconds };

        packet.SetBody(MessagePackSerializer.Serialize(body));
        return packet;
    }

    public static Packet G_TO_C_GAME_END(long matchingId, bool isEscaped)
    {
        var packet = Packet.Create((int)Protocol.G_TO_C_GAME_END);
        G_TO_C_GAME_END body = new() { MatchingId = matchingId, IsEscaped = isEscaped };

        packet.SetBody(MessagePackSerializer.Serialize(body));
        return packet;
    }

    // ========== 탐색 프로토콜 ==========

    public static Packet G_TO_C_EXPLORE_START(long playerId, int interactId)
    {
        var packet = Packet.Create((int)Protocol.G_TO_C_EXPLORE_START);
        G_TO_C_EXPLORE_START body = new()
        {
            PlayerId = playerId,
            InteractId = interactId
        };

        packet.SetBody(MessagePackSerializer.Serialize(body));
        return packet;
    }

    public static Packet G_TO_C_EXPLORE_RESULT(bool success, int interactId, int actionId, int itemId, ErrorCode errorCode, bool isViolation = false)
    {
        var packet = Packet.Create((int)Protocol.G_TO_C_EXPLORE_RESULT);
        G_TO_C_EXPLORE_RESULT body = new()
        {
            Success = success,
            InteractId = interactId,
            ActionId = actionId,
            ItemId = itemId,
            ErrorCode = errorCode,
            IsViolation = isViolation
        };

        packet.SetBody(MessagePackSerializer.Serialize(body));
        return packet;
    }

    public static Packet G_TO_C_EXPLORE_END(long playerId)
    {
        var packet = Packet.Create((int)Protocol.G_TO_C_EXPLORE_END);
        G_TO_C_EXPLORE_END body = new()
        {
            PlayerId = playerId
        };

        packet.SetBody(MessagePackSerializer.Serialize(body));
        return packet;
    }

    public static Packet G_TO_C_INTERACTABLE_STATE_CHANGE(int interactId, int newState)
    {
        var packet = Packet.Create((int)Protocol.G_TO_C_INTERACTABLE_STATE_CHANGE);
        G_TO_C_INTERACTABLE_STATE_CHANGE body = new()
        {
            InteractId = interactId,
            NewState = newState
        };

        packet.SetBody(MessagePackSerializer.Serialize(body));
        return packet;
    }

    // ========== 인게임 인벤토리 프로토콜 ==========

    public static Packet G_TO_C_INGAME_INVENTORY_LIST(List<InGameItemInfo> items)
    {
        var packet = Packet.Create((int)Protocol.G_TO_C_INGAME_INVENTORY_LIST);
        G_TO_C_INGAME_INVENTORY_LIST body = new() { Items = items };

        packet.SetBody(MessagePackSerializer.Serialize(body));
        return packet;
    }

    public static Packet G_TO_C_INGAME_INVENTORY_UPDATE(List<InGameItemInfo> items)
    {
        var packet = Packet.Create((int)Protocol.G_TO_C_INGAME_INVENTORY_UPDATE);
        G_TO_C_INGAME_INVENTORY_UPDATE body = new() { Items = items };

        packet.SetBody(MessagePackSerializer.Serialize(body));
        return packet;
    }

    public static Packet G_TO_C_USE_INGAME_ITEM_RESULT(bool success, long itemUid, ErrorCode errorCode, int ruleId = 0)
    {
        var packet = Packet.Create((int)Protocol.G_TO_C_USE_INGAME_ITEM_RESULT);
        G_TO_C_USE_INGAME_ITEM_RESULT body = new()
        {
            Success = success,
            ItemUid = itemUid,
            ErrorCode = errorCode,
            RuleId = ruleId
        };

        packet.SetBody(MessagePackSerializer.Serialize(body));
        return packet;
    }

    // ========== 플레이어 상태 프로토콜 ==========

    public static Packet G_TO_C_PLAYER_STATE(long playerId, PlayerState state)
    {
        var packet = Packet.Create((int)Protocol.G_TO_C_PLAYER_STATE);
        G_TO_C_PLAYER_STATE body = new()
        {
            PlayerId = playerId,
            State = state
        };

        packet.SetBody(MessagePackSerializer.Serialize(body));
        return packet;
    }

    // ========== 플레이어 스탯 프로토콜 ==========

    public static Packet G_TO_C_PLAYER_STATS_UPDATE(int stamina, int staminaDelta, int corruption, int corruptionDelta)
    {
        var packet = Packet.Create((int)Protocol.G_TO_C_PLAYER_STATS_UPDATE);
        G_TO_C_PLAYER_STATS_UPDATE body = new()
        {
            Stamina = stamina,
            StaminaDelta = staminaDelta,
            Corruption = corruption,
            CorruptionDelta = corruptionDelta
        };

        packet.SetBody(MessagePackSerializer.Serialize(body));
        return packet;
    }

    // ========== 탈출 절차 프로토콜 ==========

    public static Packet G_TO_C_EXIT_STEP_INFO(int groupId, int currentStepOrder, int totalStepCount, bool isCompleted, long lastAdvancedBy = 0)
    {
        var packet = Packet.Create((int)Protocol.G_TO_C_EXIT_STEP_INFO);
        G_TO_C_EXIT_STEP_INFO body = new()
        {
            GroupId = groupId,
            CurrentStepOrder = currentStepOrder,
            TotalStepCount = totalStepCount,
            IsCompleted = isCompleted,
            LastAdvancedBy = lastAdvancedBy
        };

        packet.SetBody(MessagePackSerializer.Serialize(body));
        return packet;
    }

    public static Packet G_TO_C_EXIT_ADVANCE_RESULT(bool success, ErrorCode errorCode, bool escaped, int newStepOrder)
    {
        var packet = Packet.Create((int)Protocol.G_TO_C_EXIT_ADVANCE_RESULT);
        G_TO_C_EXIT_ADVANCE_RESULT body = new()
        {
            Success = success,
            ErrorCode = errorCode,
            Escaped = escaped,
            NewStepOrder = newStepOrder
        };

        packet.SetBody(MessagePackSerializer.Serialize(body));
        return packet;
    }

    public static Packet G_TO_C_EXIT_STEP_UPDATE(long advancedByPlayerId, int newStepOrder, bool escaped)
    {
        var packet = Packet.Create((int)Protocol.G_TO_C_EXIT_STEP_UPDATE);
        G_TO_C_EXIT_STEP_UPDATE body = new()
        {
            AdvancedByPlayerId = advancedByPlayerId,
            NewStepOrder = newStepOrder,
            Escaped = escaped
        };

        packet.SetBody(MessagePackSerializer.Serialize(body));
        return packet;
    }

    public static Packet G_TO_C_RETURN_TO_LOBBY_RESULT(bool success, ErrorCode errorCode)
    {
        var packet = Packet.Create((int)Protocol.G_TO_C_RETURN_TO_LOBBY_RESULT);
        G_TO_C_RETURN_TO_LOBBY_RESULT body = new()
        {
            Success = success,
            ErrorCode = errorCode
        };

        packet.SetBody(MessagePackSerializer.Serialize(body));
        return packet;
    }

    #region Door 패킷

    public static Packet G_TO_C_DOOR_STATE_UPDATE(int doorId, bool isOpen, ErrorCode errorCode = ErrorCode.SUCCESS, long openerPlayerId = 0)
    {
        var packet = Packet.Create((int)Protocol.G_TO_C_DOOR_STATE_UPDATE);
        G_TO_C_DOOR_STATE_UPDATE body = new()
        {
            DoorId = doorId,
            IsOpen = isOpen,
            ErrorCode = errorCode,
            OpenerPlayerId = openerPlayerId
        };

        packet.SetBody(MessagePackSerializer.Serialize(body));
        return packet;
    }

    public static Packet G_TO_C_DOOR_STATE_LIST(List<int> openDoorIds)
    {
        var packet = Packet.Create((int)Protocol.G_TO_C_DOOR_STATE_LIST);
        G_TO_C_DOOR_STATE_LIST body = new()
        {
            OpenDoorIds = openDoorIds
        };

        packet.SetBody(MessagePackSerializer.Serialize(body));
        return packet;
    }

    #endregion

    #region 복도 규칙 프로토콜

    public static Packet G_TO_C_CORRIDOR_BELL(List<BellEvent> bells)
    {
        var packet = Packet.Create((int)Protocol.G_TO_C_CORRIDOR_BELL);
        G_TO_C_CORRIDOR_BELL body = new()
        {
            Bells = bells
        };

        packet.SetBody(MessagePackSerializer.Serialize(body));
        return packet;
    }

    #endregion

    #region 플레이어 상호작용 프로토콜

    public static Packet G_TO_C_PLAYER_INTERACT_REQUEST(long playerId, ErrorCode errorCode)
    {
        var packet = Packet.Create((int)Protocol.G_TO_C_PLAYER_INTERACT_REQUEST);
        G_TO_C_PLAYER_INTERACT_REQUEST body = new()
        {
            PlayerId = playerId,
            ErrorCode = errorCode
        };

        packet.SetBody(MessagePackSerializer.Serialize(body));
        return packet;
    }

    public static Packet G_TO_C_PLAYER_INTERACT_RESULT(bool accepted, long playerId, ErrorCode errorCode)
    {
        var packet = Packet.Create((int)Protocol.G_TO_C_PLAYER_INTERACT_RESULT);
        G_TO_C_PLAYER_INTERACT_RESULT body = new()
        {
            Accepted = accepted,
            PlayerId = playerId,
            ErrorCode = errorCode
        };

        packet.SetBody(MessagePackSerializer.Serialize(body));
        return packet;
    }

    public static Packet G_TO_C_PLAYER_INTERACT_END(long playerId)
    {
        var packet = Packet.Create((int)Protocol.G_TO_C_PLAYER_INTERACT_END);
        G_TO_C_PLAYER_INTERACT_END body = new()
        {
            PlayerId = playerId
        };

        packet.SetBody(MessagePackSerializer.Serialize(body));
        return packet;
    }

    #endregion
}
