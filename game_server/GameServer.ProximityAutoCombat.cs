using System.Collections.Concurrent;
using System.Collections.Immutable;
using System.Runtime.ExceptionServices;
using game_server.network;
using game_server.services;
using MessagePack;
using Microsoft.Extensions.Logging;
using network.common;
using network.common.data;
using network.common.data.models;
using network.packets;

namespace game_server;

public partial class GameServer
{
    private const int ProximityAutoCombatTickIntervalMs = 50;

    private readonly ProximityAutoCombatResolver _proximityAutoCombatResolver = new();
    private readonly ConcurrentDictionary<(long MatchingId, long ObserverPlayerId, long ActorPlayerId),
        OrbVisualState> _orbVisualStates = new();
    private readonly ConcurrentDictionary<(long MatchingId, long PlayerId, long ItemUid, int StackIndex), DateTime>
        _orbRecoveryReadyAtUtc = new();
    private Timer? _proximityAutoCombatTimer;

    private void StartProximityAutoCombatTimer()
    {
        _proximityAutoCombatTimer = new Timer(
            ProcessProximityAutoCombatTick,
            null,
            TimeSpan.FromMilliseconds(ProximityAutoCombatTickIntervalMs),
            TimeSpan.FromMilliseconds(ProximityAutoCombatTickIntervalMs));
        logger.LogInformation(
            "Proximity auto combat timer started: TickMs={TickMs}, AimMilliseconds={AimMilliseconds}",
            ProximityAutoCombatTickIntervalMs,
            ProximityAutoCombatResolver.AimDuration.TotalMilliseconds);
    }

    private void ProcessProximityAutoCombatTick(object? state)
    {
        List<GameClientSession> activeSessions;
        List<long> activeMatchingIds;
        try
        {
            activeSessions = _sessionRegistry.SnapshotWhere(
                static session =>
                    session.PlayerId.HasValue &&
                    !session.IsEliminated &&
                    !session.IsGameEnded);
            activeMatchingIds = GetActiveMatchingIds();
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "Proximity auto combat snapshot failed");
            return;
        }

