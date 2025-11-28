using network.common;
using network.common.data.models;

namespace user_server.domain.events;

/// <summary>
/// Base record for all player events
/// </summary>
public abstract record PlayerEvent(long PlayerId, DateTime OccurredAt) : IDomainEvent;

/// <summary>
/// Event raised when player info changes
/// </summary>
public record PlayerInfoChangedEvent(
    long PlayerId,
    PlayerInfo PlayerInfo,
    DateTime OccurredAt
) : PlayerEvent(PlayerId, OccurredAt);

/// <summary>
/// Event raised when player changes map
/// </summary>
public record PlayerMapChangedEvent(
    long PlayerId,
    MapId OldMapId,
    MapId NewMapId,
    DateTime OccurredAt
) : PlayerEvent(PlayerId, OccurredAt);

/// <summary>
/// Event raised when player completes a quest
/// </summary>
public record QuestCompletedEvent(
    long PlayerId,
    int QuestId,
    DateTime OccurredAt
) : PlayerEvent(PlayerId, OccurredAt);

/// <summary>
/// Event raised when player starts a quest
/// </summary>
public record QuestStartedEvent(
    long PlayerId,
    int QuestId,
    DateTime OccurredAt
) : PlayerEvent(PlayerId, OccurredAt);

/// <summary>
/// Event raised when player receives an item
/// </summary>
public record ItemReceivedEvent(
    long PlayerId,
    long ItemUid,
    int ItemId,
    int Quantity,
    DateTime OccurredAt
) : PlayerEvent(PlayerId, OccurredAt);

/// <summary>
/// Event raised when player uses an item
/// </summary>
public record ItemUsedEvent(
    long PlayerId,
    long ItemUid,
    int ItemId,
    DateTime OccurredAt
) : PlayerEvent(PlayerId, OccurredAt);

/// <summary>
/// Event raised when player receives mail
/// </summary>
public record MailReceivedEvent(
    long PlayerId,
    long MailUid,
    DateTime OccurredAt
) : PlayerEvent(PlayerId, OccurredAt);
