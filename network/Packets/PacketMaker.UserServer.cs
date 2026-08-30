using MessagePack;
using network.common;
using network.common.data.models;

namespace network.packets;

// ========== UserServer 프로토콜 ==========

public static partial class PacketMaker
{
    public static Packet U_TO_C_HEART_BEAT(DateTime utcNow)
    {
        var packet = Packet.Create((int)Protocol.U_TO_C_HEART_BEAT);
        U_TO_C_HEART_BEAT body = new() { UtcNow = utcNow };

        packet.SetBody(MessagePackSerializer.Serialize(body));
        return packet;
    }

    public static Packet U_TO_C_LOGIN(PlayerInfo playerInfo, string accountToken)
    {
        var packet = Packet.Create((int)Protocol.U_TO_C_LOGIN);
        U_TO_C_LOGIN body =
            new()
            {
                ObjectInfo = playerInfo.ObjectInfo,
                PlayerInfo = playerInfo,
                AccountToken = accountToken
            };

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

    public static Packet U_TO_C_MATCHING_SUCCESS(long matchingId, MapId mapId, long mapSubId, Cell spawnPosition,
        string gameServerIp, int gameServerPort, long gameEndTimestamp,
        string gameHandoffTicket, long targetPlayerId, JobTitle targetJobTitle, JobTitle myJobTitle,
        List<PlayerInfo> playerRoster,
        List<int>? activeBuffIds = null)
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
            GameEndTimestamp = gameEndTimestamp,
            GameHandoffTicket = gameHandoffTicket,
            TargetPlayerId = targetPlayerId,
            TargetJobTitle = targetJobTitle,
            MyJobTitle = myJobTitle,
            PlayerRoster = playerRoster,
            ActiveBuffIds = activeBuffIds ?? new List<int>()
        };

        packet.SetBody(MessagePackSerializer.Serialize(body));
        return packet;
    }

    public static Packet U_TO_C_MATCHING_FAILED(ErrorCode errorCode, long matchingId = 0)
    {
        var packet = Packet.Create((int)Protocol.U_TO_C_MATCHING_FAILED);
        U_TO_C_MATCHING_FAILED body = new() { ErrorCode = errorCode, MatchingId = matchingId };

        packet.SetBody(MessagePackSerializer.Serialize(body));
        return packet;
    }

    public static Packet U_TO_C_ERROR(ErrorCode errorCode, string message = "")
    {
        var packet = Packet.Create((int)Protocol.U_TO_C_ERROR);
        U_TO_C_ERROR body = new() { ErrorCode = errorCode, Message = message };

        packet.SetBody(MessagePackSerializer.Serialize(body));
        return packet;
    }
}
