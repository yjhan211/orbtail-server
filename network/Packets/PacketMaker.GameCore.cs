using MessagePack;
using network.common;
using network.common.data.models;

namespace network.packets;

// ========== GameServer 핵심 프로토콜 ==========

public static partial class PacketMaker
{
    public static Packet G_TO_C_HEART_BEAT(DateTime utcNow)
    {
        var packet = Packet.Create((int)Protocol.G_TO_C_HEART_BEAT);
        G_TO_C_HEART_BEAT body = new() { UtcNow = utcNow };

        packet.SetBody(MessagePackSerializer.Serialize(body));
        return packet;
    }

    public static Packet G_TO_C_CONNECT_RESULT(bool success, ErrorCode errorCode, string message = "")
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

    public static Packet G_TO_C_MOVE(long playerId, Vector3f position, Vector3f velocity, float rotation, Cell cell,
        uint lastProcessedInput, long serverTimestamp)
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
        G_TO_C_AREA_PLAYER_ENTER body = new() { PlayerInfo = playerInfo, Cell = cell };

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
        G_TO_C_AREA_EXIT_BLOCKED body = new() { AreaType = areaType, CorrectedCell = correctedCell };

        packet.SetBody(MessagePackSerializer.Serialize(body));
        return packet;
    }

    public static Packet G_TO_C_INTERACTABLE_LIST(AreaType areaType, List<InteractableObjectState> objects, bool isEnd)
    {
        var packet = Packet.Create((int)Protocol.G_TO_C_INTERACTABLE_LIST);
        G_TO_C_INTERACTABLE_LIST body = new() { AreaType = areaType, Objects = objects, IsEnd = isEnd };

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
}
