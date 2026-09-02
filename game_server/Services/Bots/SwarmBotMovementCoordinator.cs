using System.Collections.Concurrent;
using System.Collections.Immutable;
using System.Diagnostics;
using network.common;
using network.common.data;
using network.common.data.models;

namespace game_server.services;

/// <summary>
///     Commits authoritative bot movement state and freezes the matching outbound projection while
///     the caller owns the match runtime monitor. Packet construction and transport stay in GameServer.
/// </summary>
internal sealed class SwarmBotMovementCoordinator(
    BotPlayerManager botPlayerManager,
    AreaClosureManager areaClosureManager,
    AreaItemStockManager areaItemStockManager,
    InGameInventoryManager inGameInventoryManager,
    GroundItemManager groundItemManager,
    SummonStoneManager summonStoneManager,
    EncounterRevealManager encounterRevealManager,
    GameEventLogManager gameEventLogManager)
{
    private readonly ConcurrentDictionary<long, PublicationSequenceState> _publicationSequences = new();

    /// <summary>
    ///     Reserves the next wire-publication turn. Callers invoke this only after a complete plan
    ///     has been frozen, while they still own the matching runtime monitor.
    /// </summary>
    public SwarmBotPublicationTicket ReservePublication(long matchingId)
    {
        if (matchingId <= 0)
            throw new ArgumentOutOfRangeException(nameof(matchingId));

        PublicationSequenceState state = _publicationSequences.GetOrAdd(
            matchingId,
            static _ => new PublicationSequenceState());
        long sequence = Interlocked.Increment(ref state.NextTicket) - 1;
        return new SwarmBotPublicationTicket(matchingId, sequence);
    }

    /// <summary>
    ///     Regular timer and manual callers wait outside the matching runtime monitor. The dev
    ///     auto-sandbox caller is the explicit monitor-held exception that preserves legacy arena
    ///     packet order. The wait gate is never held during packet construction or Send, and a failed
    ///     publication always retires its turn so later committed plans cannot deadlock.
    /// </summary>
    public void DispatchInOrder(SwarmBotPublicationTicket ticket, Action dispatch)
    {
        ArgumentNullException.ThrowIfNull(dispatch);
        if (!_publicationSequences.TryGetValue(ticket.MatchingId, out PublicationSequenceState? state))
            throw new InvalidOperationException(
                $"Missing bot publication sequence for matching {ticket.MatchingId}.");

        lock (state.WaitGate)
        {
            while (state.ServingTicket < ticket.Sequence)
                Monitor.Wait(state.WaitGate);
            if (state.ServingTicket != ticket.Sequence)
            {
                throw new InvalidOperationException(
                    $"Bot publication ticket {ticket.Sequence} was already retired for matching " +
                    $"{ticket.MatchingId}.");
            }
        }

        try
        {
            dispatch();
        }
        finally
        {
            lock (state.WaitGate)
            {
                state.ServingTicket++;
                Monitor.PulseAll(state.WaitGate);
            }
        }
    }

    /// <summary>
    ///     Terminal component cleanup calls this only after every operation lease has drained.
    /// </summary>
    public void ClearMatching(long matchingId) =>
        _publicationSequences.TryRemove(matchingId, out _);

    public SwarmBotMovementPlan PrepareTick(
        long matchingId,
        IReadOnlyList<SwarmBotObserverSnapshot> observers,
        Func<long, long, SwarmBotDirective> directiveProvider)
    {
        ArgumentNullException.ThrowIfNull(observers);
        ArgumentNullException.ThrowIfNull(directiveProvider);

        long snapshotStartedAt = Stopwatch.GetTimestamp();
        var humanAreas = observers.ToDictionary(observer => observer.PlayerId, observer => observer.Area);
        double snapshotElapsedMilliseconds =
            Stopwatch.GetElapsedTime(snapshotStartedAt).TotalMilliseconds;
        IReadOnlyCollection<MonsterCombatTarget> pveTargets = [];
        BotPlayerManager.BotWalkingTickResult movementResult = botPlayerManager.ProcessBotMovementTick(
            matchingId,
            areaClosureManager,
            areaItemStockManager,
            humanAreas,
            inGameInventoryManager,
            groundItemManager,
            pveTargets,
            directiveProvider,
            summonStoneManager);

        long preparationStartedAt = Stopwatch.GetTimestamp();
        SwarmBotMovementPlan plan = PrepareResult(
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
    public SwarmBotMovementPlan PrepareExternalMovement(
        long matchingId,
        BotMovementEvent movement,
        IReadOnlyList<SwarmBotObserverSnapshot> observers)
    {
        ArgumentNullException.ThrowIfNull(movement);
        ArgumentNullException.ThrowIfNull(observers);

        long preparationStartedAt = Stopwatch.GetTimestamp();
        SwarmBotMovementPlan plan = PrepareResult(
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
            LogGroundItemPickup(matchingId, pickup);
            var area = (AreaType)pickup.Item.AreaType;
            removals.Add(new SwarmBotGroundItemRemovalDispatch(
                pickup.Item.GroundItemUid,
                pickup.BotPlayerId,
                pickup.AutoUsed,
                RecipientOrdinals(observers, observer => observer.Area == area)));
        }

        var autoEquips = ImmutableArray.CreateBuilder<SwarmBotPlayerInfoDispatch>();
        foreach (BotGroundItemPickup pickup in pickups)
        {
            if (pickup.AutoEquippedItemId <= 0)
                continue;

            BotPlayerState? bot = botPlayerManager.GetBot(matchingId, pickup.BotPlayerId);
            PlayerInfo? botInfo = botPlayerManager.SynthesizePlayerInfo(matchingId, pickup.BotPlayerId);
            if (bot == null || botInfo == null)
                continue;

            ImmutableArray<int> recipients = RecipientOrdinals(
                observers,
                observer => observer.Area == bot.CurrentArea);
            if (recipients.IsEmpty)
                continue;

            autoEquips.Add(new SwarmBotPlayerInfoDispatch(
                SwarmBotPlayerInfoSnapshot.Capture(botInfo),
                recipients));
        }

        return new SwarmBotMovementPlan(
            matchingId,
            movementDispatches.ToImmutable(),
            removals.ToImmutable(),
            autoEquips.ToImmutable(),
            planningElapsedMilliseconds,
            walkingElapsedMilliseconds,
            SnapshotElapsedMilliseconds: 0d,
            DispatchPreparationElapsedMilliseconds: 0d);
    }

    private SwarmBotMovementDispatch? PrepareMovement(
        long matchingId,
        BotMovementEvent movement,
        IReadOnlyList<SwarmBotObserverSnapshot> observers,
        bool advanceOrbOrbit)
    {
        BotPlayerState? bot = botPlayerManager.GetBot(matchingId, movement.BotPlayerId);
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

        ImmutableArray<int> leaveRecipients = movement.IsAreaTransition
            ? RecipientOrdinals(observers, observer => observer.Area == movement.FromArea)
            : ImmutableArray<int>.Empty;
        ImmutableArray<int> destinationRecipients = RecipientOrdinals(
            observers,
            observer => observer.Area == movement.ToArea);

        SwarmBotPlayerInfoSnapshot? enteringBot = null;
        if (movement.IsAreaTransition)
        {
            PlayerInfo? botInfo = botPlayerManager.SynthesizePlayerInfo(matchingId, movement.BotPlayerId);
            if (botInfo != null)
                enteringBot = SwarmBotPlayerInfoSnapshot.Capture(botInfo);
        }

        float orbOrbitPhase = bot?.OrbOrbitPhaseDegrees
                              ?? SwarmOrbOrbit.InitialPhaseDegrees(movement.BotPlayerId);
        SwarmBotEncounterDispatch? encounter = PrepareEncounter(
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

        CorridorEncounterDecision decision = encounterRevealManager.ResolveCorridorEncounter(
            matchingId,
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

        int targetOrdinal = -1;
        foreach (SwarmBotObserverSnapshot observer in observers)
        {
            if (observer.PlayerId == decision.TargetPlayerId)
            {
                targetOrdinal = observer.SessionOrdinal;
                break;
            }
        }
        if (targetOrdinal < 0)
            return null;

        return new SwarmBotEncounterDispatch(
            targetOrdinal,
            decision.TargetPlayerId,
            movement.BotPlayerId,
            movement.ToArea,
            decision.EventType,
            decision.CooldownSeconds,
            decision.RevealDelayMs);
    }

    private void LogGroundItemPickup(long matchingId, BotGroundItemPickup pickup)
    {
        if (pickup.CorruptionRecovery > 0)
            gameEventLogManager.RecordRecovery(matchingId, pickup.BotPlayerId, pickup.CorruptionRecovery);
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

        var boardAfterPickup = inGameInventoryManager.GetPlayerInventory(matchingId, pickup.BotPlayerId);
        gameEventLogManager.LogOrbBoardTransition(
            matchingId,
            pickup.BotPlayerId,
            boardAfterPickup.GetAllItems(),
            boardAfterPickup.GetEquippedBattleItem()?.ItemId ?? 0,
            area.ToString(),
            "pickup",
            isBot: true);
    }

    private static ImmutableArray<int> RecipientOrdinals(
        IReadOnlyList<SwarmBotObserverSnapshot> observers,
        Func<SwarmBotObserverSnapshot, bool> predicate)
    {
        var recipients = ImmutableArray.CreateBuilder<int>();
        foreach (SwarmBotObserverSnapshot observer in observers)
        {
            if (predicate(observer))
                recipients.Add(observer.SessionOrdinal);
        }

        return recipients.ToImmutable();
    }

    private sealed class PublicationSequenceState
    {
        public readonly object WaitGate = new();
        public long NextTicket;
        public long ServingTicket;
    }
}

internal readonly record struct SwarmBotPublicationTicket(long MatchingId, long Sequence);

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
    int SessionOrdinal,
    long PlayerId,
    AreaType Area,
    bool IsEliminated,
    SwarmVectorSnapshot? Position);

internal sealed record SwarmBotPlayerInfoSnapshot(
    long PlayerId,
    string Name,
    ImmutableArray<int> WearItemIds,
    PlayerState State,
    long Gold,
    int Hp,
    int Stamina,
    MapId LastMapId,
    long LastMapSubId,
    SwarmCellSnapshot LastCell,
    bool IsNew)
{
    public static SwarmBotPlayerInfoSnapshot Capture(PlayerInfo info) => new(
        info.PlayerId,
        info.Name,
        info.WearItemIdList.ToImmutableArray(),
        info.State,
        info.Gold,
        info.Hp,
        info.Stamina,
        info.LastMapId,
        info.LastMapSubId,
        SwarmCellSnapshot.Capture(info.LastCell),
        info.IsNew);

    public PlayerInfo ToPlayerInfo() => new()
    {
        PlayerId = PlayerId,
        Name = Name,
        WearItemIdList = WearItemIds.ToList(),
        State = State,
        Gold = Gold,
        Hp = Hp,
        Stamina = Stamina,
        LastMapId = LastMapId,
        LastMapSubId = LastMapSubId,
        LastCell = LastCell.ToCell(),
        IsNew = IsNew
    };
}

internal sealed record SwarmBotMovementPlan(
    long MatchingId,
    ImmutableArray<SwarmBotMovementDispatch> Movements,
    ImmutableArray<SwarmBotGroundItemRemovalDispatch> GroundItemRemovals,
    ImmutableArray<SwarmBotPlayerInfoDispatch> AutoEquips,
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
    ImmutableArray<int> LeaveRecipientOrdinals,
    ImmutableArray<int> DestinationRecipientOrdinals,
    SwarmBotPlayerInfoSnapshot? EnteringBot,
    SwarmBotEncounterDispatch? Encounter);

internal sealed record SwarmBotEncounterDispatch(
    int TargetSessionOrdinal,
    long TargetPlayerId,
    long BotPlayerId,
    AreaType Area,
    int EventType,
    int CooldownSeconds,
    int RevealDelayMs);

internal sealed record SwarmBotGroundItemRemovalDispatch(
    long GroundItemUid,
    long BotPlayerId,
    bool AutoUsed,
    ImmutableArray<int> RecipientOrdinals);

internal sealed record SwarmBotPlayerInfoDispatch(
    SwarmBotPlayerInfoSnapshot PlayerInfo,
    ImmutableArray<int> RecipientOrdinals);
