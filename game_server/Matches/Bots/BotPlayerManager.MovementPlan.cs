using game_server.sessions;
using game_server.matches.combat;
using game_server.matches.items;
using game_server.matches.logging;
using game_server.matches.monsters;
using game_server.players;
using game_server.matches.field;
using System.Collections.Immutable;
using System.Diagnostics;
using network.common;
using network.common.data;
using network.common.data.models;

namespace game_server.matches.bots;

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
        IReadOnlyList<SwarmBotObserverSnapshot> observers,
        Func<long, long, SwarmBotDirective> directiveProvider)
    {
        ArgumentNullException.ThrowIfNull(observers);
        ArgumentNullException.ThrowIfNull(directiveProvider);

        long matchingId = GetActiveMatchingId();
        long snapshotStartedAt = Stopwatch.GetTimestamp();
        var humanAreas = observers.ToDictionary(observer => observer.PlayerId, observer => observer.Area);
        double snapshotElapsedMilliseconds =
            Stopwatch.GetElapsedTime(snapshotStartedAt).TotalMilliseconds;
        IReadOnlyCollection<MonsterCombatTarget> pveTargets = [];
        BotPlayerManager.BotWalkingTickResult movementResult = ProcessBotMovementTick(
            matchingId,
            closures,
            humanAreas,
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
            movementResult.GroundItemPickups,
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
            [],
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
        IReadOnlyCollection<BotGroundItemPickup> pickups,
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
                observers,
                advanceOrbOrbit);
            if (dispatch != null)
                movementDispatches.Add(dispatch);
        }

        var removals = ImmutableArray.CreateBuilder<SwarmBotGroundItemRemovalDispatch>();
        foreach (BotGroundItemPickup pickup in pickups)
        {
            LogGroundItemPickup(inventory, gameEventLogManager, matchingId, pickup);
            var area = (AreaType)pickup.Item.AreaType;
            removals.Add(new SwarmBotGroundItemRemovalDispatch(
                pickup.Item.GroundItemUid,
                pickup.BotPlayerId,
                pickup.AutoUsed,
                SelectRecipients(observers, observer => observer.Area == area)));
        }

        return new SwarmBotMovementPlan(
            matchingId,
            movementDispatches.ToImmutable(),
            removals.ToImmutable(),
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
        IReadOnlyList<SwarmBotObserverSnapshot> observers,
        bool advanceOrbOrbit)
    {
        BotPlayerState? bot = GetBot(matchingId, movement.BotPlayerId);
        if (advanceOrbOrbit)
            bot?.AdvanceOrbOrbit(movement.Position);

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

        // Existing behavior records a transition even when no human can observe it, but performs no
        // packet preparation or encounter roll in that case.
        if (observers.Count == 0)
            return null;

        ImmutableArray<GameClientSession> leaveRecipients = movement.IsAreaTransition
            ? SelectRecipients(observers, observer => observer.Area == movement.FromArea)
            : ImmutableArray<GameClientSession>.Empty;
        ImmutableArray<GameClientSession> destinationRecipients = SelectRecipients(
            observers,
            observer => observer.Area == movement.ToArea);

        SwarmBotPlayerInfoSnapshot? enteringBot = null;
        if (movement.IsAreaTransition)
        {
            PlayerInfo? botInfo = SynthesizePlayerInfo(matchingId, movement.BotPlayerId);
            GameObjectInfo? objectInfo = SynthesizeGameObjectInfo(matchingId, movement.BotPlayerId);
            if (botInfo != null && objectInfo != null)
                enteringBot = SwarmBotPlayerInfoSnapshot.Capture(botInfo, objectInfo);
        }

        float orbOrbitPhase = bot?.OrbOrbitPhaseDegrees
                              ?? SwarmOrbOrbit.InitialPhaseDegrees(movement.BotPlayerId);
        SwarmBotEncounterDispatch? encounter = PrepareEncounter(
            encounters,
            matchingId,
            movement,
            observers,
            bot);

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
        IReadOnlyList<SwarmBotObserverSnapshot> observers,
        BotPlayerState? bot)
    {
        if (!movement.ToArea.IsCorridor())
            return null;

        var candidates = observers
            .Where(observer =>
                !observer.IsEliminated &&
                observer.Area == movement.ToArea &&
                observer.Position.HasValue)
            .Select(observer => (observer.PlayerId, observer.Position!.Value.ToVector3f()))
            .ToList();
        if (candidates.Count == 0)
            return null;

        CorridorEncounterDecision decision = encounters.ResolveCorridorEncounter(
            movement.BotPlayerId,
            new Vector3f(movement.Position.X, movement.Position.Y, movement.Position.Z),
            candidates,
            PassiveBuffUtility.GetValuePercent(
                bot?.ActiveBuffIds ?? [],
                BuffSubType.RISK_EVENT_CHANCE_DOWN),
            PassiveBuffUtility.GetValuePercent(
                bot?.ActiveBuffIds ?? [],
                BuffSubType.ENCOUNTER_ESCAPE_CHANCE_ADD));
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
        if (targetSession == null)
            return null;

        return new SwarmBotEncounterDispatch(
            targetSession,
            decision.TargetPlayerId,
            movement.BotPlayerId,
            movement.ToArea,
            decision.EventType,
            decision.CooldownSeconds,
            decision.RevealDelayMs);
    }

    private void LogGroundItemPickup(InGameInventoryManager inventory, GameEventLogManager gameEventLogManager, long matchingId, BotGroundItemPickup pickup)
    {
        if (pickup.HealthRecovery > 0)
            gameEventLogManager.RecordRecovery(matchingId, pickup.BotPlayerId, pickup.HealthRecovery);
        if (pickup.AutoUsed)
        {
            gameEventLogManager.LogRecoveryUse(
                matchingId,
                pickup.BotPlayerId,
                pickup.Item.ItemId,
                pickup.EffectiveRecovery,
                source: "ground_auto_use",
                isBot: true);
            gameEventLogManager.LogPelletPickupOutcome(
                matchingId,
                pickup.BotPlayerId,
                pickup.Item.ItemId,
                pickup.RequestedRecovery,
                pickup.EffectiveRecovery,
                pickup.EffectiveRecovery == 0 ? "wasted" :
                pickup.EffectiveRecovery == pickup.RequestedRecovery ? "effective" : "partial_waste",
                isBot: true);
        }

        var area = (AreaType)pickup.Item.AreaType;
        gameEventLogManager.LogGroundItemPickup(
            matchingId,
            pickup.BotPlayerId,
            pickup.DiscovererPlayerId,
            pickup.Item.GroundItemUid,
            pickup.Item.ItemId,
            area.ToString(),
            pickup.AutoUsed,
            isBot: true);
        if (pickup.SummonStoneAmount > 0)
        {
            gameEventLogManager.LogSummonStoneAward(
                matchingId,
                pickup.BotPlayerId,
                monsterId: 0,
                pickup.SummonStoneAmount,
                pickup.SummonStoneBalance,
                area.ToString(),
                isCore: false,
                isBot: true);
            return;
        }

        var boardAfterPickup = inventory.GetPlayerInventory(pickup.BotPlayerId);
        gameEventLogManager.LogOrbBoardTransition(
            matchingId,
            pickup.BotPlayerId,
            boardAfterPickup.GetAllItems(),
            boardAfterPickup.GetOrderedOrbs().FirstOrDefault()?.ItemId ?? 0,
            area.ToString(),
            "pickup",
            isBot: true);
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
