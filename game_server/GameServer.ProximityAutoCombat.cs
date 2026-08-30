using System.Collections.Concurrent;
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
    private readonly ConcurrentDictionary<(long MatchingId, long PlayerId), ProximityCombatAreaEntryState>
        _proximityCombatAreaEntryStates = new();
    private Timer? _proximityAutoCombatTimer;
    private int _proximityAutoCombatProcessing;

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
        if (Interlocked.Exchange(ref _proximityAutoCombatProcessing, 1) != 0)
            return;

        try
        {
            var activeSessions = _clientSessions.Values
                .Where(session =>
                    session.PlayerId.HasValue &&
                    !session.IsEliminated &&
                    !session.IsGameEnded)
                .ToList();

            foreach (long matchingId in GetActiveMatchingIds())
            {
                // 인트로 예열 (2026-08-16 유저 결정): 카운트다운 동안에도 스웜은 돈다 —
                // 운동장에서 각 방으로 나가는 몹이 그 5초의 볼거리이기 때문이다.
                // IsRoundActionPhase는 매치 시작 게이트를 포함하므로 여기서 막히면 몹이
                // 아예 태어나지 않는다. 스웜 경로만 예외로 열고, 전투는 그 안에서 막는다.
                bool swarmWarmup = !MatchStartGate.IsGameplayActive(matchingId);
                if (!swarmWarmup && !GameClientSession.IsRoundActionPhase(matchingId))
                    continue;

                _matchRuntimeRegistry.TryExecute(matchingId, () =>
                {
                    ProcessProximityAutoCombatForMatching(matchingId, activeSessions);
                });
            }
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "Proximity auto combat tick failed");
        }
        finally
        {
            Volatile.Write(ref _proximityAutoCombatProcessing, 0);
        }
    }

    private void ProcessProximityAutoCombatForMatching(
        long matchingId,
        List<GameClientSession> activeSessions)
    {
        ProcessSwarmArenaForMatching(matchingId, activeSessions);
    }
    private static void AddInventoryCombatActors(
ICollection<ProximityCombatActor> actors,
ProximityCombatActor spatialActor,
PlayerInGameInventory inventory,
OrbResonanceSnapshot resonanceState)
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
            bool orbEffectActive = resonanceState.ActiveColor == orbColor;

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

    private void BroadcastOrbVisualStates(
        long matchingId,
        IReadOnlyCollection<ProximityCombatActor> actors,
        IReadOnlyCollection<GameClientSession> matchingSessions)
    {
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

        foreach (var observer in matchingSessions)
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
                    _orbVisualStates.TryRemove(key, out _);
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

                _orbVisualStates[key] = state;
                using var packet = Packet.Create((int)Protocol.G_TO_C_ORB_EFFECT_STATE);
                packet.SetBody(MessagePackSerializer.Serialize(new G_TO_C_ORB_EFFECT_STATE
                {
                    PlayerId = actor.PlayerId,
                    WeaponItemId = actor.WeaponItemId,
                    IsActive = actor.OrbEffectActive,
                    OrbItemIds = visualActor.OrbItemIds,
                    FrontOrbHp = frontOrbHp,
                    JamCount = jamCount,
                    BodyCorruption = bodyCorruption,
                    ArmorMask = armorMask
                }));
                observer.Send(packet);
            }
        }
    }

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
        foreach (var key in _proximityCombatAreaEntryStates.Keys
                     .Where(key => key.MatchingId == matchingId)
                     .ToArray())
        {
            _proximityCombatAreaEntryStates.TryRemove(key, out _);
        }

        RemoveOrbResonanceStates(matchingId);
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

    private readonly record struct ProximityCombatAreaEntryState(
        AreaType Area,
        DateTime ReadyAtUtc,
        AreaType PreviousArea,
        DateTime PreviousAreaLeftAtUtc);

    private readonly record struct OrbVisualState(
        AreaType Area,
        int WeaponItemId,
        bool IsActive,
        string OrbItemSignature,
        int FrontOrbHp,
        int JamCount,
        int BodyCorruption,
        long ArmorMask);

}
