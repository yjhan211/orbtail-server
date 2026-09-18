using MessagePack;
using network.common;
using network.common.data.models;

namespace network.packets;

// ========== GameServer 핵심 프로토콜 ==========

public static partial class PacketMaker
{
    public static Packet G_TO_C_MATCH_ROSTER(long matchingId, List<PlayerInfo> playerRoster)
    {
        var packet = Packet.Create((int)Protocol.G_TO_C_MATCH_ROSTER);
        packet.SetBody(MessagePackSerializer.Serialize(new G_TO_C_MATCH_ROSTER
        {
            MatchingId = matchingId,
            PlayerRoster = playerRoster
        }));
        return packet;
    }

    public static Packet G_TO_C_HEART_BEAT(DateTime utcNow)
    {
        var packet = Packet.Create((int)Protocol.G_TO_C_HEART_BEAT);
        G_TO_C_HEART_BEAT body = new() { UtcNow = utcNow };

        packet.SetBody(MessagePackSerializer.Serialize(body));
        return packet;
    }

    public static Packet G_TO_C_CONNECT_RESULT(bool success, ErrorCode errorCode,
        long matchingId = 0, Cell? spawnCell = null)
    {
        var packet = Packet.Create((int)Protocol.G_TO_C_CONNECT_RESULT);
        G_TO_C_CONNECT_RESULT body = new()
        {
            Success = success,
            ErrorCode = errorCode,
            MatchingId = matchingId,
            SpawnCell = spawnCell ?? new Cell(0, 0)
        };

        packet.SetBody(MessagePackSerializer.Serialize(body));
        return packet;
    }

    public static Packet G_TO_C_PLAYER_INFO(List<GamePlayerInfo> players)
    {
        var packet = Packet.Create((int)Protocol.G_TO_C_PLAYER_INFO);
        G_TO_C_PLAYER_INFO body = new() { Players = players };

        packet.SetBody(MessagePackSerializer.Serialize(body));
        return packet;
    }

    public static Packet G_TO_C_MOVE(GameObjectInfo info, long serverTimestamp)
    {
        var packet = Packet.Create((int)Protocol.G_TO_C_MOVE, info.ObjectId);
        var body = new G_TO_C_MOVE
        {
            Objects = new() { info.Clone() },
            ServerTimestamp = serverTimestamp
        };
        packet.SetBody(MessagePackSerializer.Serialize(body));
        return packet;
    }

    public static Packet G_TO_C_COMBAT_HIT(G_TO_C_COMBAT_HIT body)
    {
        var packet = Packet.Create((int)Protocol.G_TO_C_COMBAT_HIT);
        packet.SetBody(MessagePackSerializer.Serialize(body));
        return packet;
    }

    public static Packet G_TO_C_MONSTER_DEATH(G_TO_C_MONSTER_DEATH body)
    {
        var packet = Packet.Create((int)Protocol.G_TO_C_MONSTER_DEATH);
        packet.SetBody(MessagePackSerializer.Serialize(body));
        return packet;
    }

    public static Packet G_TO_C_PLAYER_ELIMINATED(G_TO_C_PLAYER_ELIMINATED body)
    {
        var packet = Packet.Create((int)Protocol.G_TO_C_PLAYER_ELIMINATED);
        packet.SetBody(MessagePackSerializer.Serialize(body));
        return packet;
    }

    public static Packet G_TO_C_HEALTH_RECOVERY(G_TO_C_HEALTH_RECOVERY body)
    {
        var packet = Packet.Create((int)Protocol.G_TO_C_HEALTH_RECOVERY);
        packet.SetBody(MessagePackSerializer.Serialize(body));
        return packet;
    }

    public static Packet G_TO_C_STATUS_EFFECT(G_TO_C_STATUS_EFFECT body)
    {
        var packet = Packet.Create((int)Protocol.G_TO_C_STATUS_EFFECT);
        packet.SetBody(MessagePackSerializer.Serialize(body));
        return packet;
    }


    public static Packet G_TO_C_MOVE_CORRECTION(GameObjectInfo info)
    {
        var packet = Packet.Create((int)Protocol.G_TO_C_MOVE_CORRECTION);
        G_TO_C_MOVE_CORRECTION body = new() { ObjectInfo = info };

        packet.SetBody(MessagePackSerializer.Serialize(body));
        return packet;
    }

    public static Packet G_TO_C_INTERACTABLE_INFO(AreaType areaType, List<InteractableInfo> objects)
    {
        var packet = Packet.Create((int)Protocol.G_TO_C_INTERACTABLE_INFO);
        G_TO_C_INTERACTABLE_INFO body = new() { AreaType = areaType, Objects = objects };

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

    public static Packet G_TO_C_ERROR(ErrorCode errorCode, string message = "")
    {
        var packet = Packet.Create((int)Protocol.G_TO_C_ERROR);
        G_TO_C_ERROR body = new() { ErrorCode = errorCode, Message = message };

        packet.SetBody(MessagePackSerializer.Serialize(body));
        return packet;
    }
}
