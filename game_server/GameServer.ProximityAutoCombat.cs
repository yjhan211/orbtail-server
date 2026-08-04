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
    private static readonly TimeSpan ProximityCombatAreaEntryGrace = TimeSpan.FromSeconds(1);

    /// <summary>
    ///     이 시간 안에 직전 구역으로 되돌아오면 진입 유예를 다시 주지 않는다.
    ///     문턱 왕복으로 무적이 되는 것을 막는다.
    /// </summary>
    private static readonly TimeSpan ProximityCombatAreaReentryWindow = TimeSpan.FromSeconds(5);

    private readonly ProximityAutoCombatResolver _proximityAutoCombatResolver = new();
    private readonly DodgeableProjectileResolver _dodgeableProjectileResolver = new();
    private readonly Dictionary<(long MatchingId, long ObserverPlayerId, long ActorPlayerId),
        SurvivorOrbVisualState> _survivorOrbVisualStates = new();
    private readonly Dictionary<(long MatchingId, long PlayerId, long ItemUid, int StackIndex), DateTime>
        _survivorOrbRecoveryReadyAtUtc = new();
    private readonly Dictionary<(long MatchingId, long PlayerId), ProximityCombatAreaEntryState>
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
                if (!GameClientSession.IsRoundActionPhase(matchingId))
                    continue;

                lock (GetSurvivorSettlementLock(matchingId))
                {
                    ProcessProximityAutoCombatForMatching(matchingId, activeSessions);
                    if (!HasProximityAutoCombatMatchingEnded(matchingId)) continue;
                    _proximityAutoCombatResolver.RemoveMatching(matchingId);
                    RemoveSurvivorOrbVisualStates(matchingId);
                    _survivorSettlementLocks.TryRemove(matchingId, out _);
                }
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

    /// <summary>
    ///     자동전투 상태를 폐기해도 되는 시점인지 판정한다.
    ///     세션이 하나도 없는 매치에 <c>All</c>을 쓰면 빈 시퀀스가 참이 되어 매 틱 종료로 오인한다.
    ///     그러면 공격 쿨다운과 구역 진입 유예가 50ms마다 초기화되어 아무도 공격하지 못한다.
    /// </summary>
    private bool HasProximityAutoCombatMatchingEnded(long matchingId)
    {
        var matchingSessions = _clientSessions.Values
            .Where(session => session.PlayerId.HasValue && session.CurrentMapSubId == matchingId)
            .ToList();
        if (matchingSessions.Count > 0)
            return matchingSessions.All(session => session.IsGameEnded);

        return !_botPlayerManager.GetBots(matchingId).Any(bot => !bot.IsEliminated);
    }

    private void ProcessProximityAutoCombatForMatching(
        long matchingId,
        List<GameClientSession> activeSessions)
    {
        var matchingSessions = activeSessions
            .Where(session =>
                session.CurrentMapSubId == matchingId &&
                !session.IsEliminated &&
                !session.IsGameEnded)
            .ToList();
        var matchingBots = _botPlayerManager.GetBots(matchingId)
            .Where(bot => !bot.IsEliminated)
            .ToList();

        var nowUtc = DateTime.UtcNow;
        var resonanceStates = UpdateSurvivorOrbResonanceStates(matchingId, matchingSessions, matchingBots, nowUtc);
        var actors = BuildProximityCombatActors(
            matchingId,
            matchingSessions,
            matchingBots,
            resonanceStates);
        ProcessSurvivorOrbRecovery(matchingId, actors, matchingSessions, matchingBots, nowUtc);
        BroadcastSurvivorOrbVisualStates(matchingId, actors, matchingSessions);
        var combatReadyPlayerIds = matchingSessions
            .Where(session =>
                session.PlayerId.HasValue &&
                HasCompletedProximityCombatAreaEntryGrace(
                    matchingId,
                    session.PlayerId.Value,
                    session.CurrentArea,
                    nowUtc))
            .Select(session => session.PlayerId!.Value)
            .Concat(matchingBots
                .Where(bot => HasCompletedProximityCombatAreaEntryGrace(
                    matchingId,
                    bot.PlayerId,
                    bot.CurrentArea,
                    nowUtc))
                .Select(bot => bot.PlayerId))
            .ToHashSet();
        var combatActors = actors
            .Where(actor => combatReadyPlayerIds.Contains(actor.PlayerId))
            .ToList();
        var activeOrbColors = new Dictionary<long, SurvivorOrbColor>();
        foreach (var actor in actors.Where(actor => actor.OrbEffectActive))
        {
            if (SurvivorOrbData.TryGetColorAndTier(actor.WeaponItemId, out var color, out _))
                activeOrbColors[actor.PlayerId] = color;
        }

        var projectileResolutions = _dodgeableProjectileResolver.ResolveImpacts(
            matchingId,
            combatActors,
            nowUtc,
            actors);
        var projectileHits = projectileResolutions
            .SelectMany(resolution => resolution.Hits)
            .ToList();
        if (projectileResolutions.Count > 0)
            _gameEventLogManager.LogDodgeableProjectileResolutions(matchingId, projectileResolutions);
        if (projectileHits.Count > 0)
        {
            ApplyProximityCombatVolley(
                matchingId, projectileHits, matchingSessions, matchingBots, activeSessions,
                activeOrbColors, resonanceStates, actors, nowUtc, broadcastAttackVfx: false);
        }

        var aliveMonsterTargets = AdvanceEmotionAfterimageMonsters(
            matchingId, matchingSessions, matchingBots, nowUtc);
        var monsterTargetIds = aliveMonsterTargets
            .Select(target => -(long)target.MonsterId)
            .ToHashSet();
        var combatTargets = combatActors
            .Concat(aliveMonsterTargets.Select(CreateMonsterTargetActor))
            .ToList();
        var attacks = _proximityAutoCombatResolver.Resolve(
            matchingId,
            combatTargets,
            nowUtc,
            ProximityCombatLineOfSight.CanTarget,
            onTargetAcquired: targetEvent =>
                _gameEventLogManager.LogSurvivorTargetAcquired(
                    matchingId,
                    targetEvent.AttackerPlayerId,
                    targetEvent.TargetPlayerId,
                    targetEvent.Area.ToString(),
                    targetEvent.WeaponItemId,
                    targetEvent.TargetWeaponItemId,
                    BotPlayerManager.IsBotPlayerId(targetEvent.AttackerPlayerId),
                    targetEvent.OccurredAtUtc),
            onTargetLost: targetEvent =>
                _gameEventLogManager.LogSurvivorTargetLost(
                    matchingId,
                    targetEvent.AttackerPlayerId,
                    targetEvent.TargetPlayerId,
                    targetEvent.Reason,
                    BotPlayerManager.IsBotPlayerId(targetEvent.AttackerPlayerId),
                    targetEvent.OccurredAtUtc));

        var playerCombatActors = combatTargets.Where(actor => !actor.IsMonsterTarget).ToList();
        var occupiedAreas = playerCombatActors
            .Select(actor => actor.Area)
            .Where(area => area != AreaType.None)
            .ToHashSet();
        var monsterCombatActors = combatTargets.Where(actor => actor.IsMonsterTarget).ToList();
        var attackableMonsterAreas = playerCombatActors
            .Where(attacker => attacker.AttackRange > 0f && attacker.Damage > 0)
            .Where(attacker => monsterCombatActors.Any(monster =>
            {
                if (monster.Area != attacker.Area)
                    return false;
                float dx = attacker.Position.X - monster.Position.X;
                float dy = attacker.Position.Y - monster.Position.Y;
                return dx * dx + dy * dy <= attacker.AttackRange * attacker.AttackRange &&
                       ProximityCombatLineOfSight.CanTarget(attacker, monster);
            }))
            .Select(attacker => attacker.Area)
            .ToHashSet();
        foreach (var sample in _emotionAfterimageMonsterManager.SampleDensity(
                     matchingId, occupiedAreas, attackableMonsterAreas, nowUtc))
            _gameEventLogManager.LogMonsterDensitySample(matchingId, sample);

        if (attacks.Count == 0)
            return;

        var playerAttacks = attacks
            .Where(attack => !monsterTargetIds.Contains(attack.TargetPlayerId))
            .ToList();
        if (playerAttacks.Count > 0)
        {
            var windPulseAttacks = playerAttacks
                .Where(attack => !attack.IsResonanceProc &&
                                 SurvivorOrbData.GetAttackPattern(attack.WeaponItemId) ==
                                 SurvivorOrbAttackPattern.AttackerArea)
                .ToList();
            var dodgeableAttacks = playerAttacks
                .Where(attack => !attack.IsResonanceProc &&
                                 SurvivorOrbData.GetAttackPattern(attack.WeaponItemId) is
                                     SurvivorOrbAttackPattern.HomingProjectile or
                                     SurvivorOrbAttackPattern.TargetArea)
                .ToList();
            var immediateAttacks = playerAttacks
                .Where(attack => attack.IsResonanceProc ||
                                 SurvivorOrbData.GetAttackPattern(attack.WeaponItemId) ==
                                 SurvivorOrbAttackPattern.None)
                .ToList();
            if (immediateAttacks.Count > 0)
            {
                ApplyProximityCombatVolley(
                    matchingId, immediateAttacks, matchingSessions, matchingBots, activeSessions,
                    activeOrbColors, resonanceStates, actors, nowUtc, broadcastAttackVfx: true);
            }

            var expandedWindPulseAttacks = ExpandWindAreaAttacks(windPulseAttacks, combatActors);
            foreach (var windPulse in windPulseAttacks)
                BroadcastDodgeablePvpAttackLaunch(windPulse, matchingSessions);
            if (expandedWindPulseAttacks.Count > 0)
            {
                ApplyProximityCombatVolley(
                    matchingId, expandedWindPulseAttacks, matchingSessions, matchingBots, activeSessions,
                    activeOrbColors, resonanceStates, actors, nowUtc, broadcastAttackVfx: false);
            }

            var launches = _dodgeableProjectileResolver.Queue(
                matchingId, dodgeableAttacks, combatActors, nowUtc);
            if (launches.Count > 0)
                _gameEventLogManager.LogDodgeableProjectileLaunches(matchingId, launches);
            foreach (var launch in launches)
                BroadcastDodgeablePvpAttackLaunch(launch.Attack, matchingSessions);
        }

        var monsterAttacks = ExpandWindAreaAttacks(
            ExpandWaveAreaAttacks(
                attacks.Where(attack => monsterTargetIds.Contains(attack.TargetPlayerId)).ToList(),
                combatTargets),
            combatTargets);
        ApplyPlayerOrbDamageToEmotionAfterimageMonsters(
            matchingId, monsterAttacks, aliveMonsterTargets, nowUtc, matchingSessions);
    }
    private List<ProximityCombatActor> BuildProximityCombatActors(
        long matchingId,
        IReadOnlyCollection<GameClientSession> matchingSessions,
        IReadOnlyCollection<BotPlayerState> matchingBots,
        IReadOnlyDictionary<long, SurvivorOrbResonanceSnapshot> resonanceStates)
    {
        var actors = new List<ProximityCombatActor>((matchingSessions.Count + matchingBots.Count) * 6);

        foreach (var session in matchingSessions)
        {
            if (!session.PlayerId.HasValue ||
                !TryCreateSpatialActor(
                    session.PlayerId.Value,
                    session.CurrentMapId,
                    session.CurrentArea,
                    session.LastValidatedPosition,
                    out var actor))
            {
                continue;
            }

            var inventory = _inGameInventoryManager.GetPlayerInventory(matchingId, session.PlayerId.Value);
            resonanceStates.TryGetValue(session.PlayerId.Value, out var resonanceState);
            AddInventoryCombatActors(actors, actor, inventory, resonanceState);
        }

        var botMapId = _botPlayerManager.GetMatchingMapId(matchingId);
        foreach (var bot in matchingBots)
        {
            if (!TryCreateSpatialActor(
                    bot.PlayerId,
                    botMapId,
                    bot.CurrentArea,
                    bot.Position,
                    out var actor))
            {
                continue;
            }

            var inventory = _inGameInventoryManager.GetPlayerInventory(matchingId, bot.PlayerId);
            resonanceStates.TryGetValue(bot.PlayerId, out var resonanceState);
            AddInventoryCombatActors(actors, actor, inventory, resonanceState);
        }

        return actors;
    }

    private bool HasCompletedProximityCombatAreaEntryGrace(
        long matchingId,
        long playerId,
        AreaType area,
        DateTime nowUtc)
    {
        if (area == AreaType.None)
            return false;

        var key = (matchingId, playerId);
        if (!_proximityCombatAreaEntryStates.TryGetValue(key, out var state))
        {
            _proximityCombatAreaEntryStates[key] = new ProximityCombatAreaEntryState(
                area,
                nowUtc.Add(ProximityCombatAreaEntryGrace),
                AreaType.None,
                DateTime.MinValue);
            return false;
        }

        if (state.Area == area)
            return nowUtc >= state.ReadyAtUtc;

        // 문턱을 왕복하면 유예가 매번 갱신되어 그 대상은 아무도 때릴 수 없게 된다.
        // 방금 떠난 구역으로 되돌아오는 것은 "처음 보는 상대"가 아니므로 유예를 주지 않는다.
        // 비가시 공격 차단은 새로운 구역으로 진입할 때만 필요하다.
        bool isReturningToRecentArea = area == state.PreviousArea &&
                                       nowUtc - state.PreviousAreaLeftAtUtc <= ProximityCombatAreaReentryWindow;
        _proximityCombatAreaEntryStates[key] = new ProximityCombatAreaEntryState(
            area,
            isReturningToRecentArea ? nowUtc : nowUtc.Add(ProximityCombatAreaEntryGrace),
            state.Area,
            nowUtc);
        return isReturningToRecentArea;
    }

    private static void AddInventoryCombatActors(
        ICollection<ProximityCombatActor> actors,
        ProximityCombatActor spatialActor,
        PlayerInGameInventory inventory,
        SurvivorOrbResonanceSnapshot resonanceState)
    {
        var equippedItem = inventory.GetEquippedBattleItem();
        bool addedBoardOrb = false;

        int attackSlotIndex = 0;
        foreach (var item in inventory.GetAllItems()
                     .Where(item => item.Count > 0)
                     .OrderBy(item => item.ItemUid))
        {
            if (SurvivorOrbData.IsRecoveryOrb(item.ItemId))
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

            if (!SurvivorOrbData.TryGetColorAndTier(item.ItemId, out var orbColor, out _))
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
                    AttackRange = SurvivorOrbData.GetAttackPattern(item.ItemId) ==
                                  SurvivorOrbAttackPattern.AttackerArea
                        ? SurvivorOrbData.GetWindPulseRadius(item.ItemId)
                        : combatData.AttackRange *
                          (windActive ? SurvivorOrbData.WindAttackRangeMultiplier : 1f),
                    Damage = SurvivorOrbData.GetBaseAttackDamage(combatData.Damage, orbColor),
                    AttackIntervalSeconds = combatData.AttackIntervalSeconds *
                                            SurvivorOrbData.GetAttackIntervalMultiplier(item.ItemId) *
                                            SurvivorOrbData.GetBaseAttackIntervalMultiplier(orbColor) *
                                            (windActive ? SurvivorOrbData.WindAttackIntervalMultiplier : 1f),
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
                                        SurvivorOrbData.GetAttackIntervalMultiplier(equippedItem.ItemId),
                ProjectileWidth = legacyCombatData.ProjectileWidth,
                EffectDurationSeconds = legacyCombatData.EffectDurationSeconds,
                WeaponItemUid = equippedItem.ItemUid
            });
            return;
        }

        actors.Add(spatialActor);
    }
    private void ProcessSurvivorOrbRecovery(
        long matchingId,
        IReadOnlyCollection<ProximityCombatActor> actors,
        IReadOnlyCollection<GameClientSession> matchingSessions,
        IReadOnlyCollection<BotPlayerState> matchingBots,
        DateTime nowUtc)
    {
        var activeRecoveryKeys = new HashSet<(long PlayerId, long ItemUid, int StackIndex)>();

        foreach (var actor in actors)
        {
            int requestedRecovery = SurvivorOrbData.GetRecoveryAmount(actor.WeaponItemId);
            if (requestedRecovery <= 0)
                continue;

            var actorKey = (actor.PlayerId, actor.WeaponItemUid, actor.WeaponStackIndex);
            var stateKey = (matchingId, actor.PlayerId, actor.WeaponItemUid, actor.WeaponStackIndex);
            activeRecoveryKeys.Add(actorKey);

            if (!_survivorOrbRecoveryReadyAtUtc.TryGetValue(stateKey, out var readyAtUtc))
            {
                _survivorOrbRecoveryReadyAtUtc[stateKey] =
                    nowUtc.AddSeconds(SurvivorOrbData.RecoveryTickSeconds);
                continue;
            }

            if (nowUtc < readyAtUtc)
                continue;

            _survivorOrbRecoveryReadyAtUtc[stateKey] =
                nowUtc.AddSeconds(SurvivorOrbData.RecoveryTickSeconds);

            int effectiveRecovery = 0;
            var session = matchingSessions.FirstOrDefault(candidate =>
                candidate.PlayerId == actor.PlayerId && !candidate.IsEliminated);
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
                    candidate.PlayerId == actor.PlayerId && !candidate.IsEliminated);
                if (bot != null && bot.Corruption > 0)
                {
                    int previousCorruption = bot.Corruption;
                    bot.Corruption = Math.Max(0, bot.Corruption - requestedRecovery);
                    effectiveRecovery = previousCorruption - bot.Corruption;
                }
            }

            if (effectiveRecovery <= 0)
                continue;

            session?.SendSurvivorOrbRecoveryFeedback(actor.WeaponItemId, effectiveRecovery);

            // Human sessions already record effective recovery inside ModifyStats.
            // Bots mutate their state directly, so only that path needs explicit telemetry.
            if (session == null)
            {
                _gameEventLogManager.RecordSurvivorRecovery(
                    matchingId, actor.PlayerId, effectiveRecovery);
            }
            logger.LogDebug(
                "Survivor recovery orb tick: MatchingId={MatchingId}, PlayerId={PlayerId}, ItemId={ItemId}, ItemUid={ItemUid}, StackIndex={StackIndex}, Recovery={Recovery}",
                matchingId,
                actor.PlayerId,
                actor.WeaponItemId,
                actor.WeaponItemUid,
                actor.WeaponStackIndex,
                effectiveRecovery);
        }

        foreach (var key in _survivorOrbRecoveryReadyAtUtc.Keys
                     .Where(key => key.MatchingId == matchingId)
                     .ToArray())
        {
            if (activeRecoveryKeys.Contains((key.PlayerId, key.ItemUid, key.StackIndex)))
                continue;

            _survivorOrbRecoveryReadyAtUtc.Remove(key);
        }
    }

    private void BroadcastSurvivorOrbVisualStates(
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
                        SurvivorOrbData.IsSurvivorOrb(actor.WeaponItemId) ||
                        SurvivorOrbData.IsRecoveryOrb(actor.WeaponItemId))
                    .OrderBy(actor => actor.WeaponItemUid)
                    .ThenBy(actor => actor.WeaponStackIndex)
                    .ToList();
                var primaryActor = orbActors.FirstOrDefault(actor =>
                    actor.OrbEffectActive &&
                    SurvivorOrbData.TryGetColorAndTier(actor.WeaponItemId, out var color, out _) &&
                    color == SurvivorOrbColor.Green);
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
            if (!observer.PlayerId.HasValue || observer.IsEliminated)
                continue;

            foreach (var visualActor in visualActors)
            {
                var actor = visualActor.Actor;
                var key = (matchingId, observer.PlayerId.Value, actor.PlayerId);
                if (observer.CurrentArea != actor.Area)
                {
                    _survivorOrbVisualStates.Remove(key);
                    continue;
                }

                var state = new SurvivorOrbVisualState(
                    actor.Area,
                    actor.WeaponItemId,
                    actor.OrbEffectActive,
                    visualActor.OrbItemSignature);
                if (_survivorOrbVisualStates.TryGetValue(key, out var previousState) &&
                    previousState == state)
                {
                    continue;
                }

                _survivorOrbVisualStates[key] = state;
                using var packet = Packet.Create((int)Protocol.G_TO_C_SURVIVOR_ORB_EFFECT_STATE);
                packet.SetBody(MessagePackSerializer.Serialize(new G_TO_C_SURVIVOR_ORB_EFFECT_STATE
                {
                    PlayerId = actor.PlayerId,
                    WeaponItemId = actor.WeaponItemId,
                    IsActive = actor.OrbEffectActive,
                    OrbItemIds = visualActor.OrbItemIds
                }));
                observer.Send(packet);
            }
        }
    }

    private void RemoveSurvivorOrbVisualStates(long matchingId)
    {
        foreach (var key in _survivorOrbVisualStates.Keys
                     .Where(key => key.MatchingId == matchingId)
                     .ToArray())
        {
            _survivorOrbVisualStates.Remove(key);
        }
        foreach (var key in _survivorOrbRecoveryReadyAtUtc.Keys
                     .Where(key => key.MatchingId == matchingId)
                     .ToArray())
        {
            _survivorOrbRecoveryReadyAtUtc.Remove(key);
        }
        foreach (var key in _proximityCombatAreaEntryStates.Keys
                     .Where(key => key.MatchingId == matchingId)
                     .ToArray())
        {
            _proximityCombatAreaEntryStates.Remove(key);
        }

        RemoveSurvivorOrbResonanceStates(matchingId);
        _dodgeableProjectileResolver.RemoveMatching(matchingId);
    }

    private static IReadOnlyList<ProximityCombatAttack> ExpandWaveAreaAttacks(
        IReadOnlyCollection<ProximityCombatAttack> attacks,
        IReadOnlyCollection<ProximityCombatActor> combatTargets)
    {
        if (attacks.Count == 0)
            return [];

        var expanded = new List<ProximityCombatAttack>(attacks.Count);
        foreach (var attack in attacks)
        {
            bool isWaveAreaAttack = EmotionAfterimagePveCombatRules.ShouldApplyWaveAreaAttack(
                attack.WeaponItemId,
                attack.IsResonanceProc);
            var primaryAttack = attack with { IsWaveAreaAttack = isWaveAreaAttack };
            expanded.Add(primaryAttack);
            if (!isWaveAreaAttack)
                continue;

            foreach (long secondaryTargetId in EmotionAfterimagePveCombatRules.FindWaveAreaSecondaryTargetIds(
                         combatTargets,
                         attack.AttackerPlayerId,
                         attack.TargetPlayerId,
                         attack.Area,
                         attack.WeaponItemId))
            {
                expanded.Add(primaryAttack with
                {
                    TargetPlayerId = secondaryTargetId,
                    IsWaveAreaSecondary = true
                });
            }
        }

        return expanded;
    }
    private static IReadOnlyList<ProximityCombatAttack> ExpandWindAreaAttacks(
        IReadOnlyCollection<ProximityCombatAttack> attacks,
        IReadOnlyCollection<ProximityCombatActor> combatTargets)
    {
        if (attacks.Count == 0)
            return [];

        var uniqueActors = combatTargets
            .GroupBy(actor => actor.PlayerId)
            .ToDictionary(group => group.Key, group => group.First());
        var expanded = new List<ProximityCombatAttack>(attacks.Count);
        foreach (var attack in attacks)
        {
            if (attack.IsResonanceProc ||
                SurvivorOrbData.GetAttackPattern(attack.WeaponItemId) !=
                SurvivorOrbAttackPattern.AttackerArea ||
                !uniqueActors.TryGetValue(attack.AttackerPlayerId, out var attacker) ||
                !uniqueActors.TryGetValue(attack.TargetPlayerId, out var primaryTarget))
            {
                expanded.Add(attack);
                continue;
            }

            float radius = SurvivorOrbData.GetWindPulseRadius(attack.WeaponItemId);
            float radiusSquared = radius * radius;
            foreach (var target in uniqueActors.Values
                         .Where(target => target.PlayerId != attack.AttackerPlayerId &&
                                          target.Area == attack.Area &&
                                          target.MapId == attacker.MapId &&
                                          target.IsMonsterTarget == primaryTarget.IsMonsterTarget)
                         .Where(target =>
                         {
                             float dx = target.Position.X - attacker.Position.X;
                             float dy = target.Position.Y - attacker.Position.Y;
                             return dx * dx + dy * dy <= radiusSquared &&
                                    ProximityCombatLineOfSight.CanTarget(attacker, target);
                         })
                         .OrderBy(target => target.PlayerId))
            {
                expanded.Add(attack with
                {
                    TargetPlayerId = target.PlayerId,
                    IsWindAreaAttack = true,
                    IsWindAreaSecondary = target.PlayerId != attack.TargetPlayerId
                });
            }
        }

        return expanded;
    }
    private void ApplyProximityCombatVolley(
        long matchingId,
        IReadOnlyCollection<ProximityCombatAttack> attacks,
        IReadOnlyCollection<GameClientSession> matchingSessions,
        IReadOnlyCollection<BotPlayerState> matchingBots,
        List<GameClientSession> activeSessions,
        IReadOnlyDictionary<long, SurvivorOrbColor> activeOrbColors,
        IReadOnlyDictionary<long, SurvivorOrbResonanceSnapshot> resonanceStates,
        IReadOnlyCollection<ProximityCombatActor> actors,
        DateTime nowUtc,
        bool broadcastAttackVfx)
    {
        var actualHits = new List<ProximityCombatAttack>();
        var pendingAttacks = new Queue<ProximityCombatAttack>(attacks);
        while (pendingAttacks.Count > 0)
        {
            var attack = pendingAttacks.Dequeue();
            int damage = attack.Damage;
            if (damage <= 0)
                continue;

            bool attackerStillValid = matchingSessions.Any(session =>
                                          session.PlayerId == attack.AttackerPlayerId &&
                                          !session.IsEliminated &&
                                          session.CurrentCorruption < Config.SURVIVOR_MAX_CORRUPTION &&
                                          session.CurrentArea == attack.Area) ||
                                      matchingBots.Any(bot =>
                                          bot.PlayerId == attack.AttackerPlayerId &&
                                          !bot.IsEliminated &&
                                          bot.Corruption < Config.SURVIVOR_MAX_CORRUPTION &&
                                          bot.CurrentArea == attack.Area);
            if (!attackerStillValid)
                continue;

            bool hitApplied = false;
            var targetSession = matchingSessions.FirstOrDefault(session =>
                session.PlayerId == attack.TargetPlayerId &&
                !session.IsEliminated &&
                session.CurrentArea == attack.Area);
            if (targetSession != null)
            {
                targetSession.ApplyProximityAutoCombatHit(
                    attack.AttackerPlayerId,
                    attack.Area,
                    attack.WeaponItemId,
                    damage);
                hitApplied = true;
            }
            else
            {
                var targetBot = matchingBots.FirstOrDefault(bot =>
                    bot.PlayerId == attack.TargetPlayerId &&
                    !bot.IsEliminated &&
                    bot.Corruption < Config.SURVIVOR_MAX_CORRUPTION &&
                    bot.CurrentArea == attack.Area);
                if (targetBot == null)
                    continue;

                _gameEventLogManager.LogSurvivorHit(
                    matchingId,
                    attack.AttackerPlayerId,
                    attack.TargetPlayerId,
                    attack.WeaponItemId,
                    damage,
                    targetBot.Corruption < Config.SURVIVOR_MAX_CORRUPTION && targetBot.Corruption + damage >= Config.SURVIVOR_MAX_CORRUPTION,
                    BotPlayerManager.IsBotPlayerId(attack.AttackerPlayerId),
                    DateTimeOffset.UtcNow);
                _botPlayerManager.ApplyProximityAutoCombatDamage(
                    targetBot, damage, attack.AttackerPlayerId);
                hitApplied = true;
            }

            if (!hitApplied)
                continue;

            actualHits.Add(attack);
            NotifySurvivorOrbResonanceDamaged(matchingId, attack.TargetPlayerId, nowUtc);

            if (!attack.IsResonanceProc &&
                TryConsumeSunConcentration(matchingId, attack, nowUtc))
            {
                matchingSessions.FirstOrDefault(session =>
                    session.PlayerId == attack.AttackerPlayerId && !session.IsEliminated)
                    ?.SendSurvivorOrbResonanceFeedback(SurvivorOrbColor.Red);
                int burstDamage = Math.Max(1, (int)Math.Ceiling(attack.Damage * SurvivorOrbData.SunBurstDamageMultiplier));
                pendingAttacks.Enqueue(attack with
                {
                    Damage = burstDamage,
                    SunResonanceStage = 0,
                    WaveResonanceArmed = false,
                    IsResonanceProc = true
                });

                if (attack.SunResonanceStage >= 5)
                {
                    foreach (long secondaryTargetId in FindSunBurstSecondaryTargetIds(actors, attack))
                    {
                        pendingAttacks.Enqueue(attack with
                        {
                            TargetPlayerId = secondaryTargetId,
                            Damage = burstDamage,
                            SunResonanceStage = 0,
                            WaveResonanceArmed = false,
                            IsResonanceProc = true
                        });
                    }
                }
            }

            if (!attack.IsResonanceProc &&
                TryTriggerWaveCounter(
                    matchingId,
                    attack.TargetPlayerId,
                    attack.AttackerPlayerId,
                    nowUtc,
                    resonanceStates,
                    out int waveItemId,
                    out int waveCounterDamage))
            {
                matchingSessions.FirstOrDefault(session =>
                    session.PlayerId == attack.TargetPlayerId && !session.IsEliminated)
                    ?.SendSurvivorOrbResonanceFeedback(SurvivorOrbColor.Blue);
                pendingAttacks.Enqueue(new ProximityCombatAttack(
                    attack.TargetPlayerId,
                    attack.AttackerPlayerId,
                    attack.Area,
                    waveItemId,
                    waveCounterDamage,
                    attack.ProjectileWidth,
                    attack.EffectDurationSeconds,
                    IsResonanceProc: true));
                ApplySurvivorWaveSlow(
                    attack.AttackerPlayerId,
                    matchingSessions,
                    matchingBots,
                    nowUtc);
            }

            var attackerSession = matchingSessions.FirstOrDefault(session =>
                session.PlayerId == attack.AttackerPlayerId && !session.IsEliminated);
            if (!attack.IsWaveAreaSecondary)
            {
                attackerSession?.SendProximityAutoCombatAttackFeedback(
                    attack.TargetPlayerId,
                    attack.Area,
                    attack.WeaponItemId,
                    damage);
                if (broadcastAttackVfx)
                    BroadcastObservedProximityAttackVfx(attack, matchingSessions);
            }

            logger.LogDebug(
                "Proximity auto attack: MatchingId={MatchingId}, Attacker={Attacker}, Target={Target}, Area={Area}, WeaponItemId={WeaponItemId}, Damage={Damage}, ResonanceProc={ResonanceProc}",
                matchingId,
                attack.AttackerPlayerId,
                attack.TargetPlayerId,
                attack.Area,
                attack.WeaponItemId,
                damage,
                attack.IsResonanceProc);
        }

        _gameEventLogManager.LogSurvivorOrbAttackTargets(matchingId, attacks, actualHits, activeOrbColors);

        foreach (var bot in matchingBots)
        {
            if (!_botPlayerManager.TryFinalizeProximityAutoCombatElimination(bot, matchingId))
                continue;

            ProcessBotElimination(matchingId, bot.PlayerId, EliminationReason.MENTAL_ZERO, activeSessions,
                attackerPlayerId: bot.LastProximityAttackerPlayerId);
        }
    }

    private static IEnumerable<long> FindSunBurstSecondaryTargetIds(
        IReadOnlyCollection<ProximityCombatActor> actors,
        ProximityCombatAttack attack)
    {
        var target = actors.FirstOrDefault(actor => actor.PlayerId == attack.TargetPlayerId && actor.Area == attack.Area);
        if (target.PlayerId == 0)
            return Array.Empty<long>();

        float radius = attack.SunResonanceStage >= 5
            ? SurvivorOrbData.SunBurstRadius * SurvivorOrbData.SunFiveBurstRadiusMultiplier
            : SurvivorOrbData.SunBurstRadius;
        float radiusSquared = radius * radius;
        return actors
            .Where(actor => actor.PlayerId != attack.AttackerPlayerId &&
                            actor.PlayerId != attack.TargetPlayerId &&
                            actor.Area == attack.Area)
            .GroupBy(actor => actor.PlayerId)
            .Select(group => group.First())
            .Where(actor =>
            {
                float dx = actor.Position.X - target.Position.X;
                float dy = actor.Position.Y - target.Position.Y;
                return dx * dx + dy * dy <= radiusSquared;
            })
            .OrderBy(actor => actor.PlayerId)
            .Take(SurvivorOrbData.SunFiveBurstMaxTargets - 1)
            .Select(actor => actor.PlayerId)
            .ToArray();
    }

    private static void ApplySurvivorWaveSlow(
        long targetPlayerId,
        IReadOnlyCollection<GameClientSession> matchingSessions,
        IReadOnlyCollection<BotPlayerState> matchingBots,
        DateTime nowUtc)
    {
        int durationMilliseconds = (int)Math.Round(SurvivorOrbData.WaveSlowSeconds * 1000f);
        var session = matchingSessions.FirstOrDefault(candidate =>
            candidate.PlayerId == targetPlayerId && !candidate.IsEliminated);
        session?.SendSurvivorWaveSlowFeedback(0, durationMilliseconds);

        var bot = matchingBots.FirstOrDefault(candidate =>
            candidate.PlayerId == targetPlayerId && !candidate.IsEliminated);
        if (bot != null)
            bot.WaveSlowUntilUtc = nowUtc.AddSeconds(SurvivorOrbData.WaveSlowSeconds);
    }
    private static void BroadcastObservedProximityAttackVfx(
        ProximityCombatAttack attack,
        IReadOnlyCollection<GameClientSession> matchingSessions)
    {
        foreach (var observer in matchingSessions)
        {
            if (!observer.PlayerId.HasValue || observer.IsEliminated ||
                observer.PlayerId.Value == attack.AttackerPlayerId ||
                observer.PlayerId.Value == attack.TargetPlayerId ||
                observer.CurrentArea != attack.Area)
                continue;

            using var packet = Packet.Create((int)Protocol.G_TO_C_PROXIMITY_ATTACK_VFX);
            packet.SetBody(MessagePackSerializer.Serialize(new G_TO_C_PROXIMITY_ATTACK_VFX
            {
                AttackerPlayerId = attack.AttackerPlayerId,
                TargetPlayerId = attack.TargetPlayerId,
                AreaType = attack.Area,
                WeaponItemId = attack.WeaponItemId
            }));
            observer.Send(packet);
        }
    }

    private static void BroadcastDodgeablePvpAttackLaunch(
        ProximityCombatAttack attack,
        IReadOnlyCollection<GameClientSession> matchingSessions)
    {
        foreach (var observer in matchingSessions)
        {
            if (!observer.PlayerId.HasValue || observer.IsEliminated ||
                observer.CurrentArea != attack.Area)
                continue;

            using var packet = Packet.Create((int)Protocol.G_TO_C_PROXIMITY_ATTACK_VFX);
            packet.SetBody(MessagePackSerializer.Serialize(new G_TO_C_PROXIMITY_ATTACK_VFX
            {
                AttackerPlayerId = attack.AttackerPlayerId,
                TargetPlayerId = attack.TargetPlayerId,
                AreaType = attack.Area,
                WeaponItemId = attack.WeaponItemId
            }));
            observer.Send(packet);
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

    private readonly record struct ProximityCombatAreaEntryState(
        AreaType Area,
        DateTime ReadyAtUtc,
        AreaType PreviousArea,
        DateTime PreviousAreaLeftAtUtc);

    private readonly record struct SurvivorOrbVisualState(
        AreaType Area,
        int WeaponItemId,
        bool IsActive,
        string OrbItemSignature);

}
