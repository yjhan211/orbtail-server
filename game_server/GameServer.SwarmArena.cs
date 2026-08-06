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

    // P0-b 정지 공격 규칙(하드 컷): 이동 중에는 공격하지 않는다. 감쇠안(0.4)은 상대가
    // 읽을 수 없고 무빙 최적해를 남겨서 기각 — #217 기획 코멘트 참조.
    private static readonly bool SwarmStopToAttackEnabled = false;
    private const float SwarmMovingSpeedThreshold = 1.5f;

    // 정지를 이 시간 이상 유지해야 무장된다 — 끊어 걷기(스텝 샷)가 무료가 되지 않게.
    private const double SwarmStopAimSeconds = 0.3d;

    // 오브 CSV 수치는 구 잔상(고HP) 기준이라 유리 떼(HP 12)에는 너무 약하다.
    // 데미지 3배로 T1(4)도 원킬을 유지하고, 성장은 오브 수 = 처치 스트림 수로 체감시킨다.
    private const int SwarmOrbDamageMultiplier = 3;
    private const float SwarmOrbIntervalMultiplier = 0.6f;

    private readonly Dictionary<(long MatchingId, long PlayerId),
        (Vector3f Position, DateTime At, bool Moving, DateTime StoppedAtUtc)> _swarmMovementSamples = new();
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
        var actors = BuildSwarmArenaCombatActors(matchingId, aliveSessions, aliveBots, nowUtc);
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
        AreaType? area,
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
            if ((area.HasValue && info.ZoneId != (int)area.Value) ||
                info.InteractionType != InteractionType.RNG_COLLECT ||
                info.ZoneId == (int)AreaType.Corridor ||
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

    // 시작방 팩이 마르면 봇이 이주할 무한 스폰 사냥터.
    // 쓰레기장은 문 잠금(113·114·118·119)으로 도달 불가, 보건실·3-2는 시작방으로 승격되어 제외.
    // 조우 지점(도서관·강당)과 운동장·3-1이 순례 목적지.
    private static readonly AreaType[] SwarmHuntingAreas =
    [
        AreaType.Ground, AreaType.Gym, AreaType.Library, AreaType.Classroom4
    ];

    /// <summary>
    ///     봇 이동 지시 라우팅: 도주(생존) > 바닥 소환석 줍기 > 전 구역 스팟 순례 >
    ///     마른 방 탈출(사냥터 이주) > 스웜 디렉터 배회.
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

        // 1) 같은 구역 바닥 소환석 — 걸어가면 자동 픽업 반경(1.75)이 줍는다.
        var groundStone = _groundItemManager.GetSnapshot(matchingId, bot.CurrentArea)
            .Where(item => item.ItemId == Config.SUMMON_STONE_GROUND_ITEM_ID)
            .OrderBy(item =>
            {
                float dx = item.PositionX - bot.Position.X;
                float dy = item.PositionY - bot.Position.Y;
                return dx * dx + dy * dy;
            })
            .FirstOrDefault();
        if (groundStone != null)
        {
            Cell stoneCell = ProximityCombatLineOfSight.WorldPositionToCell(
                MapId.School, new Vector3f(groundStone.PositionX, groundStone.PositionY, 0f));
            return new SpotArenaBotDirective(
                SpotArenaBotMode.Escort,
                bot.CurrentArea,
                stoneCell,
                new Vector3f(groundStone.PositionX, groundStone.PositionY, 0f));
        }

        // 2) 소환석이 차면 전 구역에서 가장 가까운 미소진 스팟으로 순례 (구역 간 이동 포함).
        if (_summonStoneManager.GetSnapshot(matchingId, botPlayerId).StoneCount >=
            Config.SWARM_EXPLORE_SUMMON_COST &&
            TryFindNearestAvailableExploreSpot(
                matchingId, area: null, bot.Position, out var spot, out _))
        {
            var spotArea = (AreaType)spot.ZoneId;
            Cell spotCell = new(spot.CellX, spot.CellY);
            if (!GameMapData.IsMoveablePosition(MapId.School, spotCell))
            {
                spotCell = spotCell.GetAdjacentCells().FirstOrDefault(cell =>
                    GameMapData.IsMoveablePosition(MapId.School, cell) &&
                    GameMapData.GetCurrentArea(MapId.School, cell) == spotArea) ?? spotCell;
            }

            return new SpotArenaBotDirective(
                SpotArenaBotMode.Escort,
                spotArea,
                spotCell,
                BotPlayerManager.CellToWorldPosition(MapId.School, spotCell));
        }

        // 3) 정지 공격 규칙: 도주·줍기·개봉 용무가 없고 사거리 안에 잔상이 있으면
        //    제자리에 선다 — 이동 중에는 공격이 나가지 않으므로 서야 사냥이 된다.
        //    도주(반경 4)가 먼저 걸리므로 정지 위치는 항상 4~7 거리의 안전 사격 지점이다.
        if (HasSwarmMonsterInBasicRange(matchingId, bot))
        {
            return new SpotArenaBotDirective(
                SpotArenaBotMode.Escort,
                bot.CurrentArea,
                ProximityCombatLineOfSight.WorldPositionToCell(MapId.School, bot.Position),
                bot.Position);
        }

        // 4) 시작방·복도는 공급이 마른다 — 무한 스폰 사냥터로 이주해 소환석을 번다.
        if (bot.CurrentArea == AreaType.Corridor ||
            SurvivorRoyaleSpawnData.GetPhaseRoomCandidates().Contains(bot.CurrentArea))
        {
            AreaType huntingArea = SwarmHuntingAreas
                .OrderBy(area =>
                {
                    var center = BotPlayerManager.CellToWorldPosition(
                        MapId.School, GameMapData.GetAreaSpawnCell(MapId.School, area));
                    float dx = center.X - bot.Position.X;
                    float dy = center.Y - bot.Position.Y;
                    return dx * dx + dy * dy;
                })
                .First();
            Cell huntingCell = GameMapData.GetAreaSpawnCell(MapId.School, huntingArea);
            return new SpotArenaBotDirective(
                SpotArenaBotMode.Escort,
                huntingArea,
                huntingCell,
                BotPlayerManager.CellToWorldPosition(MapId.School, huntingCell));
        }

        return directive;
    }

    private bool HasSwarmMonsterInBasicRange(long matchingId, BotPlayerState bot)
    {
        const float rangeSquared = SwarmArenaBasicRange * SwarmArenaBasicRange;
        foreach (var target in _swarmArenaManager.GetCombatTargets(matchingId))
        {
            if (target.Area != bot.CurrentArea)
                continue;
            float dx = target.Position.X - bot.Position.X;
            float dy = target.Position.Y - bot.Position.Y;
            if (dx * dx + dy * dy <= rangeSquared)
                return true;
        }

        return false;
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
                _swarmMovementSamples[key] = (participant.Position, nowUtc, false, nowUtc);
                continue;
            }

            double elapsed = (nowUtc - sample.At).TotalSeconds;
            if (elapsed < 0.1d)
                continue;

            float dx = participant.Position.X - sample.Position.X;
            float dy = participant.Position.Y - sample.Position.Y;
            float speed = MathF.Sqrt(dx * dx + dy * dy) / (float)elapsed;
            bool moving = speed >= SwarmMovingSpeedThreshold;
            DateTime stoppedAtUtc = moving || sample.Moving ? nowUtc : sample.StoppedAtUtc;
            _swarmMovementSamples[key] = (participant.Position, nowUtc, moving, stoppedAtUtc);
        }
    }

    /// <summary>
    ///     정지 공격 규칙: 정지를 SwarmStopAimSeconds 이상 유지해야 공격이 무장된다.
    ///     샘플이 아직 없으면(막 합류) 다음 틱부터 판정한다.
    /// </summary>
    private bool IsSwarmAttackArmed(long matchingId, long playerId, DateTime nowUtc)
    {
        if (!SwarmStopToAttackEnabled)
            return true;
        if (!_swarmMovementSamples.TryGetValue((matchingId, playerId), out var sample))
            return false;
        return !sample.Moving && (nowUtc - sample.StoppedAtUtc).TotalSeconds >= SwarmStopAimSeconds;
    }

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
        List<BotPlayerState> aliveBots,
        DateTime nowUtc)
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
                AddSwarmParticipantCombatActors(actors, matchingId, spatial, nowUtc);
            }
        }

        MapId botMapId = _botPlayerManager.GetMatchingMapId(matchingId);
        foreach (var bot in aliveBots)
        {
            if (TryCreateSpatialActor(bot.PlayerId, botMapId, bot.CurrentArea, bot.Position, out var botSpatial))
                AddSwarmParticipantCombatActors(actors, matchingId, botSpatial, nowUtc);
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
    ///     비무장(이동 중)이면 모든 공격 액터의 데미지를 0으로 눕힌다 — 리졸버가 공격자에서
    ///     제외하고 조준 상태를 해제하되, 피격 대상으로는 남는다.
    /// </summary>
    private void AddSwarmParticipantCombatActors(
        List<ProximityCombatActor> actors,
        long matchingId,
        ProximityCombatActor spatial,
        DateTime nowUtc)
    {
        bool armed = IsSwarmAttackArmed(matchingId, spatial.PlayerId, nowUtc);
        var fallback = CreateSwarmParticipantActor(spatial, armed);
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
                Damage = armed ? actor.Damage * SwarmOrbDamageMultiplier : 0,
                AttackIntervalSeconds = actor.AttackIntervalSeconds * SwarmOrbIntervalMultiplier
            };
        }
    }

    private ProximityCombatActor CreateSwarmParticipantActor(ProximityCombatActor spatial, bool armed)
    {
        return spatial with
        {
            WeaponItemId = SwarmArenaWeaponItemId,
            AttackRange = SwarmArenaBasicRange,
            Damage = armed ? SwarmArenaBasicDamage : 0,
            AttackIntervalSeconds = SwarmArenaBasicAttackIntervalSeconds,
            WeaponItemUid = spatial.PlayerId,
            TargetPriority = 0
        };
    }
}
