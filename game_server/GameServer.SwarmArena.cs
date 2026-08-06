using game_server.network;
using game_server.services;
using Microsoft.Extensions.Logging;
using network.common;
using network.common.data;
using network.common.data.models;

namespace game_server;

public partial class GameServer
{
    private const int SwarmArenaBasicDamage = 12;
    private const float SwarmArenaBasicRange = 7f;
    private const float SwarmArenaBasicAttackIntervalSeconds = 1f;
    private const int SwarmArenaWeaponItemId = 107000010;

    // P0-b A/B: A안 = 1.0 (이동 무관), B안 = 0.4 (이동 중 공격 감쇠, Archero 문법).
    private const float SwarmMovingAttackMultiplier = 1f;
    private const float SwarmMovingSpeedThreshold = 1.5f;

    // 오브 CSV 수치는 구 잔상(고HP) 기준이라 유리 떼(HP 12)에는 너무 약하다.
    // 데미지 3배로 T1(4)도 원킬을 유지하고, 성장은 오브 수 = 처치 스트림 수로 체감시킨다.
    private const int SwarmOrbDamageMultiplier = 3;
    private const float SwarmOrbIntervalMultiplier = 0.6f;

    private readonly Dictionary<(long MatchingId, long PlayerId), (Vector3f Position, DateTime At, bool Moving)>
        _swarmMovementSamples = new();
    private readonly HashSet<long> _swarmOrbGrantedMatchings = new();

    /// <summary>
    ///     #217 8인 맵 역할 검증(M1). 매치 수명(탈락·최후 1인·타이머)은 기존 서바이버 로얄
    ///     흐름이 소유하고, 여기서는 스웜 디렉터 틱·접촉 피해·전투 액터·PvP만 돌린다.
    /// </summary>
    private void ProcessSwarmArenaForMatching(long matchingId, List<GameClientSession> activeSessions)
    {
        var sessions = activeSessions
            .Where(session => session.PlayerId.HasValue &&
                              session.CurrentMapSubId == matchingId &&
                              !session.IsGameEnded)
            .ToList();
        if (sessions.Count == 0)
            return;
        var bots = _botPlayerManager.GetBots(matchingId).ToList();

        if (!_swarmArenaManager.HasMatching(matchingId))
        {
            long humanPlayerId = sessions[0].PlayerId!.Value;
            if (!_swarmArenaManager.InitializeMatching(matchingId, humanPlayerId, DateTime.UtcNow))
                return;

            GameClientSession.SwarmExploreNoiseCallback ??=
                (noiseMatchingId, noisePlayerId) =>
                    _swarmArenaManager.AttractSwarm(noiseMatchingId, noisePlayerId);
            EqualizeStartRoomExploreSpots(matchingId, sessions);
            logger.LogInformation(
                "Swarm arena initialized: MatchingId={MatchingId}, Humans={HumanCount}, Bots={BotCount}",
                matchingId, sessions.Count, bots.Count);
        }

        if (_swarmOrbGrantedMatchings.Add(matchingId))
        {
            foreach (var session in sessions)
                session.GrantSwarmArenaOrb(SwarmArenaWeaponItemId);
        }

        DateTime nowUtc = DateTime.UtcNow;

        var aliveSessions = sessions.Where(session => !session.IsEliminated).ToList();
        var aliveBots = bots.Where(bot => !bot.IsEliminated).ToList();
        var participants = aliveSessions
            .Where(session => session.LastValidatedPosition != null)
            .Select(session => new SpotArenaPlayerSpatial(
                session.PlayerId!.Value, session.CurrentArea, session.LastValidatedPosition!))
            .Concat(aliveBots.Select(bot =>
                new SpotArenaPlayerSpatial(bot.PlayerId, bot.CurrentArea, bot.Position)))
            .ToList();

        var tick = _swarmArenaManager.Tick(matchingId, participants, nowUtc);

        foreach (var damage in tick.PlayerDamage)
            ApplySwarmParticipantDamage(matchingId, damage, aliveSessions, aliveBots, sessions);

        // 봇도 사람과 같은 규칙으로 성장한다: 소환석 5개 + 스팟 소진. 공짜 버튼 소환 없음.
        ProcessSwarmBotExplores(matchingId, aliveBots, sessions);

        if (TryConsumeMonsterPositionBroadcastSlot(matchingId, nowUtc))
            BroadcastMonsterMinimapSnapshot(sessions, _swarmArenaManager.GetVisualStates(matchingId));

        UpdateSwarmMovementSamples(matchingId, participants, nowUtc);
        var actors = BuildSwarmArenaCombatActors(matchingId, aliveSessions, aliveBots);
        ProcessSurvivorOrbRecovery(matchingId, actors, aliveSessions, aliveBots, nowUtc);
        BroadcastSurvivorOrbVisualStates(matchingId, actors, sessions);
        var attacks = _proximityAutoCombatResolver.Resolve(
            matchingId,
            actors,
            nowUtc,
            (attacker, target) => !attacker.IsMonsterTarget &&
                                  (target.IsMonsterTarget
                                      ? attacker.Area == target.Area
                                      : ProximityCombatLineOfSight.CanTarget(attacker, target)));
        foreach (var attack in attacks)
        {
            var damageResult = _swarmArenaManager.ApplyMonsterDamage(
                matchingId, attack.TargetPlayerId, attack.AttackerPlayerId, attack.Damage);
            if (damageResult.Applied)
            {
                sessions.FirstOrDefault(session => session.PlayerId == attack.AttackerPlayerId)
                    ?.SendEmotionAfterimageMonsterAttackFeedback(
                        damageResult.MonsterId, attack.Area, attack.WeaponItemId, attack.Damage);
                if (damageResult.Killed && damageResult.MonsterState != null)
                    SpawnSpotArenaSummonStone(matchingId, damageResult.MonsterState, sessions);
                continue;
            }

            // PvP는 저데미지 보조다. 킬의 주 경로는 스웜이어야 한다 (#217 결합 원칙).
            ApplySwarmPvpAttack(matchingId, attack, aliveSessions, aliveBots, sessions);
        }

        // 봇 탈락 확정은 기존 근접전투 파이프라인과 동일한 경로를 쓴다.
        foreach (var bot in aliveBots)
        {
            if (!_botPlayerManager.TryFinalizeProximityAutoCombatElimination(bot, matchingId))
                continue;

            ProcessBotElimination(matchingId, bot.PlayerId, EliminationReason.MENTAL_ZERO, activeSessions,
                attackerPlayerId: bot.LastProximityAttackerPlayerId);
        }
    }