        foreach (long matchingId in activeMatchingIds)
        {
            try
            {
                // 인트로 예열 (2026-08-16 유저 결정): 카운트다운 동안에도 스웜은 돈다 —
                // 운동장에서 각 방으로 나가는 몹이 그 5초의 볼거리이기 때문이다.
                // 매치 시작 게이트로 막으면 몹이 아예 태어나지 않으므로 여기서는 거르지
                // 않고, 게이트 전 전투 차단은 ProcessProximityAutoCombatForMatching 안이 맡는다.
                SwarmCombatPublicationCoordinator.PublicationTurn? publicationTurn =
                    _swarmCombatPublicationCoordinator.TryBeginDueRealtimeTurn(matchingId);
                if (publicationTurn == null)
                    continue;

                PrepareAndDispatchCombatPublication(
                    matchingId,
                    publicationTurn,
                    () => ProcessProximityAutoCombatForMatching(matchingId, activeSessions));
            }
            catch (Exception ex)
            {
                logger.LogError(
                    ex,
                    "Proximity auto combat tick failed: MatchingId={MatchingId}",
                    matchingId);
            }
        }
    }

    private void ProcessProximityAutoCombatForMatching(
        long matchingId,
        List<GameClientSession> activeSessions)
    {
        ProcessSwarmArenaForMatching(matchingId, activeSessions);
    }

    /// <summary>
    ///     Holds the match publication turn and terminal operation lease across outside-monitor
    ///     dispatch. Authoritative state, logs, recipients, packet bytes, and deferred visual steps
    ///     are all prepared while TryAcquireOperation owns the matching runtime monitor.
    /// </summary>
    private void PrepareAndDispatchCombatPublication(
        long matchingId,
        SwarmCombatPublicationCoordinator.PublicationTurn publicationTurn,
        Action prepare)
    {
        IDisposable? runtimeOperation = null;
        PrepareAndDispatchMatchPublication(
            matchingId,
            publicationTurn,
            (captureActivePreparation, _) =>
            {
                runtimeOperation = _matchRuntimeRegistry.TryAcquireOperation(
                    matchingId,
                    captureActivePreparation);
                return runtimeOperation != null;
            },
            prepare,
            static () => { },
            () => runtimeOperation?.Dispose());
    }

    /// <summary>
    ///     Publishes a player-message mutation with the operation lease already borrowed from
    ///     SessionBase. The shared ordered turn is acquired before the nested runtime monitor; a
    ///     runtime that became finalizing while the outer lease was held prepares its rejection in
    ///     the same lane. Dispatch and turn retirement complete before the message scope releases
    ///     that outer lease.
    /// </summary>
    private void PublishOrderedSessionPublication(
        long matchingId,
        Action prepare,
        Action prepareFinalizingRejection)
    {
        ArgumentNullException.ThrowIfNull(prepare);
        ArgumentNullException.ThrowIfNull(prepareFinalizingRejection);

        SwarmCombatPublicationCoordinator.PublicationTurn publicationTurn =
            _swarmCombatPublicationCoordinator.BeginOrderedTurn(matchingId) ??
            throw new InvalidOperationException(
                $"Missing ordered publication runtime for matching {matchingId}.");

        PrepareAndDispatchMatchPublication(
            matchingId,
            publicationTurn,
            (captureActivePreparation, captureFinalizingRejection) =>
            {
                if (_matchRuntimeRegistry.TryExecute(
                        matchingId,
                        captureActivePreparation))
                {
                    return true;
                }

                captureFinalizingRejection();
                return true;
            },
            prepare,
            prepareFinalizingRejection,
            releaseOwnedRuntimeOperation: null);
    }

    /// <summary>
    ///     Shared capture/freeze/dispatch failure boundary. Timer callers supply an owned operation
    ///     lease and release it after the turn; message callers supply a nested TryExecute gate and
    ///     leave their borrowed outer lease untouched.
    /// </summary>
    private void PrepareAndDispatchMatchPublication(
        long matchingId,
        SwarmCombatPublicationCoordinator.PublicationTurn publicationTurn,
        Func<Action, Action, bool> tryPrepare,
        Action prepare,
        Action prepareFinalizingRejection,
        Action? releaseOwnedRuntimeOperation)
    {
        SwarmCombatPublicationCoordinator.PublicationPlan? publicationPlan = null;
        ExceptionDispatchInfo? preparationFailure = null;
        ExceptionDispatchInfo? pendingFailure = null;
        List<Exception>? secondaryFailures = null;

        void RecordFailure(Exception failure)
        {
            if (pendingFailure == null)
                pendingFailure = ExceptionDispatchInfo.Capture(failure);
            else
            {
                secondaryFailures ??= [];
                secondaryFailures.Add(failure);
            }
        }

        void CapturePreparation(Action preparation)
        {
            SwarmCombatPublicationCoordinator.CaptureScope? capture = null;
            try
            {
                capture = _swarmCombatPublicationCoordinator.BeginCapture(publicationTurn);
                try
                {
                    preparation();
                }
                catch (Exception ex)
                {
                    // Freeze and dispatch the already committed authoritative prefix before
                    // the original preparation exception reaches the message/timer boundary.
                    preparationFailure = ExceptionDispatchInfo.Capture(ex);
                }

                try
                {
                    publicationPlan = capture.Freeze();
                }
                catch (Exception ex)
                {
                    if (preparationFailure != null && pendingFailure == null)
                        pendingFailure = preparationFailure;
                    RecordFailure(ex);
                }
            }
            catch (Exception ex)
            {
                RecordFailure(ex);
            }
            finally
            {
                try
                {
                    capture?.Dispose();
                }
                catch (Exception ex)
                {
                    RecordFailure(ex);
                }
            }
        }

        try
        {
            bool prepared = tryPrepare(
                () => CapturePreparation(prepare),
                () => CapturePreparation(prepareFinalizingRejection));

            if (prepared)
            {
                if (preparationFailure != null &&
                    !ReferenceEquals(pendingFailure, preparationFailure))
                {
                    if (pendingFailure != null)
                    {
                        secondaryFailures ??= [];
                        secondaryFailures.Add(pendingFailure.SourceException);
                    }

                    pendingFailure = preparationFailure;
                }

                if (publicationPlan == null)
                {
                    if (pendingFailure == null)
                    {
                        RecordFailure(
                            new InvalidOperationException(
                                "Combat publication operation completed without a frozen plan."));
                    }
                }
                else
                {
                    try
                    {
                        _swarmCombatPublicationCoordinator.DispatchAndRetire(
                            publicationTurn,
                            publicationPlan);
                    }
                    catch (Exception ex)
                    {
                        // Preparation remains authoritative when both it and prefix transport fail.
                        RecordFailure(ex);
                    }
                }
            }
        }
        catch (Exception ex)
        {
            if (preparationFailure != null && pendingFailure == null)
                pendingFailure = preparationFailure;
            RecordFailure(ex);
        }

        try
        {
            // Retire the publication before releasing the operation lease. A deferred terminal
            // finalization may clear the coordinator as soon as the lease reaches zero.
            publicationTurn.Dispose();
        }
        catch (Exception ex)
        {
            RecordFailure(ex);
        }

        try
        {
            releaseOwnedRuntimeOperation?.Invoke();
        }
        catch (Exception ex)
        {
            RecordFailure(ex);
        }

        if (preparationFailure != null &&
            ReferenceEquals(pendingFailure, preparationFailure))
        {
            if (secondaryFailures != null)
            {
                foreach (Exception secondaryFailure in secondaryFailures)
                {
                    try
                    {
                        logger.LogError(
                            secondaryFailure,
                            "Match publication cleanup or prefix dispatch also failed: MatchingId={MatchingId}",
                            matchingId);
                    }
                    catch
                    {
                        // Logging must not mask the authoritative preparation exception.
                    }
                }
            }

            preparationFailure!.Throw();
        }

        if (pendingFailure != null && secondaryFailures is { Count: > 0 })
        {
            throw new AggregateException(
                "Match publication and cleanup both failed.",
                [pendingFailure.SourceException, .. secondaryFailures]);
        }

        pendingFailure?.Throw();
    }
    private static void AddInventoryCombatActors(
ICollection<ProximityCombatActor> actors,
ProximityCombatActor spatialActor,
PlayerInGameInventory inventory)
    {
        var equippedItem = inventory.GetEquippedBattleItem();
        bool addedBoardOrb = false;

        int attackSlotIndex = 0;
        foreach (var item in inventory.GetAllItems()
                     .Where(item => item.Count > 0)
                     .OrderBy(item => item.ItemUid))
        {
            if (OrbData.IsRecoveryOrb(item.ItemId))
            {
                for (int stackIndex = 0; stackIndex < item.Count; stackIndex++)
                {
                    actors.Add(spatialActor with
                    {
                        WeaponItemId = item.ItemId,
                        WeaponItemUid = item.ItemUid,
                        WeaponStackIndex = stackIndex
                    });
                    addedBoardOrb = true;
                }
                continue;
            }

            if (!OrbData.TryGetColorAndTier(item.ItemId, out var orbColor, out _))
                continue;

            var combatData = BattleItemCombatData.Get(item.ItemId);
            if (combatData == null)
                continue;

            bool windActive = false;
            int sunStage = 0;
            bool waveArmed = false;
            bool orbEffectActive = false;

            for (int stackIndex = 0; stackIndex < item.Count; stackIndex++)
            {
                actors.Add(spatialActor with
                {
                    WeaponItemId = item.ItemId,
                    AttackRange = OrbData.GetAttackPattern(item.ItemId) ==
                                  OrbAttackPattern.AttackerArea
                        ? OrbData.GetWindPulseRadius(item.ItemId)
                        : combatData.AttackRange *
                          (windActive ? OrbData.WindAttackRangeMultiplier : 1f),
                    Damage = OrbData.GetBaseAttackDamage(combatData.Damage, orbColor),
                    AttackIntervalSeconds = combatData.AttackIntervalSeconds *
                                            OrbData.GetAttackIntervalMultiplier(item.ItemId) *
                                            OrbData.GetBaseAttackIntervalMultiplier(orbColor) *
                                            (windActive ? OrbData.WindAttackIntervalMultiplier : 1f),
                    ProjectileWidth = combatData.ProjectileWidth,
                    EffectDurationSeconds = combatData.EffectDurationSeconds,
                    MaxTargets = 1,
                    AdditionalTargetDamageMultiplier = 1f,
                    InitialBurstAttackCount = 0,
                    InitialBurstAttackIntervalMultiplier = 1f,
                    BurstRechargeSeconds = 0f,
                    // 슬롯마다 발사를 조금씩 어긋내 6칸이 같은 틱에 터지지 않게 한다.
                    // 0.15는 6번째 오브를 0.75초나 늦춰 조우 반응이 굼떠 보였다.
                    InitialAttackDelaySeconds = attackSlotIndex++ * 0.05f,
                    OrbEffectActive = orbEffectActive,
                    WeaponItemUid = item.ItemUid,
                    WeaponStackIndex = stackIndex,
                    SunResonanceStage = sunStage,
                    WaveResonanceArmed = waveArmed
                });
                addedBoardOrb = true;
            }
        }
        if (addedBoardOrb)
            return;

        var legacyCombatData = equippedItem == null
            ? null
            : BattleItemCombatData.Get(equippedItem.ItemId);
        if (equippedItem != null && legacyCombatData != null)
        {
            actors.Add(spatialActor with
            {
                WeaponItemId = equippedItem.ItemId,
                AttackRange = legacyCombatData.AttackRange,
                Damage = legacyCombatData.Damage,
                AttackIntervalSeconds = legacyCombatData.AttackIntervalSeconds *
                                        OrbData.GetAttackIntervalMultiplier(equippedItem.ItemId),
                ProjectileWidth = legacyCombatData.ProjectileWidth,
                EffectDurationSeconds = legacyCombatData.EffectDurationSeconds,
                WeaponItemUid = equippedItem.ItemUid
            });
            return;
        }

        actors.Add(spatialActor);
    }
    private void ProcessOrbRecovery(
        long matchingId,
        IReadOnlyCollection<ProximityCombatActor> actors,
        IReadOnlyCollection<GameClientSession> matchingSessions,
        IReadOnlyCollection<BotPlayerState> matchingBots,
        DateTime nowUtc)
    {
        var activeRecoveryKeys = new HashSet<(long PlayerId, long ItemUid, int StackIndex)>();
        var dueRecoveryByPlayer = new Dictionary<long, List<(ProximityCombatActor Actor, int Amount)>>();

        foreach (var actor in actors)
        {
            int requestedRecovery = OrbData.GetRecoveryAmount(actor.WeaponItemId);
            if (requestedRecovery <= 0)
                continue;

            var actorKey = (actor.PlayerId, actor.WeaponItemUid, actor.WeaponStackIndex);
            var stateKey = (matchingId, actor.PlayerId, actor.WeaponItemUid, actor.WeaponStackIndex);
            activeRecoveryKeys.Add(actorKey);

            if (!_orbRecoveryReadyAtUtc.TryGetValue(stateKey, out var readyAtUtc))
            {
                _orbRecoveryReadyAtUtc[stateKey] =
                    nowUtc.AddSeconds(OrbData.RecoveryTickSeconds);
                continue;
            }

            if (nowUtc < readyAtUtc)
                continue;

            _orbRecoveryReadyAtUtc[stateKey] =
                nowUtc.AddSeconds(OrbData.RecoveryTickSeconds);

            if (!dueRecoveryByPlayer.TryGetValue(actor.PlayerId, out var dueRecoveries))
            {
                dueRecoveries = new List<(ProximityCombatActor Actor, int Amount)>();
                dueRecoveryByPlayer[actor.PlayerId] = dueRecoveries;
            }
            dueRecoveries.Add((actor, requestedRecovery));
        }

        // #227 6단계: 같은 서버 틱에 발동한 회복 오브는 실제 회복·숫자·효과음을 한 번으로
        // 합친다. 개별 오브의 다음 발동 시각은 위에서 그대로 유지한다.
        foreach (var (playerId, dueRecoveries) in dueRecoveryByPlayer)
        {
            int requestedRecovery = dueRecoveries.Sum(entry => entry.Amount);
            var representative = dueRecoveries
                .OrderByDescending(entry => entry.Amount)
                .ThenBy(entry => entry.Actor.WeaponItemUid)
                .First().Actor;

            int effectiveRecovery = 0;
            var session = matchingSessions.FirstOrDefault(candidate =>
                candidate.PlayerId == playerId && !candidate.IsEliminated);
            if (session != null)
            {
                int previousCorruption = session.CurrentCorruption;
                if (previousCorruption > 0)
                {
                    session.ModifyStats(corruptionDelta: -requestedRecovery);
                    effectiveRecovery = previousCorruption - session.CurrentCorruption;
                }
            }
            else
            {
                var bot = matchingBots.FirstOrDefault(candidate =>
                    candidate.PlayerId == playerId && !candidate.IsEliminated);
                if (bot != null && bot.Corruption > 0)
                {
                    int previousCorruption = bot.Corruption;
                    bot.Corruption = Math.Max(0, bot.Corruption - requestedRecovery);
                    effectiveRecovery = previousCorruption - bot.Corruption;
                }
            }

            if (effectiveRecovery <= 0)
                continue;

            session?.SendOrbRecoveryFeedback(representative.WeaponItemId, effectiveRecovery);

            // Human sessions already record effective recovery inside ModifyStats.
            // Bots mutate their state directly, so only that path needs explicit telemetry.
            if (session == null)
            {
                _gameEventLogManager.RecordRecovery(
                    matchingId, playerId, effectiveRecovery);
            }
            logger.LogDebug(
                "Survivor recovery event tick: MatchingId={MatchingId}, PlayerId={PlayerId}, " +
                "OrbCount={OrbCount}, ItemId={ItemId}, Recovery={Recovery}",
                matchingId,
                playerId,
                dueRecoveries.Count,
                representative.WeaponItemId,
                effectiveRecovery);
        }

        foreach (var key in _orbRecoveryReadyAtUtc.Keys
                     .Where(key => key.MatchingId == matchingId)
                     .ToArray())
        {
            if (activeRecoveryKeys.Contains((key.PlayerId, key.ItemUid, key.StackIndex)))
                continue;

            _orbRecoveryReadyAtUtc.TryRemove(key, out _);
        }
    }

    /// <summary>
    ///     현재 observer→actor 순서의 cache remove 또는 publication 후보를 불변 값으로 고정한다.
    ///     cache commit은 아직 하지 않는다. 각 Send 직전 commit이라는 기존 failure boundary는
    ///     <see cref="AppendOrbVisualStatePublicationSteps"/>의 deferred step이 보존한다.
    /// </summary>
    private ImmutableArray<SwarmOrbVisualPublication> PrepareOrbVisualStatePublications(
        long matchingId,
        IReadOnlyCollection<ProximityCombatActor> actors,
        IReadOnlyCollection<GameClientSession> matchingSessions)
    {
        GameClientSession[] recipientSnapshot = matchingSessions.ToArray();
        var publications = ImmutableArray.CreateBuilder<SwarmOrbVisualPublication>();
        var visualActors = actors
            .GroupBy(actor => actor.PlayerId)
            .Select(group =>
            {
                var orbActors = group
                    .Where(actor =>
                        OrbData.IsOrbItem(actor.WeaponItemId) ||
                        OrbData.IsRecoveryOrb(actor.WeaponItemId))
                    .OrderBy(actor => actor.WeaponItemUid)
                    .ThenBy(actor => actor.WeaponStackIndex)
                    .ToList();
                var primaryActor = orbActors.FirstOrDefault(actor =>
                    actor.OrbEffectActive &&
                    OrbData.TryGetColorAndTier(actor.WeaponItemId, out var color, out _) &&
                    color == OrbColor.Green);
                if (primaryActor.PlayerId == 0)
                    primaryActor = orbActors.FirstOrDefault(actor => actor.OrbEffectActive);
                if (primaryActor.PlayerId == 0)
                    primaryActor = orbActors.Count > 0 ? orbActors[0] : group.First();

                var orbItemIds = orbActors.Select(actor => actor.WeaponItemId).ToList();
                return new
                {
                    Actor = primaryActor,
                    OrbItemIds = orbItemIds,
                    OrbItemSignature = string.Join(",", orbItemIds)
                };
            })
            .ToList();

        foreach (var observer in recipientSnapshot)
        {
            // 탈락 관전자도 받는다 (#219): 오브 궤도·앞줄 HP가 관전 화면에서도 계속 갱신돼야 한다.
            if (!observer.PlayerId.HasValue)
                continue;

            foreach (var visualActor in visualActors)
            {
                var actor = visualActor.Actor;
                var key = (matchingId, observer.PlayerId.Value, actor.PlayerId);
                if (observer.CurrentArea != actor.Area)
                {
                    publications.Add(SwarmOrbVisualPublication.Remove(
                        matchingId, observer.PlayerId.Value, actor.PlayerId));
                    continue;
                }

                // 앞줄 오브 HP·잼·본체 오염을 시그니처에 포함 — 값 변화가 곧 상태 변화라 갱신이 전송된다.
                int frontOrbHp = GetSwarmFrontOrbHp(matchingId, actor.PlayerId);
                int jamCount = GetSwarmJamCount(matchingId, actor.PlayerId, matchingSessions);
                int bodyCorruption = GetSwarmBodyCorruption(matchingId, actor.PlayerId, matchingSessions);
                long armorMask = GetSwarmArmorMask(matchingId, actor.PlayerId);
                var state = new OrbVisualState(
                    actor.Area,
                    actor.WeaponItemId,
                    actor.OrbEffectActive,
                    visualActor.OrbItemSignature,
                    frontOrbHp,
                    jamCount,
                    bodyCorruption,
                    armorMask);
                if (_orbVisualStates.TryGetValue(key, out var previousState) &&
                    previousState == state)
                {
                    continue;
                }

                publications.Add(SwarmOrbVisualPublication.Publish(
                    matchingId,
                    observer.PlayerId.Value,
                    observer,
                    actor.PlayerId,
                    state,
                    actor.WeaponItemId,
                    actor.OrbEffectActive,
                    CaptureSwarmOrbVisualItemIds(visualActor.OrbItemIds),
                    frontOrbHp,
                    jamCount,
                    bodyCorruption,
                    armorMask));
            }
        }

        return publications.ToImmutable();
    }

    /// <summary>
    ///     match monitor 안에서는 불변 step만 capture하고, dispatch 때 각 cache remove/update와
    ///     바로 뒤 Send를 하나의 deferred step으로 실행한다. Send 실패 시 현재 key는 no-retry로
    ///     남지만 아직 방문하지 않은 step은 cache가 바뀌지 않아 다음 tick에 다시 후보가 된다.
    /// </summary>
    private void AppendOrbVisualStatePublicationSteps(
        ImmutableArray<SwarmOrbVisualPublication> publications)
    {
        foreach (SwarmOrbVisualPublication publication in publications)
        {
            // One deferred step owns both cache commit and its immediately following send. A failed
            // key remains committed (no retry), while later unvisited keys remain candidates.
            _swarmCombatPublicationCoordinator.AppendDeferredStep(
                () => CommitAndDispatchOrbVisualStatePublication(publication));
        }
    }

    private void CommitAndDispatchOrbVisualStatePublication(
        SwarmOrbVisualPublication publication)
    {
        var key = (publication.MatchingId, publication.ObserverPlayerId, publication.ActorPlayerId);
        if (publication.State is { } state)
            _orbVisualStates[key] = state;
        else
            _orbVisualStates.TryRemove(key, out _);

        if (publication.State is null)
            return;

        using var packet = Packet.Create((int)Protocol.G_TO_C_ORB_EFFECT_STATE);
        packet.SetBody(MessagePackSerializer.Serialize(new G_TO_C_ORB_EFFECT_STATE
        {
            PlayerId = publication.ActorPlayerId,
            WeaponItemId = publication.WeaponItemId,
            IsActive = publication.IsActive,
            OrbItemIds = publication.OrbItemIds.ToList(),
            FrontOrbHp = publication.FrontOrbHp,
            JamCount = publication.JamCount,
            BodyCorruption = publication.BodyCorruption,
            ArmorMask = publication.ArmorMask
        }));
        publication.Recipient!.Send(packet);
    }

    private static ImmutableArray<int> CaptureSwarmOrbVisualItemIds(IEnumerable<int> orbItemIds) =>
        [.. orbItemIds];

    private void RemoveOrbVisualStates(long matchingId)
    {
        foreach (var key in _orbVisualStates.Keys
                     .Where(key => key.MatchingId == matchingId)
                     .ToArray())
        {
            _orbVisualStates.TryRemove(key, out _);
        }
        foreach (var key in _orbRecoveryReadyAtUtc.Keys
                     .Where(key => key.MatchingId == matchingId)
                     .ToArray())
        {
            _orbRecoveryReadyAtUtc.TryRemove(key, out _);
        }
    }

    private static bool TryCreateSpatialActor(
long playerId,
MapId mapId,
AreaType committedArea,
Vector3f? position,
out ProximityCombatActor actor)
    {
        actor = default;
        if (mapId == MapId.None || committedArea == AreaType.None || position == null)
            return false;

        var cell = ProximityCombatLineOfSight.WorldPositionToCell(mapId, position);
        var resolvedArea = GameMapData.GetCurrentArea(mapId, cell);
        if (resolvedArea == AreaType.None || resolvedArea != committedArea ||
            !GameMapData.IsMoveablePosition(mapId, cell))
        {
            return false;
        }

        actor = new ProximityCombatActor(
            playerId,
            resolvedArea,
            position,
            0,
            0f,
            0,
            0f,
            0f,
            0f,
            mapId,
            cell);
        return true;
    }

    private readonly record struct OrbVisualState(
        AreaType Area,
        int WeaponItemId,
        bool IsActive,
        string OrbItemSignature,
        int FrontOrbHp,
        int JamCount,
        int BodyCorruption,
        long ArmorMask);

    private sealed record SwarmOrbVisualPublication(
        long MatchingId,
        long ObserverPlayerId,
        long ActorPlayerId,
        OrbVisualState? State,
        GameClientSession? Recipient,
        int WeaponItemId,
        bool IsActive,
        ImmutableArray<int> OrbItemIds,
        int FrontOrbHp,
        int JamCount,
        int BodyCorruption,
        long ArmorMask)
    {
        public static SwarmOrbVisualPublication Remove(
            long matchingId, long observerPlayerId, long actorPlayerId) =>
            new(
                matchingId, observerPlayerId, actorPlayerId, null, null,
                0, false, ImmutableArray<int>.Empty, 0, 0, 0, 0);

        public static SwarmOrbVisualPublication Publish(
            long matchingId,
            long observerPlayerId,
            GameClientSession recipient,
            long actorPlayerId,
            OrbVisualState state,
            int weaponItemId,
            bool isActive,
            ImmutableArray<int> orbItemIds,
            int frontOrbHp,
            int jamCount,
            int bodyCorruption,
            long armorMask) =>
            new(
                matchingId, observerPlayerId, actorPlayerId, state, recipient,
                weaponItemId, isActive, orbItemIds, frontOrbHp, jamCount, bodyCorruption, armorMask);
    }

}
