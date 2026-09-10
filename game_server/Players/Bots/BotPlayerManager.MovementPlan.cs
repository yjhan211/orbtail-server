using game_server.sessions;
using game_server.combat;
using game_server.items;
using game_server.logging;
using game_server.monsters;
using game_server.players;
using game_server.field;
using System.Collections.Immutable;
using System.Diagnostics;
using network.common;
using network.common.data;
using network.common.data.models;

namespace game_server.players.bots;

/// <summary>
///     매치 잠금 안에서 자신이 속한 매치의 봇 이동과 송신 계획을 만든다. 다른 매치는 조회하지 않는다. 패킷 생성·전송은
///     BotMovementService가 같은 잠금 안에서 계획 순서대로 한다.
/// </summary>
public partial class BotPlayerManager
{
    private long GetActiveMatchingId()
    {
        if (_released)
            throw new InvalidOperationException("Cannot process bot movement after the match has ended.");
        return _matchingId;
    }

    internal SwarmBotMovementPlan PrepareMovementTick(
        AreaClosureManager closures,
        InGameInventoryManager inventory,
        GroundItemManager groundItems,
        SummonStoneManager summonStones,
        EncounterRevealManager encounters,
        GameEventLogManager gameEventLogManager,
        IReadOnlyList<Player> players,
        IReadOnlyList<SwarmBotObserverSnapshot> observers,
        Func<long, long, SwarmBotDirective> directiveProvider)
    {
        ArgumentNullException.ThrowIfNull(observers);
        ArgumentNullException.ThrowIfNull(directiveProvider);

        long matchingId = GetActiveMatchingId();
        long snapshotStartedAt = Stopwatch.GetTimestamp();
        var playerAreas = new Dictionary<long, AreaType>();
        foreach (var player in players)
        {
            if (!player.IsEliminated)
                playerAreas.Add(player.PlayerId, player.CurrentArea);
        }
        double snapshotElapsedMilliseconds =
            Stopwatch.GetElapsedTime(snapshotStartedAt).TotalMilliseconds;
        IReadOnlyCollection<MonsterCombatTarget> pveTargets = [];
        BotPlayerManager.BotWalkingTickResult movementResult = ProcessBotMovementTick(
            matchingId,
            closures,
            playerAreas,
            inventory,
            groundItems,
            pveTargets,
            directiveProvider,
            summonStones);

        long preparationStartedAt = Stopwatch.GetTimestamp();
        SwarmBotMovementPlan plan = PrepareResult(
            inventory,
            encounters,
            gameEventLogManager,
            matchingId,
            movementResult.Movements,
            players,
            observers,
            advanceOrbOrbit: true,
            movementResult.PlanningElapsedMilliseconds,
            movementResult.WalkingElapsedMilliseconds);
        return plan with
        {
            SnapshotElapsedMilliseconds = snapshotElapsedMilliseconds,
            DispatchPreparationElapsedMilliseconds =
                Stopwatch.GetElapsedTime(preparationStartedAt).TotalMilliseconds
        };
    }

    /// <summary>
    ///     Freezes an already committed manual movement. Unlike the timer path, this deliberately does
    ///     not advance the orb orbit because the existing cut-dummy controls never did so.
    /// </summary>
    internal SwarmBotMovementPlan PrepareExternalMovement(
        InGameInventoryManager inventory,
        EncounterRevealManager encounters,
        GameEventLogManager gameEventLogManager,
        BotMovementEvent movement,
        IReadOnlyList<Player> players,
        IReadOnlyList<SwarmBotObserverSnapshot> observers)
    {
        ArgumentNullException.ThrowIfNull(movement);
        ArgumentNullException.ThrowIfNull(observers);
        long matchingId = GetActiveMatchingId();

        long preparationStartedAt = Stopwatch.GetTimestamp();
        SwarmBotMovementPlan plan = PrepareResult(
            inventory,
            encounters,
            gameEventLogManager,
            matchingId,
            [movement],
            players,
            observers,
            advanceOrbOrbit: false,
            planningElapsedMilliseconds: 0d,
            walkingElapsedMilliseconds: 0d);
        return plan with
        {
            DispatchPreparationElapsedMilliseconds =
                Stopwatch.GetElapsedTime(preparationStartedAt).TotalMilliseconds
        };
    }

    private SwarmBotMovementPlan PrepareResult(
        InGameInventoryManager inventory,
        EncounterRevealManager encounters,
        GameEventLogManager gameEventLogManager,
        long matchingId,
        IReadOnlyCollection<BotMovementEvent> movements,
        IReadOnlyList<Player> players,
        IReadOnlyList<SwarmBotObserverSnapshot> observers,
        bool advanceOrbOrbit,
        double planningElapsedMilliseconds,
        double walkingElapsedMilliseconds)
    {
        var movementDispatches = ImmutableArray.CreateBuilder<SwarmBotMovementDispatch>();
        foreach (BotMovementEvent movement in movements)
        {
            SwarmBotMovementDispatch? dispatch = PrepareMovement(
                encounters,
                gameEventLogManager,
                matchingId,
                movement,
                players,
                observers,
                advanceOrbOrbit);
            if (dispatch != null)
                movementDispatches.Add(dispatch);
        }

        return new SwarmBotMovementPlan(
            matchingId,
            movementDispatches.ToImmutable(),
            planningElapsedMilliseconds,
            walkingElapsedMilliseconds,
            SnapshotElapsedMilliseconds: 0d,
            DispatchPreparationElapsedMilliseconds: 0d);
    }

