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
}

public interface IGameHandoffTicketService
{
    public Task<string> IssueAsync(GameHandoffContext context);
    public Task<GameHandoffContext?> ConsumeAsync(string? ticket);
}

public interface IGameHandoffTicketStore
{
    public Task<bool> TryStoreAsync(string ticketHash, GameHandoffContext context, TimeSpan lifetime);
    public Task<GameHandoffContext?> ConsumeAsync(string ticketHash);
}
