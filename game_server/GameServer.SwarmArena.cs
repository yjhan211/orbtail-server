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

    // 봇 오염 자연 회복: 회복 오브 운에 기대지 않는 생존 바닥. 마지막 피격 후 유예가
    // 지나면 초당 일정량 회복한다 — "도망 성공"이 실제 생존이 되게 (계측: 매치 2223에서
    // 봇 오염이 단조 증가해 85초 전멸). 사람은 위로 오브가 같은 역할을 하므로 제외.
    private const double SwarmBotRecoveryGraceSeconds = 4d;
    private const int SwarmBotRecoveryPerSecond = 4;

    private readonly Dictionary<(long MatchingId, long PlayerId), DateTime> _swarmBotLastDamagedAtUtc = new();
    private readonly Dictionary<(long MatchingId, long PlayerId), DateTime> _swarmBotNextRecoveryAtUtc = new();

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
        var bots = _botPlayerManager.GetBots(matchingId).ToList();
        // 봇 전용 매치(어드민 검증)에서도 스웜을 돌린다 — 생존·완주 계측의 기반.
        if (sessions.Count == 0 && bots.Count == 0)
            return;

        if (!_swarmArenaManager.HasMatching(matchingId))
        {
            long humanPlayerId = sessions.Count > 0 ? sessions[0].PlayerId!.Value : bots[0].PlayerId;
            if (!_swarmArenaManager.InitializeMatching(matchingId, humanPlayerId, DateTime.UtcNow))
                return;

            GameClientSession.SwarmExploreNoiseCallback ??=
                (noiseMatchingId, noisePlayerId) =>
                    _swarmArenaManager.AttractSwarm(noiseMatchingId, noisePlayerId);
            ApplySwarmExploreSpotBudget(matchingId, sessions);
            LogSwarmPairZoneDistances(matchingId);
            // M4: 폐쇄 시계는 스웜 개전과 함께 돈다 (2:00 시작방 → 3:50 성장 구역 → 5:10 복도)
            _areaClosureManager.InitializeMatching(matchingId, wavesOverride: SwarmClosureWaves);
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
        ProcessSwarmBotRecovery(matchingId, aliveBots, nowUtc);

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
    private const float SwarmBotContactDamageMultiplier = 0.5f;

    // 시작방 탐색 스팟 수는 스폰 운의 균등을 위해 방당 이 개수로 맞춘다.
    private const int SwarmStartRoomSpotCount = 2;

    // 전 맵 개봉 재고 예산 (성장곡선 v3): 시작방 6×2 + 도서관·강당 2씩 + 교실 1씩 +
    // 운동장 3 = 21. 여기 없는 구역은 0 — 재고 고갈이 이동과 조우를 만들도록 총량을 조인다.
    private static readonly Dictionary<AreaType, int> SwarmExploreSpotBudget = new()
    {
        [AreaType.Library] = 2,
        [AreaType.Gym] = 2,
        [AreaType.Classroom3] = 1,
        [AreaType.Classroom4] = 1,
        [AreaType.Ground] = 3
    };

    // M4 종반 수렴 (결정 브리프 2026-08-06): 폐쇄가 스웜을 밀어낸다.
    // 2:00 시작방 → 3:50 성장 구역 → 5:10 복도, 운동장 종착. 경고는 폐쇄 15초 전.
    private static readonly IReadOnlyList<ClosureWaveDefinition> SwarmClosureWaves =
    [
        new(120, SurvivorRoyaleSpawnData.GetPhaseRoomCandidates().ToArray(), 4),
        new(230, [
            AreaType.Library, AreaType.Gym, AreaType.Classroom3,
            AreaType.Classroom4, AreaType.BroadcastRoom
        ], 8),
        new(310, [AreaType.Corridor], 12)
    ];

    // 폐쇄 흐름 그래프: 폐쇄된 구역의 잔존 스웜이 밀려나는 목적지.
    // 시작방은 각 쌍의 조우 지점으로, 성장 구역은 복도로, 복도는 운동장으로.
    private static readonly Dictionary<AreaType, AreaType> SwarmEvacuationFlow = new()
    {
        [AreaType.ExamRoom] = AreaType.Library,
        [AreaType.Storage] = AreaType.Library,
        [AreaType.Classroom2] = AreaType.Gym,
        [AreaType.Storage2] = AreaType.Gym,
        [AreaType.AdminOffice] = AreaType.Corridor,
        [AreaType.StaffRoom] = AreaType.Corridor,
        [AreaType.Library] = AreaType.Corridor,
        [AreaType.Gym] = AreaType.Corridor,
        [AreaType.Classroom3] = AreaType.Corridor,
        [AreaType.Classroom4] = AreaType.Corridor,
        [AreaType.BroadcastRoom] = AreaType.Corridor,
        [AreaType.Corridor] = AreaType.Ground
    };

    /// <summary>
    ///     스웜 모드 폐쇄 틱 (1초): 웨이브 경고·폐쇄를 브로드캐스트하고,
    ///     폐쇄 순간 잔존 스웜을 폐쇄 흐름 그래프의 다음 구역으로 이주시킨다.
    /// </summary>
    private void ProcessSwarmClosureTick(long matchingId)
    {
        if (!_swarmArenaManager.HasMatching(matchingId))
            return;

        var sessions = _clientSessions.Values
            .Where(session => session.PlayerId.HasValue && session.CurrentMapSubId == matchingId)
            .ToList();

        var closureTick = _areaClosureManager.CheckClosureSchedule(matchingId);
        foreach (var warningArea in closureTick.WarningAreas)
        {
            using var packet = global::network.packets.Packet.Create((int)Protocol.G_TO_C_AREA_CLOSURE_WARNING);
            packet.SetBody(MessagePack.MessagePackSerializer.Serialize(new G_TO_C_AREA_CLOSURE_WARNING
            {
                AreaType = warningArea,
                SecondsRemaining = closureTick.WarningSeconds,
                ClosureAtUnixMs = closureTick.ClosureAtUnixMs
            }));
            foreach (var session in sessions) session.Send(packet);
        }

        var evacuatedMonsters = new List<MonsterRuntimeInfo>();
        foreach (var closedArea in closureTick.ClosedAreas)
        {
            _gameEventLogManager.LogClosure(matchingId, closedArea.ToString());
            using (var packet = global::network.packets.Packet.Create((int)Protocol.G_TO_C_AREA_CLOSED))
            {
                packet.SetBody(MessagePack.MessagePackSerializer.Serialize(new G_TO_C_AREA_CLOSED
                {
                    AreaType = closedArea,
                    IsClosed = true
                }));
                foreach (var session in sessions) session.Send(packet);
            }

            if (!SwarmEvacuationFlow.TryGetValue(closedArea, out var destination))
                continue;

            var moved = _swarmArenaManager.EvacuateArea(matchingId, closedArea, destination);
            evacuatedMonsters.AddRange(moved);
            if (moved.Count > 0)
                logger.LogInformation(
                    "Swarm closure evacuation: MatchingId={MatchingId}, {From} → {To}, Monsters={Count}",
                    matchingId, closedArea, destination, moved.Count);
        }

        if (evacuatedMonsters.Count > 0)
        {
            BroadcastMonsterSnapshot(matchingId, sessions, evacuatedMonsters);
            BroadcastMonsterMinimapSnapshot(sessions, _swarmArenaManager.GetVisualStates(matchingId));
        }
    }

    // 쌍 깔때기: 시작방 → 만남 구역. 거리 편차의 보정값(잔상 스폰 시점)은 이 로그를 계측한 뒤 정한다.
    private static readonly (AreaType StartRoom, AreaType PairZone)[] SwarmPairZones =
    [
        (AreaType.ExamRoom, AreaType.Library),
        (AreaType.Storage, AreaType.Library),
        (AreaType.Classroom2, AreaType.Gym),
        (AreaType.Storage2, AreaType.Gym),
        (AreaType.AdminOffice, AreaType.Corridor),
        (AreaType.StaffRoom, AreaType.Corridor)
    ];

    /// <summary>
    ///     쌍별 시작방→만남 구역 경로 길이를 매치 시작 시 한 번 로그로 남긴다.
    ///     공정성 판정(편차가 첫 성장 시각을 가르는지)의 계측 기준이다.
    /// </summary>
    private void LogSwarmPairZoneDistances(long matchingId)
    {
        foreach (var (startRoom, pairZone) in SwarmPairZones)
        {
            var path = BotPathfinder.FindPath(
                MapId.School,
                startRoom, GameMapData.GetAreaSpawnCell(MapId.School, startRoom),
                pairZone, GameMapData.GetAreaSpawnCell(MapId.School, pairZone));
            logger.LogInformation(
                "Swarm pair distance: MatchingId={MatchingId}, StartRoom={StartRoom}, PairZone={PairZone}, Steps={Steps}",
                matchingId, startRoom, pairZone, path?.Count ?? -1);
        }
    }

    /// <summary>
    ///     구역별 스팟 예산 적용: 예산 초과분을 매치 시작 시 선소진 처리한다.
    ///     CSV·씬은 건드리지 않고 소진 쿨다운 저장소만 쓴다 (id 오름차순으로 앞의 N개 유지).
    /// </summary>
    private void ApplySwarmExploreSpotBudget(long matchingId, List<GameClientSession> sessions)
    {
        var startRooms = SurvivorRoyaleSpawnData.GetPhaseRoomCandidates().ToHashSet();
        foreach (var group in GameInteractableData.GetAll()
                     .Where(info => info.InteractionType == InteractionType.RNG_COLLECT)
                     .GroupBy(info => (AreaType)info.ZoneId))
        {
            int budget = startRooms.Contains(group.Key)
                ? SwarmStartRoomSpotCount
                : SwarmExploreSpotBudget.GetValueOrDefault(group.Key);
            foreach (var spot in group.OrderBy(info => info.Id).Skip(budget))
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
            int exploreCost = GetSwarmBotExploreCost(matchingId, bot.PlayerId);
            if (_summonStoneManager.GetSnapshot(matchingId, bot.PlayerId).StoneCount < exploreCost)
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
                costOverride: exploreCost);
            if (!attempt.Success)
            {
                RngCollectCooldownStore.ClearCooldown(matchingId, spot.Id);
                continue;
            }

            BroadcastSwarmExploreConsumed(spot.Id, sessions);
            // 요약 카운터(summonCount) 배선 — 봇 개봉이 매치 요약에서 0으로 잡히던 계측 구멍.
            _gameEventLogManager.LogOrbSummonAttempt(
                matchingId,
                bot.PlayerId,
                true,
                ErrorCode.SUCCESS,
                attempt.ItemId,
                attempt.State.StoneCount,
                attempt.State.NextCost,
                attempt.State.SuccessfulSummonCount,
                bot.CurrentArea.ToString(),
                isBot: true);
            logger.LogInformation(
                "Swarm bot explore: MatchingId={MatchingId}, BotId={BotId}, InteractId={InteractId}, ItemId={ItemId}, Cost={Cost}",
                matchingId, bot.PlayerId, spot.Id, attempt.ItemId, exploreCost);
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
    // 쓰레기장은 문 잠금(113·114·118·119)으로 도달 불가.
    // 6인 깔때기: 쌍 구역(도서관·강당)과 복도층 교실(3-2·4-2)·운동장이 순례 목적지.
    private static readonly AreaType[] SwarmHuntingAreas =
    [
        AreaType.Ground, AreaType.Gym, AreaType.Library,
        AreaType.Classroom3, AreaType.Classroom4
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

        // 0) 폐쇄 구역 탈출 최우선 — 폐쇄 흐름 그래프를 따라 첫 열린 구역으로 대피한다 (M4).
        if (_areaClosureManager.IsAreaClosed(matchingId, bot.CurrentArea))
        {
            var evacuationArea = bot.CurrentArea;
            while (SwarmEvacuationFlow.TryGetValue(evacuationArea, out var nextArea))
            {
                evacuationArea = nextArea;
                if (!_areaClosureManager.IsAreaClosed(matchingId, evacuationArea))
                    break;
            }

            if (!_areaClosureManager.IsAreaClosed(matchingId, evacuationArea))
            {
                Cell evacuationCell = GameMapData.GetAreaSpawnCell(MapId.School, evacuationArea);
                return new SpotArenaBotDirective(
                    SpotArenaBotMode.Escort,
                    evacuationArea,
                    evacuationCell,
                    BotPlayerManager.CellToWorldPosition(MapId.School, evacuationCell));
            }
        }

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
            GetSwarmBotExploreCost(matchingId, botPlayerId) &&
            TryFindNearestAvailableExploreSpot(
                matchingId, area: null, bot.Position, out var spot, out _) &&
            !_areaClosureManager.IsAreaClosed(matchingId, (AreaType)spot.ZoneId))
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

        // 3) 시작방·복도는 공급이 마른다 — 무한 스폰 사냥터로 이주해 소환석을 번다.
        if (bot.CurrentArea == AreaType.Corridor ||
            SurvivorRoyaleSpawnData.GetPhaseRoomCandidates().Contains(bot.CurrentArea))
        {
            // 폐쇄된 사냥터는 제외 — 전부 닫혔으면 종착지 운동장으로 (운동장은 폐쇄되지 않는다).
            AreaType huntingArea = SwarmHuntingAreas
                .Where(area => !_areaClosureManager.IsAreaClosed(matchingId, area))
                .OrderBy(area =>
                {
                    var center = BotPlayerManager.CellToWorldPosition(
                        MapId.School, GameMapData.GetAreaSpawnCell(MapId.School, area));
                    float dx = center.X - bot.Position.X;
                    float dy = center.Y - bot.Position.Y;
                    return dx * dx + dy * dy;
                })
                .DefaultIfEmpty(AreaType.Ground)
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

    /// <summary>봇 개봉 비용 — 사람과 같은 비례식(기본 3석 + 보유 오브당 2석)을 쓴다.</summary>
    private int GetSwarmBotExploreCost(long matchingId, long botPlayerId) =>
        Config.GetSwarmExploreCost(_inGameInventoryManager.CountOrbs(matchingId, botPlayerId));

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

        // 봇은 사람 수준의 마이크로 회피가 없어 같은 수치로는 조우 전에 녹는다 (계측:
        // 첫 탈락 19초, 40초 반수 사망). 봇의 역할은 조우·경제 흐름 재현이므로 피격만 보정한다.
        int botDamage = Math.Max(1, (int)(damage.Damage * SwarmBotContactDamageMultiplier));
        bot.Corruption = Math.Min(Config.SURVIVOR_MAX_CORRUPTION, bot.Corruption + botDamage);
        _swarmBotLastDamagedAtUtc[(matchingId, bot.PlayerId)] = DateTime.UtcNow;
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
            _swarmBotLastDamagedAtUtc[(matchingId, bot.PlayerId)] = DateTime.UtcNow;
        }

        allSessions.FirstOrDefault(session => session.PlayerId == attack.AttackerPlayerId)
            ?.SendProximityAutoCombatAttackFeedback(
                attack.TargetPlayerId, attack.Area, attack.WeaponItemId, damage);
        BroadcastSpotArenaAttackVfxToTargetAndObservers(attack, allSessions);
    }

    /// <summary>
    ///     비접촉 유예를 넘긴 봇의 오염을 1초 단위로 회복한다. 피격이 들어오면
    ///     유예가 리셋되므로, 스웜에 물려 있는 동안에는 회복되지 않는다.
    /// </summary>
    private void ProcessSwarmBotRecovery(long matchingId, List<BotPlayerState> aliveBots, DateTime nowUtc)
    {
        foreach (var bot in aliveBots)
        {
            if (bot.Corruption <= 0)
                continue;

            var key = (matchingId, bot.PlayerId);
            if (_swarmBotLastDamagedAtUtc.TryGetValue(key, out var lastDamagedAtUtc) &&
                (nowUtc - lastDamagedAtUtc).TotalSeconds < SwarmBotRecoveryGraceSeconds)
                continue;
            if (_swarmBotNextRecoveryAtUtc.TryGetValue(key, out var nextRecoveryAtUtc) &&
                nowUtc < nextRecoveryAtUtc)
                continue;

            _swarmBotNextRecoveryAtUtc[key] = nowUtc.AddSeconds(1d);
            bot.Corruption = Math.Max(0, bot.Corruption - SwarmBotRecoveryPerSecond);
        }
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
        foreach (var key in _swarmBotLastDamagedAtUtc.Keys.Where(key => key.MatchingId == matchingId).ToList())
            _swarmBotLastDamagedAtUtc.Remove(key);
        foreach (var key in _swarmBotNextRecoveryAtUtc.Keys.Where(key => key.MatchingId == matchingId).ToList())
            _swarmBotNextRecoveryAtUtc.Remove(key);
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