    private const float SwarmBotOpenRange = 1.6f;

    // 시작방 탐색 스팟 수는 스폰 운의 균등을 위해 방당 이 개수로 맞춘다.
    // 최소 보유 방(쓰레기장 2개)이 기준 — 늘리려면 씬·CSV에 스팟 추가가 필요하다.
    private const int SwarmStartRoomSpotCount = 2;

    /// <summary>
    ///     시작방별 스팟 수 균일화: 초과분을 매치 시작 시 선소진 처리한다.
    ///     CSV·씬은 건드리지 않고 소진 쿨다운 저장소만 쓴다 (id 오름차순으로 앞의 N개 유지).
    /// </summary>
    private void EqualizeStartRoomExploreSpots(long matchingId, List<GameClientSession> sessions)
    {
        foreach (var area in SurvivorRoyaleSpawnData.GetPhaseRoomCandidates())
        {
            var excessSpots = GameInteractableData.GetAll()
                .Where(info => info.ZoneId == (int)area &&
                               info.InteractionType == InteractionType.RNG_COLLECT)
                .OrderBy(info => info.Id)
                .Skip(SwarmStartRoomSpotCount);
            foreach (var spot in excessSpots)
            {
                if (RngCollectCooldownStore.TryAcquireCooldown(
                        matchingId, spot.Id, Config.SWARM_EXPLORE_CONSUME_SECONDS, out _))
                    BroadcastSwarmExploreConsumed(spot.Id, sessions);
            }
        }
    }

