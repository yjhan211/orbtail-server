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

    public static Packet G_TO_C_OBJECT_INFO(List<GameObjectInfo> objects)
    {
        var packet = Packet.Create((int)Protocol.G_TO_C_OBJECT_INFO);
        G_TO_C_OBJECT_INFO body = new() { Objects = objects };

        packet.SetBody(MessagePackSerializer.Serialize(body));
        return packet;
    }

    public static Packet G_TO_C_MOVE(long playerId, Vector3f position, Vector3f velocity, float rotation, Cell cell,
        long serverTimestamp, float orbOrbitPhaseDegrees = 0f)
    {
        var packet = Packet.Create((int)Protocol.G_TO_C_MOVE, playerId);
        G_TO_C_MOVE body = new()
        {
            PlayerId = playerId,
            Position = position,
            Velocity = velocity,
            Rotation = rotation,
            Cell = cell,
            ServerTimestamp = serverTimestamp,
            OrbOrbitPhaseDegrees = orbOrbitPhaseDegrees
        };

        packet.SetBody(MessagePackSerializer.Serialize(body));
        return packet;
    }

    public static Packet G_TO_C_AREA_PLAYER_ENTER(GameObjectInfo objectInfo)
    {
        var packet = Packet.Create((int)Protocol.G_TO_C_AREA_PLAYER_ENTER);
        G_TO_C_AREA_PLAYER_ENTER body = new() { ObjectInfo = objectInfo };

        packet.SetBody(MessagePackSerializer.Serialize(body));
        return packet;
    }

    public static Packet G_TO_C_PLAYER_APPEARANCE(long playerId, List<int> wearItemIds)
    {
        var packet = Packet.Create((int)Protocol.G_TO_C_PLAYER_APPEARANCE);
        packet.SetBody(MessagePackSerializer.Serialize(new G_TO_C_PLAYER_APPEARANCE
        {
            PlayerId = playerId,
            WearItemIdList = wearItemIds
        }));
        return packet;
    }

    public static Packet G_TO_C_AREA_PLAYER_LEAVE(long playerId)
    {
        var packet = Packet.Create((int)Protocol.G_TO_C_AREA_PLAYER_LEAVE);
        G_TO_C_AREA_PLAYER_LEAVE body = new() { PlayerId = playerId };

        packet.SetBody(MessagePackSerializer.Serialize(body));
        return packet;
    }

    public static Packet G_TO_C_ENCOUNTER_REVEAL(
        long playerId,
        AreaType areaType,
        int eventType,
        int cooldownSeconds,
        int revealDelayMs = 0)
    {
        var packet = Packet.Create((int)Protocol.G_TO_C_ENCOUNTER_REVEAL);
        packet.SetBody(MessagePackSerializer.Serialize(new G_TO_C_ENCOUNTER_REVEAL
        {
            PlayerId = playerId, AreaType = areaType, EventType = eventType,
            CooldownSeconds = cooldownSeconds, RevealDelayMs = revealDelayMs
        }));
        return packet;
    }

    public static Packet G_TO_C_COMBAT_HIT(G_TO_C_COMBAT_HIT body)
    {
        var packet = Packet.Create((int)Protocol.G_TO_C_COMBAT_HIT);
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


    public static Packet G_TO_C_AREA_EXIT_BLOCKED(AreaType areaType, Cell correctedCell)
    {
        var packet = Packet.Create((int)Protocol.G_TO_C_AREA_EXIT_BLOCKED);
        G_TO_C_AREA_EXIT_BLOCKED body = new() { AreaType = areaType, CorrectedCell = correctedCell };

        packet.SetBody(MessagePackSerializer.Serialize(body));
        return packet;
    }

    public static Packet G_TO_C_INTERACTABLE_LIST(AreaType areaType, List<InteractableObjectState> objects)
    {
        var packet = Packet.Create((int)Protocol.G_TO_C_INTERACTABLE_LIST);
        G_TO_C_INTERACTABLE_LIST body = new() { AreaType = areaType, Objects = objects };

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

    public static Packet G_TO_C_ERROR(ErrorCode errorCode, string message = "")
    {
        var packet = Packet.Create((int)Protocol.G_TO_C_ERROR);
        G_TO_C_ERROR body = new() { ErrorCode = errorCode, Message = message };

        packet.SetBody(MessagePackSerializer.Serialize(body));
        return packet;
    }
}
