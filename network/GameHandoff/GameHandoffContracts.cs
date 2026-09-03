using MessagePack;
using network.common;
using network.common.data.models;

namespace network.gamehandoff;

[MessagePackObject]
public sealed class GameHandoffRosterEntry
{
    [Key("playerId")] public long PlayerId { get; set; }
    [Key("targetPlayerId")] public long TargetPlayerId { get; set; }
}

[MessagePackObject]
public sealed class GameHandoffContext
{
    [Key("playerId")] public long PlayerId { get; set; }
    [Key("matchingId")] public long MatchingId { get; set; }
    [Key("mapId")] public MapId MapId { get; set; }
    [Key("mapSubId")] public long MapSubId { get; set; }
    [Key("spawnPosition")] public Cell SpawnPosition { get; set; } = new(0, 0);
    [Key("targetPlayerId")] public long TargetPlayerId { get; set; }
    [Key("activeBuffIds")] public List<int> ActiveBuffIds { get; set; } = new();
    [Key("humanRoster")] public List<GameHandoffRosterEntry> HumanRoster { get; set; } = new();

    /// <summary>이 매치를 배정받은 Game Server 노드. 다른 노드는 이 ticket을 받아들이지 않는다.</summary>
    [Key("gameServerNodeId")] public string GameServerNodeId { get; set; } = string.Empty;
}