    /// <summary>
    ///     봇의 스팟 개봉: 게이지 없이 반경 안에서 즉시 연다. 소진 스팟은 사람·봇 공용
    ///     쿨다운 저장소로 잠기므로, 유한 스팟을 둘러싼 경쟁이 성립한다.
    /// </summary>
    private void ProcessSwarmBotExplores(
        long matchingId,
        List<BotPlayerState> bots,
        List<GameClientSession> sessions)
    {
        foreach (var bot in bots)
        {
            if (_summonStoneManager.GetSnapshot(matchingId, bot.PlayerId).StoneCount <
                Config.SWARM_EXPLORE_SUMMON_COST)
                continue;

            if (!TryFindNearestAvailableExploreSpot(
                    matchingId, bot.CurrentArea, bot.Position, out var spot, out float distance) ||
                distance > SwarmBotOpenRange)
                continue;

            if (!RngCollectCooldownStore.TryAcquireCooldown(
                    matchingId, spot.Id, Config.SWARM_EXPLORE_CONSUME_SECONDS, out _))
                continue;

            TryDestroyBotOverflowOrb(matchingId, bot);
            var attempt = _summonStoneManager.TrySummon(
                matchingId,
                bot.PlayerId,
                itemId => _inGameInventoryManager.TryAddItemWithCapacity(
                    matchingId,
                    bot.PlayerId,
                    itemId,
                    Config.SURVIVOR_INVENTORY_SLOT_COUNT,
                    out var addedItem)
                    ? addedItem
                    : null,
                SelectBotSummonChoice(matchingId, bot),
                costOverride: Config.SWARM_EXPLORE_SUMMON_COST);
            if (!attempt.Success)
            {
                RngCollectCooldownStore.ClearCooldown(matchingId, spot.Id);
                continue;
            }

            BroadcastSwarmExploreConsumed(spot.Id, sessions);
            logger.LogInformation(
                "Swarm bot explore: MatchingId={MatchingId}, BotId={BotId}, InteractId={InteractId}, ItemId={ItemId}",
                matchingId, bot.PlayerId, spot.Id, attempt.ItemId);
        }
    }

    private bool TryFindNearestAvailableExploreSpot(
        long matchingId,
        AreaType area,
        Vector3f position,
        out InteractableInfoData spot,
        out float distance)
    {
        spot = null!;
        distance = float.MaxValue;
        var onCooldown = RngCollectCooldownStore.GetSnapshot(matchingId)
            .Where(entry => entry.RemainingSeconds > 0)
            .Select(entry => entry.InteractId)
            .ToHashSet();
        foreach (var info in GameInteractableData.GetAll())
        {
            if (info.ZoneId != (int)area ||
                info.InteractionType != InteractionType.RNG_COLLECT ||
                onCooldown.Contains(info.Id))
                continue;

            var world = BotPlayerManager.CellToWorldPosition(
                MapId.School, new Cell(info.CellX, info.CellY));
            float dx = world.X - position.X;
            float dy = world.Y - position.Y;
            float candidateDistance = MathF.Sqrt(dx * dx + dy * dy);
            if (candidateDistance < distance)
            {
                distance = candidateDistance;
                spot = info;
            }
        }

        return spot != null;
    }

    private static void BroadcastSwarmExploreConsumed(int interactId, List<GameClientSession> sessions)
    {
        var body = MessagePack.MessagePackSerializer.Serialize(new G_TO_C_RNG_COLLECT_COOLDOWN_BROADCAST
        {
            InteractId = interactId,
            CooldownSeconds = Config.SWARM_EXPLORE_CONSUME_SECONDS
        });
        foreach (var session in sessions)
        {
            if (!session.PlayerId.HasValue)
                continue;
            using var packet = global::network.packets.Packet.Create(
                (int)Protocol.G_TO_C_RNG_COLLECT_COOLDOWN_BROADCAST, session.PlayerId.Value);
            packet.SetBody(body);
            session.Send(packet);
        }
    }

