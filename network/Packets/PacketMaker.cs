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

    public static Packet G_TO_C_INTERACTABLE_LIST(int zoneId, List<InteractableState> interactables)
    {
        var packet = Packet.Create((int)Protocol.G_TO_C_INTERACTABLE_LIST);
        G_TO_C_INTERACTABLE_LIST body = new()
        {
            ZoneId = zoneId,
            Interactables = interactables
        };

        packet.SetBody(MessagePackSerializer.Serialize(body));
        return packet;
    }

    public static Packet G_TO_C_INTERACTABLE_UPDATE(int id, bool isExplored, long exploredBy)
    {
        var packet = Packet.Create((int)Protocol.G_TO_C_INTERACTABLE_UPDATE);
        G_TO_C_INTERACTABLE_UPDATE body = new()
        {
            Id = id,
            IsExplored = isExplored,
            ExploredBy = exploredBy
        };

        packet.SetBody(MessagePackSerializer.Serialize(body));
        return packet;
    }

    public static Packet G_TO_C_GAME_TIME_WARNING(int remainingSeconds)
    {
        var packet = Packet.Create((int)Protocol.G_TO_C_GAME_TIME_WARNING);
        G_TO_C_GAME_TIME_WARNING body = new() { RemainingSeconds = remainingSeconds };

        packet.SetBody(MessagePackSerializer.Serialize(body));
        return packet;
    }

    public static Packet G_TO_C_GAME_END(long matchingId)
    {
        var packet = Packet.Create((int)Protocol.G_TO_C_GAME_END);
        G_TO_C_GAME_END body = new() { MatchingId = matchingId };

        packet.SetBody(MessagePackSerializer.Serialize(body));
        return packet;
    }
}
