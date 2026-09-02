using MessagePack;
using network.common;
using network.common.data.models;

namespace network.contracts.authentication;

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

public interface IGameHandoffTicketService
{
    public Task<string> IssueAsync(GameHandoffContext context);

    /// <summary>
    ///     ticket을 1회 소비한다. context가 <paramref name="gameServerNodeId" /> 노드의 것이 아니면 null —
    ///     ticket은 이미 소비됐으므로 잘못 들어온 클라이언트는 매칭을 다시 받아야 한다.
    /// </summary>
    public Task<GameHandoffContext?> ConsumeAsync(string? ticket, string gameServerNodeId);
}

public interface IGameHandoffTicketStore
{
    public Task<bool> TryStoreAsync(string ticketHash, GameHandoffContext context, TimeSpan lifetime);
    public Task<GameHandoffContext?> ConsumeAsync(string ticketHash);
}