    /// <summary>
    ///     봇 이동 지시 라우팅: 도주(생존)가 최우선이고, 소환석이 차 있으면 가장 가까운
    ///     미소진 스팟으로 순례하며, 둘 다 아니면 스웜 디렉터의 배회를 따른다.
    /// </summary>
    private SpotArenaBotDirective ResolveSwarmBotDirective(long matchingId, long botPlayerId)
    {
        var directive = _swarmArenaManager.GetBotDirective(matchingId, botPlayerId);
        if (directive.Mode != SpotArenaBotMode.Escort)
            return directive;

        var bot = _botPlayerManager.GetBots(matchingId)
            .FirstOrDefault(candidate => candidate.PlayerId == botPlayerId);
        if (bot == null || bot.IsEliminated)
            return directive;
        if (_summonStoneManager.GetSnapshot(matchingId, botPlayerId).StoneCount <
            Config.SWARM_EXPLORE_SUMMON_COST)
            return directive;
        if (!TryFindNearestAvailableExploreSpot(
                matchingId, bot.CurrentArea, bot.Position, out var spot, out _))
            return directive;

        Cell spotCell = new(spot.CellX, spot.CellY);
        if (!GameMapData.IsMoveablePosition(MapId.School, spotCell))
        {
            spotCell = spotCell.GetAdjacentCells().FirstOrDefault(cell =>
                GameMapData.IsMoveablePosition(MapId.School, cell) &&
                GameMapData.GetCurrentArea(MapId.School, cell) == bot.CurrentArea) ?? spotCell;
        }

        return new SpotArenaBotDirective(
            SpotArenaBotMode.Escort,
            bot.CurrentArea,
            spotCell,
            BotPlayerManager.CellToWorldPosition(MapId.School, spotCell));
    }

    private void ApplySwarmParticipantDamage(
        long matchingId,
        SpotArenaPlayerDamage damage,
        List<GameClientSession> aliveSessions,
        List<BotPlayerState> aliveBots,
        List<GameClientSession> allSessions)
    {
        var session = aliveSessions.FirstOrDefault(candidate =>
            candidate.PlayerId == damage.TargetPlayerId);
        if (session != null)
        {
            // 기존 잔상 피격 경로 — 오염 증가·피격 피드백·일반 탈락 흐름까지 담당한다.
            session.ApplyEmotionAfterimageMonsterHit(damage.MonsterId, damage.Damage);
            return;
        }

        var bot = aliveBots.FirstOrDefault(candidate => candidate.PlayerId == damage.TargetPlayerId);
        if (bot == null)
            return;

        bot.Corruption = Math.Min(Config.SURVIVOR_MAX_CORRUPTION, bot.Corruption + damage.Damage);
    }

    private void ApplySwarmPvpAttack(
        long matchingId,
        ProximityCombatAttack attack,
        List<GameClientSession> aliveSessions,
        List<BotPlayerState> aliveBots,
        List<GameClientSession> allSessions)
    {
        int damage = Math.Min(attack.Damage, SwarmArenaManager.PvpDamage);
        var targetSession = aliveSessions.FirstOrDefault(session =>
            session.PlayerId == attack.TargetPlayerId);
        if (targetSession != null)
        {
            targetSession.ApplyProximityAutoCombatHit(attack.AttackerPlayerId, attack.Area,
                attack.WeaponItemId, damage);
        }
        else
        {
            var bot = aliveBots.FirstOrDefault(candidate => candidate.PlayerId == attack.TargetPlayerId);
            if (bot == null)
                return;

            bot.Corruption = Math.Min(Config.SURVIVOR_MAX_CORRUPTION, bot.Corruption + damage);
            bot.LastProximityAttackerPlayerId = attack.AttackerPlayerId;
        }

        allSessions.FirstOrDefault(session => session.PlayerId == attack.AttackerPlayerId)
            ?.SendProximityAutoCombatAttackFeedback(
                attack.TargetPlayerId, attack.Area, attack.WeaponItemId, damage);
        BroadcastSpotArenaAttackVfxToTargetAndObservers(attack, allSessions);
    }

    private void UpdateSwarmMovementSamples(
        long matchingId,
        IReadOnlyCollection<SpotArenaPlayerSpatial> participants,
        DateTime nowUtc)
    {
        foreach (var participant in participants)
        {
            var key = (matchingId, participant.PlayerId);
            if (!_swarmMovementSamples.TryGetValue(key, out var sample))
            {
                _swarmMovementSamples[key] = (participant.Position, nowUtc, false);
                continue;
            }

            double elapsed = (nowUtc - sample.At).TotalSeconds;
            if (elapsed < 0.1d)
                continue;

            float dx = participant.Position.X - sample.Position.X;
            float dy = participant.Position.Y - sample.Position.Y;
            float speed = MathF.Sqrt(dx * dx + dy * dy) / (float)elapsed;
            _swarmMovementSamples[key] =
                (participant.Position, nowUtc, speed >= SwarmMovingSpeedThreshold);
        }
    }

