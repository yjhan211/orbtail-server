using network.common.data.models;

namespace user_server.application.queries.player;

/// <summary>
/// Query to get player information
/// </summary>
public record GetPlayerInfoQuery(long PlayerId);

/// <summary>
/// Query to get current player quests
/// </summary>
public record GetPlayerQuestsQuery(long PlayerId);

/// <summary>
/// Query to get player inventory items
/// </summary>
public record GetPlayerItemsQuery(long PlayerId);

/// <summary>
/// Query to get player mails
/// </summary>
public record GetPlayerMailsQuery(long PlayerId);

/// <summary>
/// Query result containing player information
/// </summary>
public record PlayerInfoResult(PlayerInfo? PlayerInfo);

/// <summary>
/// Query result containing quest list
/// </summary>
public record PlayerQuestsResult(List<QuestInfo>? Quests);

/// <summary>
/// Query result containing item list
/// </summary>
public record PlayerItemsResult(List<ItemInfo>? Items);

/// <summary>
/// Query result containing mail list
/// </summary>
public record PlayerMailsResult(List<MailInfo>? Mails);
