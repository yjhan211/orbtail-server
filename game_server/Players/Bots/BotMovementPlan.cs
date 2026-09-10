using System.Collections.Immutable;
using game_server.sessions;
using network.common;
using network.common.data.models;

namespace game_server.players.bots;

internal readonly record struct SwarmVectorSnapshot(float X, float Y, float Z)
{
    public static SwarmVectorSnapshot Capture(Vector3f value) => new(value.X, value.Y, value.Z);
    public Vector3f ToVector3f() => new(X, Y, Z);
}

internal readonly record struct SwarmCellSnapshot(int X, int Y)
{
    public static SwarmCellSnapshot Capture(Cell value) => new(value.X, value.Y);
    public Cell ToCell() => new(X, Y);
}

internal readonly record struct SwarmBotObserverSnapshot(
    GameClientSession Session,
    long PlayerId,
    AreaType Area);

internal sealed record SwarmBotPlayerInfoSnapshot(
    long PlayerId,
    string Name,
    ImmutableArray<int> WearItemIds,
    PlayerState State,
    long Gold,
    int Hp,
    MapId MapId,
    long MapSubId,
    SwarmCellSnapshot Cell,
    SwarmVectorSnapshot Position,
    SwarmVectorSnapshot Velocity,
    float Rotation,
    bool IsFlip,
    bool IsNew)
{
    public static SwarmBotPlayerInfoSnapshot Capture(PlayerInfo info, GameObjectInfo objectInfo) => new(
        info.PlayerId,
        info.Name,
        info.WearItemIdList.ToImmutableArray(),
        objectInfo.State,
        info.Gold,
        info.Hp,
        objectInfo.MapId,
        objectInfo.MapSubId,
        SwarmCellSnapshot.Capture(objectInfo.Cell),
        SwarmVectorSnapshot.Capture(objectInfo.Position),
        SwarmVectorSnapshot.Capture(objectInfo.Velocity),
        objectInfo.Rotation,
        objectInfo.IsFlip,
        info.IsNew);

    public PlayerInfo ToPlayerInfo() => new()
    {
        PlayerId = PlayerId,
        Name = Name,
        WearItemIdList = WearItemIds.ToList(),
        State = State,
        Gold = Gold,
        Hp = Hp,
        IsNew = IsNew
    };

    public GameObjectInfo ToGameObjectInfo() => new(ObjectType.PLAYER, PlayerId, MapId, MapSubId, Cell.ToCell(), IsFlip)
    {
        Position = Position.ToVector3f(),
        Velocity = Velocity.ToVector3f(),
        Rotation = Rotation,
        State = State
    };
}

internal sealed record SwarmBotMovementPlan(
    long MatchingId,
    ImmutableArray<SwarmBotMovementDispatch> Movements,
    double PlanningElapsedMilliseconds,
    double WalkingElapsedMilliseconds,
    double SnapshotElapsedMilliseconds,
    double DispatchPreparationElapsedMilliseconds);

internal sealed record SwarmBotMovementDispatch(
    long BotPlayerId,
    bool IsAreaTransition,
    AreaType ToArea,
    SwarmCellSnapshot ToCell,
    SwarmVectorSnapshot Position,
    SwarmVectorSnapshot Velocity,
    float Rotation,
    long ServerTimestamp,
    float OrbOrbitPhaseDegrees,
    ImmutableArray<GameClientSession> LeaveRecipients,
    ImmutableArray<GameClientSession> DestinationRecipients,
    SwarmBotPlayerInfoSnapshot? EnteringBot,
    SwarmBotEncounterDispatch? Encounter);

internal sealed record SwarmBotEncounterDispatch(
    GameClientSession? TargetSession,
    long TargetPlayerId,
    long BotPlayerId,
    AreaType Area,
    int EventType,
    int CooldownSeconds,
    int RevealDelayMs);