    private bool IsSwarmParticipantMoving(long matchingId, long playerId) =>
        _swarmMovementSamples.TryGetValue((matchingId, playerId), out var sample) && sample.Moving;

    private void CleanupSwarmArenaState(long matchingId)
    {
        _swarmArenaManager.RemoveMatching(matchingId);
        _swarmOrbGrantedMatchings.Remove(matchingId);
        foreach (var key in _swarmMovementSamples.Keys.Where(key => key.MatchingId == matchingId).ToList())
            _swarmMovementSamples.Remove(key);
    }

    private List<ProximityCombatActor> BuildSwarmArenaCombatActors(
        long matchingId,
        List<GameClientSession> aliveSessions,
        List<BotPlayerState> aliveBots)
    {
        var actors = new List<ProximityCombatActor>();
        foreach (var session in aliveSessions)
        {
            if (session.PlayerId.HasValue &&
                session.LastValidatedPosition != null &&
                TryCreateSpatialActor(
                    session.PlayerId.Value,
                    session.CurrentMapId,
                    session.CurrentArea,
                    session.LastValidatedPosition,
                    out var spatial))
            {
                AddSwarmParticipantCombatActors(actors, matchingId, spatial);
            }
        }

        MapId botMapId = _botPlayerManager.GetMatchingMapId(matchingId);
        foreach (var bot in aliveBots)
        {
            if (TryCreateSpatialActor(bot.PlayerId, botMapId, bot.CurrentArea, bot.Position, out var botSpatial))
                AddSwarmParticipantCombatActors(actors, matchingId, botSpatial);
        }

        foreach (var target in _swarmArenaManager.GetCombatTargets(matchingId))
        {
            actors.Add(new ProximityCombatActor(
                target.CombatTargetId,
                target.Area,
                target.Position,
                0,
                0f,
                0,
                0f,
                MapId: MapId.School,
                Cell: ProximityCombatLineOfSight.WorldPositionToCell(MapId.School, target.Position),
                IsMonsterTarget: true,
                TargetPriority: 1));
        }

        return actors;
    }

    /// <summary>
    ///     보드의 오브가 곧 화력이다. 오브가 있으면 오브별 공격 문법(기존 인벤토리 액터)을
    ///     스웜 배율로 얹고, 없을 때만 기본 공격 하나로 싸운다 — 드래프트가 성장 체감이 되게.
    /// </summary>
    private void AddSwarmParticipantCombatActors(
        List<ProximityCombatActor> actors,
        long matchingId,
        ProximityCombatActor spatial)
    {
        var fallback = CreateSwarmParticipantActor(matchingId, spatial);
        var inventory = _inGameInventoryManager.GetPlayerInventory(matchingId, spatial.PlayerId);
        if (!inventory.GetAllItems().Any(item => item.Count > 0))
        {
            actors.Add(fallback);
            return;
        }

        int before = actors.Count;
        AddInventoryCombatActors(
            actors,
            fallback with
            {
                AttackRange = 0f,
                Damage = 0,
                AttackIntervalSeconds = 0f
            },
            inventory,
            resonanceState: default);
        for (int index = before; index < actors.Count; index++)
        {
            var actor = actors[index];
            actors[index] = actor with
            {
                Damage = actor.Damage * SwarmOrbDamageMultiplier,
                AttackIntervalSeconds = actor.AttackIntervalSeconds * SwarmOrbIntervalMultiplier
            };
        }
    }

    private ProximityCombatActor CreateSwarmParticipantActor(long matchingId, ProximityCombatActor spatial)
    {
        int damage = SwarmArenaBasicDamage;
        if (IsSwarmParticipantMoving(matchingId, spatial.PlayerId))
            damage = Math.Max(1, (int)(damage * SwarmMovingAttackMultiplier));

        return spatial with
        {
            WeaponItemId = SwarmArenaWeaponItemId,
            AttackRange = SwarmArenaBasicRange,
            Damage = damage,
            AttackIntervalSeconds = SwarmArenaBasicAttackIntervalSeconds,
            WeaponItemUid = spatial.PlayerId,
            TargetPriority = 0
        };
    }
}
