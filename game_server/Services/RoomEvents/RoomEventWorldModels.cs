using System.Collections.Immutable;
using network.common;
using network.common.data.models;

namespace game_server.services;

public enum RoomEventWorldStatus
{
    Dormant = 0,
    Active = 1,
    Contained = 2,
    Overrun = 3
}

public sealed record RoomEventWorldState(
    long EventInstanceId,
    int EventId,
    AreaType AreaType,
    int InteractId,
    RoomEventWorldStatus Status,
    int StateVersion,
    DateTime ActivatedAtUtc,
    DateTime ExpiresAtUtc,
    string RequiredResponseTag,
    int CurrentContribution,
    int TargetContribution,
    ImmutableDictionary<long, int> ContributionByPlayer,
    ImmutableDictionary<int, int> ContributionByItemId,
    ImmutableHashSet<int> DistinctContributedItemIds,
    int TimeExtensionsUsed,
    int MaxTimeExtensions,
    int TimeExtensionSeconds,
    int ManualResponsesUsed,
    int MaxManualResponses,
    int MaxContributionPerPlayer)
{
    public bool IsActive => Status == RoomEventWorldStatus.Active;
}

public sealed record RoomEventActivationCommand(
    long MatchingId,
    int EventId,
    AreaType AreaType,
    int InteractId,
    string RequiredResponseTag);

public sealed record RoomEventInterventionCommand(
    long MatchingId,
    long ActorPlayerId,
    long EventInstanceId,
    int ExpectedStateVersion,
    int ChoiceId,
    int SelectedItemId);

public enum RoomEventCommandError
{
    None = 0,
    MatchingNotFound = 1,
    MatchingClosed = 2,
    InstanceNotFound = 3,
    AlreadyActive = 4,
    StaleVersion = 5,
    Inactive = 6,
    Expired = 7,
    InvalidLocation = 8,
    InvalidChoice = 9,
    InvalidResponseTag = 10,
    InvalidItem = 11,
    InvalidItemStage = 12,
    ContributionLimit = 13,
    ItemContributionLimit = 14,
    InsufficientItem = 15,
    ResourceCostFailed = 16,
    ExtensionLimit = 17,
    ManualResponseLimit = 18,
    InvalidState = 19
}

public interface IRoomEventActorGateway
{
    public bool IsPlayerInArea(long matchingId, long playerId, AreaType areaType);

    public bool TryApplyResourceDelta(
        long matchingId,
        long playerId,
        int staminaDelta,
        int mentalDelta);
}

public sealed record RoomEventActivationResult(
    bool Success,
    RoomEventCommandError Error,
    RoomEventWorldState? State);

public sealed record RoomEventCommandResult(
    bool Success,
    RoomEventCommandError Error,
    RoomEventWorldState? State,
    InGameItemInfo? ConsumedItem,
    int AppliedStaminaDelta,
    int AppliedMentalDelta);