    private SwarmBotMovementDispatch? PrepareMovement(
        EncounterRevealManager encounters,
        GameEventLogManager gameEventLogManager,
        long matchingId,
        BotMovementEvent movement,
        IReadOnlyList<Player> players,
        IReadOnlyList<SwarmBotObserverSnapshot> observers,
        bool advanceOrbOrbit)
    {
        BotPlayerState? bot = GetBot(matchingId, movement.BotPlayerId);
        if (advanceOrbOrbit)
            bot?.Player.AdvanceOrbOrbit(movement.Position);

        long serverTimestamp = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
        if (movement.IsAreaTransition)
        {
            gameEventLogManager.LogMove(
                matchingId,
                movement.BotPlayerId,
                movement.FromArea.ToString(),
                movement.ToArea.ToString(),
                isBot: true);
        }

        ImmutableArray<GameClientSession> leaveRecipients = movement.IsAreaTransition
            ? SelectRecipients(observers, observer => observer.Area == movement.FromArea)
            : ImmutableArray<GameClientSession>.Empty;
        ImmutableArray<GameClientSession> destinationRecipients = SelectRecipients(
            observers,
            observer => observer.Area == movement.ToArea);

        SwarmBotPlayerInfoSnapshot? enteringBot = null;
        if (movement.IsAreaTransition)
        {
            PlayerInfo? botInfo = CreatePlayerInfo(matchingId, movement.BotPlayerId);
            GameObjectInfo? objectInfo = SynthesizeGameObjectInfo(matchingId, movement.BotPlayerId);
            if (botInfo != null && objectInfo != null)
                enteringBot = SwarmBotPlayerInfoSnapshot.Capture(botInfo, objectInfo);
        }

        float orbOrbitPhase = bot?.Player.OrbOrbitPhaseDegrees
                              ?? SwarmOrbOrbit.InitialPhaseDegrees(movement.BotPlayerId);
        SwarmBotEncounterDispatch? encounter = PrepareEncounter(
            encounters,
            matchingId,
            movement,
            players,
            observers);

        return new SwarmBotMovementDispatch(
            movement.BotPlayerId,
            movement.IsAreaTransition,
            movement.ToArea,
            SwarmCellSnapshot.Capture(movement.ToCell),
            SwarmVectorSnapshot.Capture(movement.Position),
            SwarmVectorSnapshot.Capture(movement.Velocity),
            movement.Rotation,
            serverTimestamp,
            orbOrbitPhase,
            leaveRecipients,
            destinationRecipients,
            enteringBot,
            encounter);
    }

    private SwarmBotEncounterDispatch? PrepareEncounter(
        EncounterRevealManager encounters,
        long matchingId,
        BotMovementEvent movement,
        IReadOnlyList<Player> players,
        IReadOnlyList<SwarmBotObserverSnapshot> observers)
    {
        if (!movement.ToArea.IsCorridor())
            return null;

        var candidates = new List<(long PlayerId, Vector3f Position)>();
        foreach (var player in players)
        {
            if (player.PlayerId == movement.BotPlayerId || player.IsEliminated ||
                player.CurrentArea != movement.ToArea || player.Position == null)
                continue;
            candidates.Add((player.PlayerId, player.Position));
        }
        if (candidates.Count == 0)
            return null;

        CorridorEncounterDecision decision = encounters.ResolveCorridorEncounter(
            movement.BotPlayerId,
            new Vector3f(movement.Position.X, movement.Position.Y, movement.Position.Z),
            candidates,
            0,
            0);
        if (!decision.HasEvent)
            return null;

        GameClientSession? targetSession = null;
        foreach (SwarmBotObserverSnapshot observer in observers)
        {
            if (observer.PlayerId == decision.TargetPlayerId)
            {
                targetSession = observer.Session;
                break;
            }
        }

        return new SwarmBotEncounterDispatch(
            targetSession,
            decision.TargetPlayerId,
            movement.BotPlayerId,
            movement.ToArea,
            decision.EventType,
            decision.CooldownSeconds,
            decision.RevealDelayMs);
    }

    private static ImmutableArray<GameClientSession> SelectRecipients(
        IReadOnlyList<SwarmBotObserverSnapshot> observers,
        Func<SwarmBotObserverSnapshot, bool> predicate)
    {
        var recipients = ImmutableArray.CreateBuilder<GameClientSession>();
        foreach (SwarmBotObserverSnapshot observer in observers)
        {
            if (predicate(observer))
                recipients.Add(observer.Session);
        }

        return recipients.ToImmutable();
    }

}
