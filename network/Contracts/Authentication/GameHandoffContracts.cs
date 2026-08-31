using MessagePack;
using network.common;
using network.common.data.models;
using network.contracts.scaling;

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
    [Key("gameServerNodeId")] public string GameServerNodeId { get; set; } = string.Empty;
    [Key("gameServerGeneration")] public string GameServerGeneration { get; set; } = string.Empty;
    [Key("gameServerFence")] public long GameServerFence { get; set; }

    [IgnoreMember]
    public bool HasGameServerOwner =>
        !string.IsNullOrWhiteSpace(GameServerNodeId) &&
        !string.IsNullOrWhiteSpace(GameServerGeneration) &&
        GameServerFence > 0;

    public GameServerMatchOwner? GetGameServerOwner() =>
        HasGameServerOwner
            ? new GameServerMatchOwner(MatchingId, GameServerNodeId, GameServerGeneration, GameServerFence)
            : null;
}

public interface IGameHandoffTicketService
{
    public Task<string> IssueAsync(GameHandoffContext context);
    public Task<GameHandoffContext?> ConsumeAsync(string? ticket);
    public Task<GameHandoffContext?> ConsumeForOwnerAsync(
        string? ticket,
        GameServerNodeIdentity identity,
        TimeSpan provisionalOwnerLifetime);
}

public interface IGameHandoffTicketStore
{
    public Task<bool> TryStoreAsync(string ticketHash, GameHandoffContext context, TimeSpan lifetime);
    public Task<GameHandoffContext?> ConsumeAsync(string ticketHash);
    public Task<GameHandoffContext?> PeekOwnedAsync(string ticketHash);
    public Task<GameHandoffContext?> ConsumeOwnedAsync(
        string ticketHash,
        string consumeNonce,
        GameServerNodeIdentity identity,
        GameServerMatchOwner owner,
        TimeSpan provisionalOwnerLifetime,
        TimeSpan receiptLifetime);
}
