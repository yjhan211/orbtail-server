using game_server.network;
using game_server.services;
using MessagePack;
using Microsoft.Extensions.Logging;
using network.common;
using network.common.data;
using network.common.data.models;
using network.helpers;
using network.packets;

namespace game_server;

public partial class GameServer
{
    private const int SwarmArenaBasicDamage = 12;
    // 사거리는 클라 표시(PlayerRangeRing)와 공유 — Config가 단일 출처다.
    private const float SwarmArenaBasicRange = Config.SWARM_ORB_ATTACK_RANGE;
    private const float SwarmArenaBasicAttackIntervalSeconds = 1f;
    private const int SwarmArenaWeaponItemId = 107000010;
    private static readonly int[] SwarmStartingOrbPool = [107000010, 107000020, 107000030];

    // 플레이어 단위 지급 (2026-08-09): 매칭 단위 1회 지급은 지급 틱에 아직 접속 전인
    // 사람을 영영 빈손으로 만들었다 — 늦게 합류해도 첫 등장 틱에 각자 1회 받는다.
    private readonly HashSet<(long MatchingId, long PlayerId)> _swarmStartingOrbGrantedPlayers = new();

    // P0-b 정지 공격 규칙(하드 컷): 이동 중에는 공격하지 않는다. 감쇠안(0.4)은 상대가
    // 읽을 수 없고 무빙 최적해를 남겨서 기각 — #217 기획 코멘트 참조.
    // #219 M1: 캠프 모드에서 부활 시도 → 두 번째 플레이 판정에서도 불쾌 (2026-08-07).
    // SB 원형이지만 오브 궤도 연출은 정지 사격 자세가 없어 "멈추면 쏜다"가 읽히지 않는다.
    // 이동 중 공격으로 확정하고, 정지 보너스류는 사거리·연출이 생긴 뒤 재검토.
    private static readonly bool SwarmStopToAttackEnabled = false;
    private const float SwarmMovingSpeedThreshold = 1.5f;

    // 정지를 이 시간 이상 유지해야 무장된다 — 끊어 걷기(스텝 샷)가 무료가 되지 않게.
    private const double SwarmStopAimSeconds = 0.3d;

    // #219 M1 게이트: 자기장(보관), 유닛 낱개 체력(클론 전투 모델).
    private static readonly bool SwarmFieldEnabled = false;
    private static readonly bool SwarmOrbHealthEnabled = true;

    // 오브 CSV 수치는 구 잔상(고HP) 기준이라 데미지만 3배 보정한다.
    // 공속 가속(0.6)은 초반 스팸으로 판정되어 퇴역 — CSV 기본 리듬(2026-08-07).
    private const int SwarmOrbDamageMultiplier = 3;
    private const float SwarmOrbIntervalMultiplier = 1f;

    // 연사화 (#222): 오브별 주기·발당 데미지를 함께 줄여 DPS 불변으로 발사 밀도를 올린다.
    // 오브가 많아지면 총 발사 간격이 최소 스페이싱(0.15초×오브 수) 밑으로 내려가지 않게
    // 캡 — 캡이 걸리면 발당 데미지가 그 비율만큼 굵어져 DPS는 유지된다.
    // 0.6 (#222 M4): 바람이 공속 축이 되면서 기본 연사를 살짝 늦춰 바람의 여지를 만든다.
    private const float SwarmOrbRapidFireScale = 0.6f;
    private const float SwarmOrbMinShotSpacingSeconds = 0.15f;

    private readonly Dictionary<(long MatchingId, long PlayerId),
        (Vector3f Position, DateTime At, bool Moving, DateTime StoppedAtUtc)> _swarmMovementSamples = new();

    // 봇 오염 자연 회복: 회복 오브 운에 기대지 않는 생존 바닥. 마지막 피격 후 유예가
    // 지나면 초당 일정량 회복한다 — "도망 성공"이 실제 생존이 되게 (계측: 매치 2223에서
    // 봇 오염이 단조 증가해 85초 전멸). 사람은 위로 오브가 같은 역할을 하므로 제외.
    private const double SwarmBotRecoveryGraceSeconds = 4d;
    private const int SwarmBotRecoveryPerSecond = 4;

    private readonly Dictionary<(long MatchingId, long PlayerId), DateTime> _swarmBotLastDamagedAtUtc = new();
    private readonly Dictionary<(long MatchingId, long PlayerId), DateTime> _swarmBotNextRecoveryAtUtc = new();

    // SB 유닛 개별 체력: 접촉·PvP는 오브 HP를 깎고, HP가 0이 된 오브만 파괴된다.
    // 최저 티어 오브가 항상 앞줄에서 맞는다 — 파괴 순서와 같은 규칙. 접촉 피해량은 몬스터 종이 결정.
    private readonly Dictionary<(long MatchingId, long PlayerId), (int ItemId, int Hp)> _swarmFrontOrbHp = new();

    // 몬스터 착탄 지연: 발사 즉시 판정하되 피해는 투사체 비행시간 뒤에 정산한다.
    private readonly List<(long MatchingId, long CombatTargetId, long AttackerId, int Damage, DateTime ApplyAtUtc)>
        _pendingSwarmMonsterHits = new();

    // 잼 리더보드 (#222 M3): 마지막 브로드캐스트 시그니처 — 변동이 없으면 재전송하지 않는다.
    private readonly Dictionary<long, string> _swarmJamRankingsSignature = new();

    // 잼 헌트 만료 판정 (#222 M3-2): 중복 정산 가드 + 게이트 없는 매치(봇 전용)의 대체 앵커.
    private readonly HashSet<long> _swarmTimeoutEndedMatchings = new();
    private readonly Dictionary<long, DateTime> _swarmMatchFallbackAnchorUtc = new();

    // 젬 광산 (#222 M3, SB 원작 각본): 개전 150초 개장(살포 시작) → 225초 폭발(대량 낙수 후 종료).
    // SB는 잔여 0:57 가동 → 0:15 폭발("최대 젬 공급원, 차지하는 쪽이 이긴다") — 4분 매치로 환산.
    // 클라 상주 이펙트·안내(GemMineDirector)와 같은 시각·지점 상수를 쓴다.
    private const float GemMineOpenSeconds = 150f;
    private const float GemMineExplodeSeconds = 225f;
    private const float GemMineX = 39.34f;
    private const float GemMineY = 49.97f;
    private const int GemMineOpenBurstJam = 6;
    private const int GemMineScatterJam = 2;
    private const int GemMineExplosionJam = 20;
    private const double GemMineScatterIntervalSeconds = 6d;

    private readonly HashSet<long> _swarmGemMineOpenedMatchings = new();
    private readonly HashSet<long> _swarmGemMineExplodedMatchings = new();
    private readonly Dictionary<long, DateTime> _swarmGemMineNextScatterUtc = new();

    private void ProcessPendingSwarmMonsterHits(
        long matchingId, DateTime nowUtc, List<GameClientSession> sessions)
    {
        for (int index = _pendingSwarmMonsterHits.Count - 1; index >= 0; index--)
        {
            var hit = _pendingSwarmMonsterHits[index];
            if (hit.MatchingId != matchingId || nowUtc < hit.ApplyAtUtc)
                continue;

            _pendingSwarmMonsterHits.RemoveAt(index);
            var damageResult = _swarmArenaManager.ApplyMonsterDamage(
                matchingId, hit.CombatTargetId, hit.AttackerId, hit.Damage);
            // 비행 중 몬스터가 이미 죽었으면 조용히 소멸 — 이중 정산 없음.
            if (damageResult.Applied && damageResult.Killed && damageResult.MonsterState != null)
                SpawnSpotArenaSummonStone(
                    matchingId, damageResult.MonsterState, sessions, damageResult.JamReward,
                    damageResult.HeartReward, damageResult.BootsReward, damageResult.KeyReward);
        }
    }

    // 티어별 오브 HP는 Common(SurvivorOrbData.GetSquadOrbMaxHp)이 단일 출처 — 클라 체력바와 공유.
    private static int GetSquadOrbMaxHp(int tier) => SurvivorOrbData.GetSquadOrbMaxHp(tier);

    /// <summary>
    ///     #217 8인 맵 역할 검증(M1). 매치 수명(탈락·최후 1인·타이머)은 기존 서바이버 로얄
    ///     흐름이 소유하고, 여기서는 스웜 디렉터 틱·접촉 피해·전투 액터·PvP만 돌린다.
    /// </summary>
    private void ProcessSwarmArenaForMatching(long matchingId, List<GameClientSession> activeSessions)
    {
        // 탐사 모드(SOLO_MAP_VALIDATION=1): 맵 검증용 1인 매치 — 캠프 몹·접촉 피해·
        // 전투·오브 스트림을 전부 끈다. 이동·문·탐색만 남는다.
        // SOLO_MONSTERS=1을 얹으면 캠프 몹만 되살린다 (봇 없이 몹 상대 검증).
        if (MatchStartGate.IsSoloMapValidationEnabled && !MatchStartGate.IsSoloMonstersEnabled)
            return;

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
            // 하트 = 본체 오염 + 앞줄 오브 HP 회복 (#222 M4, 원작 하트는 스쿼드도 회복).
            // 엔트리 제거 = 만충 취급 — 다음 오브 비주얼 틱에 체력바·크랙이 함께 복구된다.
            GameClientSession.SwarmHeartPickupCallback ??=
                (healMatchingId, healPlayerId) =>
                    _swarmFrontOrbHp.Remove((healMatchingId, healPlayerId));
            // 하트 픽업 게이트: 앞줄 오브가 상했으면 오염 0이어도 줍는다 (원작 만피 게이트의 근사).
            GroundItemPickupPolicy.FrontOrbDamagedResolver ??=
                (gateMatchingId, gatePlayerId) =>
                    _swarmFrontOrbHp.ContainsKey((gateMatchingId, gatePlayerId));
            LogSwarmPairZoneDistances(matchingId);
            // M4 자기장: 안전 거리 수축 시계는 스웜 개전과 함께 돈다.
            // #219 M1: 클론에서는 자기장을 무장하지 않는다 — 수렴은 M3의 광산 각본이 담당한다.
            if (SwarmFieldEnabled)
                _swarmFieldStartedAtUtc[matchingId] = DateTime.UtcNow;
            logger.LogInformation(
                "Swarm pressure field armed: MatchingId={MatchingId}, MaxDistance={MaxDistance}, " +
                "HoldSeconds={Hold}, ShrinkSeconds={Shrink}",
                matchingId, SwarmPressureField.MaxDistance, SwarmFieldHoldSeconds, SwarmFieldShrinkSeconds);
            logger.LogInformation(
                "Swarm arena initialized: MatchingId={MatchingId}, Humans={HumanCount}, Bots={BotCount}",
                matchingId, sessions.Count, bots.Count);
        }

        // #219 M2: 시작 스쿼드 = 랜덤 1오브 (사람·봇 공통) — 첫 캠프를 버틸 최소 화력만 주고
        // 빌드는 드래프트가 만든다. 빈손이 되면 개봉 무료 규칙이 재기를 보장.
        foreach (var session in sessions)
        {
            if (!session.PlayerId.HasValue ||
                !_swarmStartingOrbGrantedPlayers.Add((matchingId, session.PlayerId.Value)))
                continue;

            // 잼 지갑 리셋 (#222 M3) — 세션이 매치를 넘어 살아있으므로 시작 지급 시점에 초기화.
            session.ResetJam();
            session.FreeSummonCharges = 0;
            session.GrantSwarmArenaOrb(
                SwarmStartingOrbPool[Random.Shared.Next(SwarmStartingOrbPool.Length)]);
        }

        foreach (var bot in bots)
        {
            if (!_swarmStartingOrbGrantedPlayers.Add((matchingId, bot.PlayerId)))
                continue;

            _inGameInventoryManager.TryAddItemWithCapacity(
                matchingId, bot.PlayerId,
                SwarmStartingOrbPool[Random.Shared.Next(SwarmStartingOrbPool.Length)],
                Config.SWARM_ORB_CAPACITY, out _);
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
        ProcessSwarmGemMine(matchingId, nowUtc, sessions);
        BroadcastSwarmJamRankings(matchingId, sessions, bots);
        ProcessSwarmJamHuntTimeout(matchingId, nowUtc, sessions, aliveSessions, aliveBots);
        // 지난 틱에 예약된 착탄들을 먼저 정산한다 — 체력바가 폭발 시점에 맞춰 닳는다.
        ProcessPendingSwarmMonsterHits(matchingId, nowUtc, sessions);

        var attacks = _proximityAutoCombatResolver.Resolve(
            matchingId,
            actors,
            nowUtc,
            // PvP도 몬스터와 같은 타원(dy×2) 판정 (#222): 링 스프라이트가 아이소 타원이라
            // 원형 판정은 세로 방향에서 보이는 링의 2배 거리까지 공격이 성립했다 —
            // "링이 겹치기만 해도 공격"으로 읽히던 체감의 원인. 이제 표시가 곧 판정이다.
            (attacker, target) => !attacker.IsMonsterTarget &&
                                  IsWithinSwarmOrbRange(attacker, target) &&
                                  (target.IsMonsterTarget
                                      ? attacker.Area == target.Area
                                      : ProximityCombatLineOfSight.CanTarget(attacker, target)));
        Dictionary<long, ProximityCombatActor>? actorById = null;
        foreach (var attack in attacks)
        {
            int monsterId = _swarmArenaManager.GetMonsterIdForCombatTarget(matchingId, attack.TargetPlayerId);
            if (monsterId > 0)
            {
                // 발사 연출은 즉시, 피해는 투사체 비행시간 뒤에 — 체력바와 폭발이 일치한다.
                sessions.FirstOrDefault(session => session.PlayerId == attack.AttackerPlayerId)
                    ?.SendEmotionAfterimageMonsterAttackFeedback(
                        monsterId, attack.Area, attack.WeaponItemId, attack.Damage);

                // 관전자에게도 발사 연출 (#219): 공격자 피드백만으로는 봇의 사냥이 완전 무음이었다.
                // 클라 관전 분기(TargetPlayerId < 0 → 몬스터)가 받는 음수 id로 실어 보낸다.
                BroadcastSpotArenaAttackVfxToTargetAndObservers(
                    attack with { TargetPlayerId = -monsterId }, sessions);

                actorById ??= actors
                    .GroupBy(actor => actor.PlayerId)
                    .ToDictionary(group => group.Key, group => group.First());
                float distance = actorById.TryGetValue(attack.AttackerPlayerId, out var attackerActor) &&
                                 actorById.TryGetValue(attack.TargetPlayerId, out var targetActor)
                    ? Vector3f.Distance(attackerActor.Position, targetActor.Position)
                    : Config.SWARM_ORB_ATTACK_RANGE;
                double delaySeconds =
                    SurvivorOrbData.GetPvpProjectileImpactDelaySeconds(attack.WeaponItemId, distance);
                _pendingSwarmMonsterHits.Add((matchingId, attack.TargetPlayerId, attack.AttackerPlayerId,
                    attack.Damage, nowUtc.AddSeconds(delaySeconds)));
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

    // 스팟 예산 선소진(#217 성장곡선 v3, 21개)은 퇴역 — SB에는 인위적 봉인이 없고,
    // 희소성은 리젠(60초)과 크기 비례 비용이 담당한다. 배치된 스팟은 전부 살아 있다.

    // 자기장 스케줄 (M4 종반 수렴, 자기장 전환 2026-08-06): 60초 유예 후 안전 거리가
    // 최대 보행 거리에서 0까지 선형 수축한다 (5:30 완료, 운동장만 안전).
    // 깔때기 순서(시작방 → 쌍 구역 → 복도)는 보행 거리가 먼 순서로 자연 재현된다.
    private const double SwarmFieldHoldSeconds = 60d;
    private const double SwarmFieldShrinkSeconds = 270d;
    private const int SwarmFieldWarningLeadSeconds = 15;

    // 경계 밖 오염 (리소스 틱 5초당): 기본 + 초과 거리 6셀당 1. 문턱에서 즉사가 아니라
    // "슬슬 따가움 → 깊을수록 아픔"의 경사 — 외곽 마지막 개봉 도박이 성립해야 한다.
    private const int SwarmFieldBaseCorruptionPerTick = 2;
    private const int SwarmFieldDistancePerExtraCorruption = 6;

    private readonly System.Collections.Concurrent.ConcurrentDictionary<long, DateTime>
        _swarmFieldStartedAtUtc = new();
    private readonly Dictionary<long, HashSet<AreaType>> _swarmFieldWarnedAreas = new();
    private readonly Dictionary<long, HashSet<AreaType>> _swarmFieldOutsideAreas = new();

    /// <summary>현재 안전 거리. 수축 전에는 int.MaxValue(전 맵 안전).</summary>
    private int GetSwarmSafeDistance(long matchingId, DateTime nowUtc)
    {
        if (!_swarmFieldStartedAtUtc.TryGetValue(matchingId, out var startedAtUtc))
            return int.MaxValue;

        double shrinkElapsed = (nowUtc - startedAtUtc).TotalSeconds - SwarmFieldHoldSeconds;
        if (shrinkElapsed <= 0) return int.MaxValue;

        double progress = Math.Min(1d, shrinkElapsed / SwarmFieldShrinkSeconds);
        return (int)Math.Ceiling(SwarmPressureField.MaxDistance * (1d - progress));
    }

    /// <summary>구역 전체가 현재 경계 밖(폐쇄·자기장)인가 — 봇 대피·스팟 필터의 기준.</summary>
    private bool IsSwarmAreaOutside(long matchingId, AreaType area) =>
        _areaClosureManager.IsAreaClosed(matchingId, area) ||
        SwarmPressureField.GetAreaMinDistance(area) > GetSwarmSafeDistance(matchingId, DateTime.UtcNow);

    /// <summary>자기장 오염 (리소스 틱당). 경계 안이면 0, 밖이면 기본 + 초과 거리 비례.</summary>
    private int GetSwarmFieldCorruptionPerTick(long matchingId, Vector3f worldPosition)
    {
        if (!Config.SWARM_P0_ENABLED || worldPosition == null)
            return 0;

        int safeDistance = GetSwarmSafeDistance(matchingId, DateTime.UtcNow);
        if (safeDistance == int.MaxValue)
            return 0;

        var cell = ProximityCombatLineOfSight.WorldPositionToCell(MapId.School, worldPosition);
        int over = SwarmPressureField.GetDistance(cell) - safeDistance;
        if (over <= 0) return 0;
        return SwarmFieldBaseCorruptionPerTick + over / SwarmFieldDistancePerExtraCorruption;
    }

    /// <summary>구역 전체가 경계 밖이 되는 시각까지 남은 초.</summary>
    private int GetSecondsUntilAreaOutside(long matchingId, int areaMinDistance, DateTime nowUtc)
    {
        if (!_swarmFieldStartedAtUtc.TryGetValue(matchingId, out var startedAtUtc))
            return int.MaxValue;

        double outsideElapsed = SwarmFieldHoldSeconds + SwarmFieldShrinkSeconds *
            (1d - (double)areaMinDistance / SwarmPressureField.MaxDistance);
        return (int)Math.Max(0d, (startedAtUtc.AddSeconds(outsideElapsed) - nowUtc).TotalSeconds);
    }

    /// <summary>
    ///     스웜 자기장 틱 (1초): 안전 거리를 수축시키며, 구역이 곧 완전히 밖이 되면 경고를,
    ///     완전히 밖이 되면 폐쇄를 한 번씩 브로드캐스트한다 — 미니맵은 기존 폐쇄 표시를 재사용한다.
    ///     실제 압박(오염)은 구역이 아니라 참가자 셀의 보행 거리 기준으로 리소스 틱이 준다.
    /// </summary>
    private void ProcessSwarmClosureTick(long matchingId)
    {
        if (!_swarmArenaManager.HasMatching(matchingId))
            return;
        if (!_swarmFieldStartedAtUtc.ContainsKey(matchingId))
            return;

        DateTime nowUtc = DateTime.UtcNow;
        int safeNow = GetSwarmSafeDistance(matchingId, nowUtc);
        int safeAtLead = GetSwarmSafeDistance(matchingId, nowUtc.AddSeconds(SwarmFieldWarningLeadSeconds));
        if (safeAtLead == int.MaxValue)
            return;

        var sessions = _clientSessions.Values
            .Where(session => session.PlayerId.HasValue && session.CurrentMapSubId == matchingId)
            .ToList();
        if (!_swarmFieldWarnedAreas.TryGetValue(matchingId, out var warned))
            _swarmFieldWarnedAreas[matchingId] = warned = new HashSet<AreaType>();
        if (!_swarmFieldOutsideAreas.TryGetValue(matchingId, out var outside))
            _swarmFieldOutsideAreas[matchingId] = outside = new HashSet<AreaType>();

        foreach (var area in SwarmPressureField.GetKnownAreas())
        {
            int minDistance = SwarmPressureField.GetAreaMinDistance(area);
            if (minDistance <= 0)
                continue;

            if (minDistance > safeNow)
            {
                if (!outside.Add(area))
                    continue;

                _gameEventLogManager.LogClosure(matchingId, area.ToString());
                using var packet = global::network.packets.Packet.Create((int)Protocol.G_TO_C_AREA_CLOSED);
                packet.SetBody(MessagePack.MessagePackSerializer.Serialize(new G_TO_C_AREA_CLOSED
                {
                    AreaType = area,
                    IsClosed = true
                }));
                foreach (var session in sessions) session.Send(packet);
                continue;
            }

            if (minDistance <= safeAtLead || !warned.Add(area))
                continue;

            int secondsRemaining = GetSecondsUntilAreaOutside(matchingId, minDistance, nowUtc);
            using var warningPacket =
                global::network.packets.Packet.Create((int)Protocol.G_TO_C_AREA_CLOSURE_WARNING);
            warningPacket.SetBody(MessagePack.MessagePackSerializer.Serialize(new G_TO_C_AREA_CLOSURE_WARNING
            {
                AreaType = area,
                SecondsRemaining = secondsRemaining,
                ClosureAtUnixMs = DateTimeOffset.UtcNow.AddSeconds(secondsRemaining).ToUnixTimeMilliseconds()
            }));
            foreach (var session in sessions) session.Send(warningPacket);
        }
    }

    /// <summary>
    ///     #219 폐쇄 부활: 자기장 대신 시간 웨이브 스케줄(AreaClosureManager)로 구역을 닫는다.
    ///     경고 15초 → 폐쇄 브로드캐스트. 폐쇄 오염은 정산 틱(GetClosedAreaCorruptionPerTick),
    ///     신규 몹 스폰 정지는 캠프 리졸버, 봇·스팟 제외는 IsSwarmAreaOutside가 담당한다.
    /// </summary>
    private void ProcessSwarmScheduledClosureTick(long matchingId)
    {
        _areaClosureManager.InitializeMatching(matchingId);
        var closureTick = _areaClosureManager.CheckClosureSchedule(matchingId);
        if (closureTick.WarningAreas.Count == 0 && closureTick.ClosedAreas.Count == 0)
            return;

        var sessions = _clientSessions.Values
            .Where(session => session.PlayerId.HasValue && session.CurrentMapSubId == matchingId)
            .ToList();

        foreach (var area in closureTick.WarningAreas)
        {
            using var warningPacket =
                global::network.packets.Packet.Create((int)Protocol.G_TO_C_AREA_CLOSURE_WARNING);
            warningPacket.SetBody(MessagePack.MessagePackSerializer.Serialize(new G_TO_C_AREA_CLOSURE_WARNING
            {
                AreaType = area,
                SecondsRemaining = closureTick.WarningSeconds,
                ClosureAtUnixMs = closureTick.ClosureAtUnixMs
            }));
            foreach (var session in sessions) session.Send(warningPacket);
        }

        foreach (var area in closureTick.ClosedAreas)
        {
            _gameEventLogManager.LogClosure(matchingId, area.ToString());
            using var packet = global::network.packets.Packet.Create((int)Protocol.G_TO_C_AREA_CLOSED);
            packet.SetBody(MessagePack.MessagePackSerializer.Serialize(new G_TO_C_AREA_CLOSED
            {
                AreaType = area,
                IsClosed = true
            }));
            foreach (var session in sessions) session.Send(packet);
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
    ///     봇의 스팟 개봉: 게이지 없이 반경 안에서 즉시 연다. 소진 스팟은 사람·봇 공용
    ///     쿨다운 저장소로 잠기므로, 유한 스팟을 둘러싼 경쟁이 성립한다.
    /// </summary>
    // 봇 채집 채널 (#219 탐색 모션): 즉시 개봉은 모션도 없고 사람(1.5초 채집)보다 빨랐다.
    // 사람 클라와 같은 1.5초 채널 동안 EXPLORE_1 상태로 서 있다가 개봉을 확정한다.
    private const double SwarmBotExploreChannelSeconds = 1.5d;

    private void ProcessSwarmBotExplores(
        long matchingId,
        List<BotPlayerState> bots,
        List<GameClientSession> sessions)
    {
        foreach (var bot in bots)
        {
            // (2) 채널 진행 중 — 1.5초가 지나면 개봉 확정
            if (bot.SwarmExploreStartedAtUtc != DateTime.MinValue)
            {
                if ((DateTime.UtcNow - bot.SwarmExploreStartedAtUtc).TotalSeconds <
                    SwarmBotExploreChannelSeconds)
                    continue;

                FinishSwarmBotExplore(matchingId, bot, sessions);
                continue;
            }

            // (1) 채널 시작: 근접 + 자금 + 스팟 가용이면 쿨다운을 선점하고 채집 자세로 선다
            if (!TryFindNearestAvailableExploreSpot(
                    matchingId, bot.CurrentArea, bot.Position, out var spot, out float distance) ||
                distance > SwarmBotOpenRange)
                continue;

            // 위협 사거리 안에서는 채집을 열지 않는다 — 채널 홀드 채로 얻어맞는 사고 방지
            // (매치 2376 봇 -108: 빈손으로 채집 반복하며 인지 밖 파도 사거리에 일방 피격).
            float channelPower = GetSwarmSquadPower(matchingId, bot.PlayerId);
            FindNearbySwarmRivals(matchingId, bot, channelPower,
                includeMonstersAsStronger: channelPower <= 0f,
                out var channelThreatPosition, out _);
            if (channelThreatPosition != null)
                continue;

            int exploreCost = GetSwarmBotExploreCost(matchingId, bot.PlayerId);
            // 열쇠 (#222 M4): 충전이 있으면 자금 없이도 개봉을 연다.
            if (_summonStoneManager.GetSnapshot(matchingId, bot.PlayerId).StoneCount < exploreCost &&
                bot.FreeSummonCharges <= 0)
                continue;

            if (!RngCollectCooldownStore.TryAcquireCooldown(
                    matchingId, spot.Id, Config.SWARM_EXPLORE_REGEN_SECONDS, out _))
                continue;

            bot.SwarmExploreSpotId = spot.Id;
            bot.SwarmExploreStartedAtUtc = DateTime.UtcNow;
            bot.HoldForInteraction(TimeSpan.FromSeconds(SwarmBotExploreChannelSeconds + 0.5d));
            BroadcastBotExploreStarts(
                matchingId, [(bot.PlayerId, spot.Id, bot.CurrentArea)], sessions);
        }
    }

    private void FinishSwarmBotExplore(long matchingId, BotPlayerState bot, List<GameClientSession> sessions)
    {
        int spotId = bot.SwarmExploreSpotId;
        bot.SwarmExploreSpotId = 0;
        bot.SwarmExploreStartedAtUtc = DateTime.MinValue;
        BroadcastBotExploreEnds(matchingId, [(bot.PlayerId, bot.CurrentArea)], sessions);
        if (spotId <= 0)
            return;

        // 궤도 스쿼드: 파괴(버리기)는 퇴역 — 궤도가 가득 차면 개봉이 실패할 뿐이다.
        // 3머지 자동 압축이 자리를 만들고, 상한 도달은 성장의 자연 종점이다.
        int exploreCost = GetSwarmBotExploreCost(matchingId, bot.PlayerId);
        // 열쇠 (#222 M4): 충전이 있으면 이번 개봉 비용 0 — 성공 시 1 소비 (사람과 같은 규칙).
        bool useFreeSummon = bot.FreeSummonCharges > 0 && exploreCost > 0;
        if (useFreeSummon)
            exploreCost = 0;
        var attempt = _summonStoneManager.TrySummon(
            matchingId,
            bot.PlayerId,
            itemId => _inGameInventoryManager.TryAddItemWithCapacity(
                matchingId,
                bot.PlayerId,
                itemId,
                Config.SWARM_ORB_CAPACITY,
                out var addedItem)
                ? addedItem
                : null,
            choiceIndex: 0,
            costOverride: exploreCost,
            exactItemId: SurvivorOrbData.ApplyDraftTier(
                ChooseBotDraftOrbItemId(matchingId, bot.PlayerId, GetSwarmDraftTier(matchingId)),
                GetSwarmDraftTier(matchingId)));
        if (!attempt.Success)
        {
            RngCollectCooldownStore.ClearCooldown(matchingId, spotId);
            return;
        }

        if (useFreeSummon)
            bot.FreeSummonCharges = Math.Max(0, bot.FreeSummonCharges - 1);
        BroadcastSwarmExploreConsumed(spotId, Config.SWARM_EXPLORE_REGEN_SECONDS, sessions);
        // 봇도 자동 머지 — 사람과 같은 성장 규칙 (#217 자동 머지)
        _inGameInventoryManager.AutoMergeSurvivorOrbs(matchingId, bot.PlayerId, Random.Shared);
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
            matchingId, bot.PlayerId, spotId, attempt.ItemId, exploreCost);
    }

    /// <summary>
    ///     봇의 드래프트 색 선택 (#219 M2) — 사람과 같은 규칙 공간에서 고른다:
    ///     현재 시간 등급 티어가 같은 색 2개면 그 색(이번 픽이 3머지), 아니면 최다 보유 색
    ///     (전문화), 빈손이면 랜덤. 반환은 색 T1 베이스 ID — 티어는 호출부가 입힌다.
    /// </summary>
    private int ChooseBotDraftOrbItemId(long matchingId, long botPlayerId, int draftTier)
    {
        var countsByColor = new Dictionary<SurvivorOrbColor, (int Total, int AtDraftTier)>();
        foreach (var item in _inGameInventoryManager.GetPlayerInventory(matchingId, botPlayerId).GetAllItems())
        {
            if (item.Count <= 0 ||
                !SurvivorOrbData.TryGetColorAndTier(item.ItemId, out var color, out int tier))
                continue;

            var entry = countsByColor.TryGetValue(color, out var current) ? current : (0, 0);
            countsByColor[color] = (entry.Item1 + item.Count, entry.Item2 + (tier == draftTier ? item.Count : 0));
        }

        foreach (var (color, entry) in countsByColor)
            if (entry.AtDraftTier >= 2 && TryGetDraftTier1ItemId(color, out int mergeItemId))
                return mergeItemId;

        var best = countsByColor.OrderByDescending(pair => pair.Value.Total).FirstOrDefault();
        if (best.Value.Total > 0 && TryGetDraftTier1ItemId(best.Key, out int specializeItemId))
            return specializeItemId;

        return SwarmStartingOrbPool[Random.Shared.Next(SwarmStartingOrbPool.Length)];
    }

    /// <summary>상자 시간 등급 (#222 M3): 개전 앵커(게이트, 봇 전용은 스웜 첫 틱) 경과로 티어 결정.</summary>
    private int GetSwarmDraftTier(long matchingId)
    {
        var startedAtUtc = MatchStartGate.GetGameplayStartedAtUtc(matchingId);
        if (startedAtUtc == null &&
            _swarmMatchFallbackAnchorUtc.TryGetValue(matchingId, out var fallbackAnchor))
            startedAtUtc = fallbackAnchor;
        if (startedAtUtc == null)
            return 1;

        return SurvivorOrbData.GetDraftTierByElapsed((DateTime.UtcNow - startedAtUtc.Value).TotalSeconds);
    }

    private static bool TryGetDraftTier1ItemId(SurvivorOrbColor color, out int itemId)
    {
        itemId = color switch
        {
            SurvivorOrbColor.Red => 107000010,
            SurvivorOrbColor.Green => 107000020,
            SurvivorOrbColor.Blue => 107000030,
            _ => 0
        };
        return itemId != 0;
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
                onCooldown.Contains(info.Id) ||
                // 경계 밖 구역 스팟은 후보에서 제외 — 최근접이 밖이라고 순례 전체가 멈추면 안 된다.
                IsSwarmAreaOutside(matchingId, (AreaType)info.ZoneId))
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

    private static void BroadcastSwarmExploreConsumed(
        int interactId, int cooldownSeconds, List<GameClientSession> sessions)
    {
        var body = MessagePack.MessagePackSerializer.Serialize(new G_TO_C_RNG_COLLECT_COOLDOWN_BROADCAST
        {
            InteractId = interactId,
            CooldownSeconds = cooldownSeconds
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

        // 0) 경계 밖 탈출 최우선 — 자기장에서는 안쪽으로 걷는 것 자체가 대피 경로다.
        //    경계 안 사냥터 중 가장 가까운 곳으로, 전부 밖이면 종착지 운동장으로 향한다.
        if (IsSwarmAreaOutside(matchingId, bot.CurrentArea))
        {
            AreaType evacuationArea = SwarmHuntingAreas
                .Where(area => !IsSwarmAreaOutside(matchingId, area))
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
            Cell evacuationCell = GameMapData.GetAreaSpawnCell(MapId.School, evacuationArea);
            return new SpotArenaBotDirective(
                SpotArenaBotMode.Escort,
                evacuationArea,
                evacuationCell,
                BotPlayerManager.CellToWorldPosition(MapId.School, evacuationCell));
        }

        // 0.5) 상대 전력 비교 (#222): 티어 가중 전력(1/1.75/4)으로 비교한다.
        //      "싸움을 건다 = 유리하다" — 확실히 우세(×1.25 이상)일 때만 추격하고,
        //      동수 포함 그 이하는 회피한다. 동수 대치(뭉쳐서 수동 오브 소모전)가 성립하지
        //      않게 하는 규칙. 임계 사이 구간(1.0~1.25)은 중립 밴드 = 판단 떨림 방지.
        //      빈손은 화력이 0이라 몹도 강자로 취급해 피한다.
        float squadPower = GetSwarmSquadPower(matchingId, botPlayerId);
        bool hasSquadOrbs = squadPower > 0f;
        FindNearbySwarmRivals(matchingId, bot, squadPower, includeMonstersAsStronger: !hasSquadOrbs,
            out Vector3f strongerPosition, out (Vector3f Position, AreaType Area)? weakerRival);

        // 피격 반응 (#222, 매치 2379 -131 · 2386 -182): 맞는 동안은 절대 서 있지 않는다.
        // 열세·비등이면 그 방향에서 이탈(위협 승격), 우세면 싸우되 좌우 와리가리(스트레이프) —
        // 이동 중 공격이 허용되므로 화력 손실 없이 피격 정지 현상만 사라진다.
        bool recentlyDamaged =
            _swarmBotLastDamagedAtUtc.TryGetValue((matchingId, botPlayerId), out var lastDamagedAtUtc) &&
            (DateTime.UtcNow - lastDamagedAtUtc).TotalSeconds <= SwarmBotDamagedFleeSeconds;
        Vector3f recentAttackerPosition = null;
        if (recentlyDamaged && bot.LastProximityAttackerPlayerId != 0)
            TryGetSwarmParticipantPosition(
                matchingId, bot.LastProximityAttackerPlayerId, out recentAttackerPosition);
        if (strongerPosition == null && recentAttackerPosition != null)
        {
            float attackerPower = GetSwarmSquadPower(matchingId, bot.LastProximityAttackerPlayerId);
            if (squadPower < attackerPower * SwarmBotChasePowerAdvantage)
            {
                strongerPosition = recentAttackerPosition;
            }
            else
            {
                var strafeCell = ComputeSwarmStrafeCell(bot, recentAttackerPosition);
                if (strafeCell != null &&
                    GameMapData.GetCurrentArea(MapId.School, strafeCell) is var strafeArea &&
                    strafeArea != AreaType.None)
                {
                    return new SpotArenaBotDirective(
                        SpotArenaBotMode.Escort,
                        strafeArea,
                        strafeCell,
                        BotPlayerManager.CellToWorldPosition(MapId.School, strafeCell));
                }
            }
        }
        if (strongerPosition != null)
        {
            // 위협 앞에서는 채집 채널 홀드도 끊고 뛴다 — 홀드 채로 맞다 죽는 사고 방지 (매치 2372 봇 -78).
            bot.CancelInteractionHold();
            float fleeDx = bot.Position.X - strongerPosition.X;
            float fleeDy = bot.Position.Y - strongerPosition.Y;
            float fleeLength = MathF.Sqrt(fleeDx * fleeDx + fleeDy * fleeDy);
            if (fleeLength < 0.001f)
            {
                fleeDx = 1f;
                fleeDy = 0f;
                fleeLength = 1f;
            }

            // 도주 방향으로 앞선 가상 지점에서 최근접 스팟을 찾으면 "위협 반대편 스팟"이 된다.
            var fleeProbe = new Vector3f(
                bot.Position.X + fleeDx / fleeLength * SwarmBotFleeProbeDistance,
                bot.Position.Y + fleeDy / fleeLength * SwarmBotFleeProbeDistance,
                0f);
            if (TryFindNearestAvailableExploreSpot(
                    matchingId, area: null, fleeProbe, out var fleeSpot, out _))
            {
                var fleeArea = (AreaType)fleeSpot.ZoneId;
                Cell fleeCell = new(fleeSpot.CellX, fleeSpot.CellY);
                if (!GameMapData.IsMoveablePosition(MapId.School, fleeCell))
                {
                    fleeCell = fleeCell.GetAdjacentCells().FirstOrDefault(cell =>
                        GameMapData.IsMoveablePosition(MapId.School, cell) &&
                        GameMapData.GetCurrentArea(MapId.School, cell) == fleeArea) ?? fleeCell;
                }

                return new SpotArenaBotDirective(
                    SpotArenaBotMode.Escort,
                    fleeArea,
                    fleeCell,
                    BotPlayerManager.CellToWorldPosition(MapId.School, fleeCell));
            }

            // 폴백 (#222): 도주 방향에 열린 스팟이 없어도 무조건 이탈한다 — 스팟 부재로
            // 지시 없이 낙하해 제자리에서 얻어맞던 구멍(매치 2372 봇 -78) 수리.
            Cell fleeFallbackCell =
                ProximityCombatLineOfSight.WorldPositionToCell(MapId.School, fleeProbe);
            if (!GameMapData.IsMoveablePosition(MapId.School, fleeFallbackCell))
            {
                fleeFallbackCell = fleeFallbackCell.GetAdjacentCells()
                    .FirstOrDefault(cell => GameMapData.IsMoveablePosition(MapId.School, cell));
            }

            if (fleeFallbackCell != null &&
                GameMapData.GetCurrentArea(MapId.School, fleeFallbackCell) is var fleeFallbackArea &&
                fleeFallbackArea != AreaType.None)
            {
                return new SpotArenaBotDirective(
                    SpotArenaBotMode.Escort,
                    fleeFallbackArea,
                    fleeFallbackCell,
                    BotPlayerManager.CellToWorldPosition(MapId.School, fleeFallbackCell));
            }

            // 벽 방향이면 위협 반대편에서 가장 가까운 열린 사냥 구역 스폰으로 물러난다.
            AreaType fleeRetreatArea = SwarmHuntingAreas
                .Where(area => !IsSwarmAreaOutside(matchingId, area))
                .OrderBy(area =>
                {
                    var center = BotPlayerManager.CellToWorldPosition(
                        MapId.School, GameMapData.GetAreaSpawnCell(MapId.School, area));
                    float dx = center.X - fleeProbe.X;
                    float dy = center.Y - fleeProbe.Y;
                    return dx * dx + dy * dy;
                })
                .DefaultIfEmpty(AreaType.Ground)
                .First();
            Cell fleeRetreatCell = GameMapData.GetAreaSpawnCell(MapId.School, fleeRetreatArea);
            return new SpotArenaBotDirective(
                SpotArenaBotMode.Escort,
                fleeRetreatArea,
                fleeRetreatCell,
                BotPlayerManager.CellToWorldPosition(MapId.School, fleeRetreatCell));
        }

        if (hasSquadOrbs && weakerRival.HasValue)
        {
            // 약자 추격: 접근하면 자동전투(오브 우선 타겟)가 나머지를 한다.
            return new SpotArenaBotDirective(
                SpotArenaBotMode.Escort,
                weakerRival.Value.Area,
                ProximityCombatLineOfSight.WorldPositionToCell(MapId.School, weakerRival.Value.Position),
                weakerRival.Value.Position);
        }

        // 0.7) 잼 회수 (#222): 승점이 바닥에 보이면 줍는 게 항상 이득 — 같은 구역 최근접 잼으로.
        //      반응 지연(2.5초)을 지난 잼만 노린다: 사람 선점권 유지.
        if (TryFindNearestSwarmJamItem(matchingId, bot, out Vector3f jamPosition))
        {
            Cell jamCell = ProximityCombatLineOfSight.WorldPositionToCell(MapId.School, jamPosition);
            if (GameMapData.IsMoveablePosition(MapId.School, jamCell))
            {
                return new SpotArenaBotDirective(
                    SpotArenaBotMode.Escort,
                    bot.CurrentArea,
                    jamCell,
                    BotPlayerManager.CellToWorldPosition(MapId.School, jamCell));
            }
        }

        // 1) 지갑이 차면 줍기보다 개봉이 먼저 — 열린 구역 중 가장 가까운 스팟으로 순례한다.
        //    줍기가 이 단계를 선점하면 봇이 수십 석을 들고도 개봉을 영영 미룬다 (매치 2221 계측).
        //    폐쇄 필터는 스팟 탐색 안에서 처리한다 — 최근접이 폐쇄라고 순례가 멈추면 안 된다.
        if (TryFindNearestAvailableExploreSpot(
                matchingId, area: null, bot.Position, out var spot, out _) &&
            _summonStoneManager.GetSnapshot(matchingId, botPlayerId).StoneCount >=
            // 비용은 봇 자신의 궤도 크기 기준 — spot.Id를 넘기던 오배선(빈 인벤=0비용) 수리
            GetSwarmBotExploreCost(matchingId, botPlayerId))
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

        // 2) 같은 구역 바닥 소환석 — 걸어가면 자동 픽업 반경이 줍는다.
        //    반응 지연 (#222): 갓 떨어진 돌은 무시 — 사람이 먼저 주울 시간을 준다.
        var groundStone = _groundItemManager.GetSnapshot(matchingId, bot.CurrentArea)
            .Where(item => item.ItemId == Config.SUMMON_STONE_GROUND_ITEM_ID &&
                           !_groundItemManager.IsYoungerThan(
                               matchingId, item.GroundItemUid,
                               BotPlayerManager.SummonStoneBotReactionDelay))
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

        // 3) 사냥 정지: 도주·개봉·줍기 용무가 없고 사거리 안에 몹이 있으면 제자리에 선다.
        //    정지 공격 규칙에서 서야 쏘고, 캠프 모드에선 잠든 캠프 옆이 안전 사격 지점이다.
        //    빈손은 제외 — 화력 없이 몹 옆에 서는 건 자살이다 (#222).
        if (hasSquadOrbs && HasSwarmMonsterInBasicRange(matchingId, bot))
        {
            return new SpotArenaBotDirective(
                SpotArenaBotMode.Escort,
                bot.CurrentArea,
                ProximityCombatLineOfSight.WorldPositionToCell(MapId.School, bot.Position),
                bot.Position);
        }

        // 3.5) 소환석 기근 (#219): 다음 개봉 비용이 부족하면 캠프로 사냥을 나간다.
        //      봇 전지 퇴역 — 봇은 캠프 '위치'만 알고(지도 지식) 생사는 모른다. 같은 구역에
        //      들어와 눈으로 확인한 빈 캠프는 리스폰 주기만큼 제외하고 다음 캠프로 순회한다.
        //      빈손 봇은 개봉이 무료라 1)에서 이미 스팟 순례로 빠진다.
        if (_summonStoneManager.GetSnapshot(matchingId, botPlayerId).StoneCount <
            GetSwarmBotExploreCost(matchingId, botPlayerId) &&
            hasSquadOrbs &&
            TryChooseSwarmBotCampTarget(matchingId, bot, out var campArea, out var campPosition))
        {
            return new SpotArenaBotDirective(
                SpotArenaBotMode.Escort,
                campArea,
                ProximityCombatLineOfSight.WorldPositionToCell(MapId.School, campPosition),
                campPosition);
        }

        // 4) 시작방·복도는 공급이 마른다 — 무한 스폰 사냥터로 이주해 소환석을 번다.
        if (bot.CurrentArea == AreaType.Corridor ||
            SurvivorRoyaleSpawnData.GetPhaseRoomCandidates().Contains(bot.CurrentArea))
        {
            // 경계 밖 사냥터는 제외 — 전부 밖이면 종착지 운동장으로 (운동장은 항상 안이다).
            AreaType huntingArea = SwarmHuntingAreas
                .Where(area => !IsSwarmAreaOutside(matchingId, area))
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

    // 라이벌 스캔 반경: 이 안의 참가자와 전력을 비교해 회피/추격을 정한다.
    // 최대 공격 사거리(기본 7 + 파도 가산 3)보다 넓어야 한다 — 6이던 시절, 파도 빌드가
    // 봇의 인지 밖(6~10)에서 일방적으로 쏘는 사각이 있었다 (매치 2376 봇 -108).
    private const float SwarmBotRivalScanRadius = 11f;

    // 도주 방향 앞의 가상 지점 — 이 지점 기준 최근접 스팟이 "위협 반대편 재기 스팟"이 된다.
    private const float SwarmBotFleeProbeDistance = 8f;

    /// <summary>이 봇의 스쿼드 오브 총 개수 — 저성장(파밍 부족) 판정용.</summary>
    private int CountSwarmSquadOrbs(long matchingId, long playerId)
    {
        return _inGameInventoryManager.GetPlayerInventory(matchingId, playerId)
            .GetAllItems()
            .Where(item => item.Count > 0 && GetSquadOrbTier(item.ItemId) > 0)
            .Sum(item => item.Count);
    }

    /// <summary>
    ///     티어 가중 전력(1/1.75/4 합) — 개수 비교의 왜곡(T3 1개 = T1 1개 취급) 방지.
    ///     상자 시간 등급 도입 후 회피/추격 판단의 단일 기준.
    /// </summary>
    private float GetSwarmSquadPower(long matchingId, long playerId)
    {
        float power = 0f;
        foreach (var item in _inGameInventoryManager.GetPlayerInventory(matchingId, playerId).GetAllItems())
        {
            if (item.Count <= 0) continue;
            int tier = GetSquadOrbTier(item.ItemId);
            if (tier <= 0) continue;
            power += SurvivorOrbData.GetSwarmStatTierWeight(tier) * item.Count;
        }

        return power;
    }

    // 추격 우위 임계: 내 전력이 상대의 이 배수 이상일 때만 붙는다. 그 이하(동수 포함)는 회피.
    private const float SwarmBotChasePowerAdvantage = 1.25f;

    // 피격 반응 창: 이 시간 안에 맞았으면 중립 밴드 상대도 위협으로 승격한다.
    // 3초는 공격 간헐(조준·쿨다운·재접근)에 못 미쳐 와리가리↔정지가 번갈아 보였다 — 6초로.
    private const double SwarmBotDamagedFleeSeconds = 6d;

    // 피격 중 와리가리: 공격자 방향의 수직으로 이만큼 이동, 1초마다 좌우 반전.
    private const float SwarmBotStrafeDistance = 2.5f;

    /// <summary>피격 중 스트레이프 목적지 — 공격자 수직 방향, 매초 좌우 반전. 벽이면 반대편.</summary>
    private static Cell ComputeSwarmStrafeCell(BotPlayerState bot, Vector3f attackerPosition)
    {
        float dx = attackerPosition.X - bot.Position.X;
        float dy = attackerPosition.Y - bot.Position.Y;
        float length = MathF.Sqrt(dx * dx + dy * dy);
        if (length < 0.001f)
        {
            dx = 1f;
            dy = 0f;
            length = 1f;
        }

        float perpX = -dy / length;
        float perpY = dx / length;
        float side = DateTime.UtcNow.Second % 2 == 0 ? 1f : -1f;
        for (int attempt = 0; attempt < 2; attempt++)
        {
            var candidate = new Vector3f(
                bot.Position.X + perpX * side * SwarmBotStrafeDistance,
                bot.Position.Y + perpY * side * SwarmBotStrafeDistance,
                0f);
            var cell = ProximityCombatLineOfSight.WorldPositionToCell(MapId.School, candidate);
            if (GameMapData.IsMoveablePosition(MapId.School, cell))
                return cell;
            side = -side;
        }

        return null;
    }

    // 잼 회수 탐색 반경 — 같은 구역에서만.
    private const float SwarmBotJamSeekRadius = 16f;

    /// <summary>같은 구역의 반응 지연 지난 최근접 잼 — 봇 잼 회수 지시의 목적지.</summary>
    private bool TryFindNearestSwarmJamItem(long matchingId, BotPlayerState bot, out Vector3f position)
    {
        position = null;
        float bestDistanceSquared = SwarmBotJamSeekRadius * SwarmBotJamSeekRadius;
        foreach (var item in _groundItemManager.GetSnapshot(matchingId, bot.CurrentArea))
        {
            if (item.ItemId != Config.JAM_GROUND_ITEM_ID)
                continue;
            if (_groundItemManager.IsYoungerThan(
                    matchingId, item.GroundItemUid, BotPlayerManager.SummonStoneBotReactionDelay))
                continue;

            float dx = item.PositionX - bot.Position.X;
            float dy = item.PositionY - bot.Position.Y;
            float distanceSquared = dx * dx + dy * dy;
            if (distanceSquared >= bestDistanceSquared)
                continue;

            bestDistanceSquared = distanceSquared;
            position = new Vector3f(item.PositionX, item.PositionY, 0f);
        }

        return position != null;
    }

    /// <summary>참가자(사람·봇) 위치 조회 — 피격 반응의 도주 기준점.</summary>
    private bool TryGetSwarmParticipantPosition(long matchingId, long playerId, out Vector3f position)
    {
        position = null;
        foreach (var other in _botPlayerManager.GetBots(matchingId))
        {
            if (other.PlayerId != playerId || other.IsEliminated) continue;
            position = other.Position;
            return true;
        }

        foreach (var session in GetSessionsByInstance(MapId.School, matchingId))
        {
            if (session.PlayerId != playerId || session.IsEliminated ||
                session.LastValidatedPosition == null) continue;
            position = session.LastValidatedPosition;
            return true;
        }

        return false;
    }

    /// <summary>
    ///     반경 내 라이벌 탐색 — 티어 가중 전력 기준. 동수 이상인 최근접(강자)과 확실히 약한
    ///     (×1.25 우위) 최근접(약자)을 함께 찾는다. 빈손이면 살아있는 몹도 강자로 취급한다.
    /// </summary>
    private void FindNearbySwarmRivals(
        long matchingId,
        BotPlayerState bot,
        float myPower,
        bool includeMonstersAsStronger,
        out Vector3f strongerPosition,
        out (Vector3f Position, AreaType Area)? weakerRival)
    {
        float radiusSquared = SwarmBotRivalScanRadius * SwarmBotRivalScanRadius;
        float bestStrongerDistanceSquared = radiusSquared;
        float bestWeakerDistanceSquared = radiusSquared;
        Vector3f nearestStronger = null;
        (Vector3f Position, AreaType Area)? nearestWeaker = null;

        void Consider(long rivalPlayerId, Vector3f position, AreaType area)
        {
            float dx = position.X - bot.Position.X;
            float dy = position.Y - bot.Position.Y;
            float distanceSquared = dx * dx + dy * dy;
            if (distanceSquared >= radiusSquared) return;

            float rivalPower = GetSwarmSquadPower(matchingId, rivalPlayerId);
            // 동수는 강자 취급 — 서로가 서로를 피하며 대치가 해산된다.
            if (rivalPower >= myPower && distanceSquared < bestStrongerDistanceSquared)
            {
                bestStrongerDistanceSquared = distanceSquared;
                nearestStronger = position;
            }
            else if (myPower >= rivalPower * SwarmBotChasePowerAdvantage &&
                     distanceSquared < bestWeakerDistanceSquared &&
                     !IsSwarmAreaOutside(matchingId, area))
            {
                bestWeakerDistanceSquared = distanceSquared;
                nearestWeaker = (position, area);
            }
        }

        foreach (var other in _botPlayerManager.GetBots(matchingId))
        {
            if (other.PlayerId == bot.PlayerId || other.IsEliminated) continue;
            Consider(other.PlayerId, other.Position, other.CurrentArea);
        }

        foreach (var session in GetSessionsByInstance(MapId.School, matchingId))
        {
            if (!session.PlayerId.HasValue || session.IsEliminated ||
                session.LastValidatedPosition == null)
                continue;
            Consider(session.PlayerId.Value, session.LastValidatedPosition, session.CurrentArea);
        }

        if (includeMonstersAsStronger)
        {
            foreach (var monster in _swarmArenaManager.GetVisualStates(matchingId))
            {
                if (!monster.IsAlive) continue;
                float dx = monster.PositionX - bot.Position.X;
                float dy = monster.PositionY - bot.Position.Y;
                float distanceSquared = dx * dx + dy * dy;
                if (distanceSquared >= bestStrongerDistanceSquared) continue;
                bestStrongerDistanceSquared = distanceSquared;
                nearestStronger = new Vector3f(monster.PositionX, monster.PositionY, 0f);
            }
        }

        strongerPosition = nearestStronger;
        weakerRival = nearestWeaker;
    }

    // 빈 캠프 재방문 제외 시간 — 캠프 리스폰(45초)보다 짧게 잡아 순회가 한 바퀴 돌면 돌아온다.
    private const double SwarmBotEmptyCampSkipSeconds = 30d;

    // 캠프 혼잡 판정 반경과 초과 인원당 실효 거리 배율 (#222 봇 뭉침 해소).
    private const float SwarmBotCampCrowdRadius = 7f;
    private const float SwarmBotCampCrowdPenaltyPerBot = 1.5f;

    // 캠프 생사 판정 반경 — 리쉬(5.5) 안에 살아있는 몹이 없으면 그 캠프는 비어 있는 것이다.
    private const float SwarmBotCampAliveCheckRange = 5.5f;

    private readonly Dictionary<(long MatchingId, long PlayerId, AreaType Area, int CampIndex), DateTime>
        _swarmBotCampSkipUntilUtc = new();

    /// <summary>
    ///     봇의 캠프 순례 목적지 — 정적 앵커(지도 지식)에서 가까운 순으로 고른다. 같은 구역
    ///     캠프는 시야로 생사를 확인할 수 있고, 비어 있으면 스킵 표시 후 다음 후보로 넘어간다.
    ///     다른 구역 캠프는 생사를 모르니 일단 걸어간다 — 도착 후 다음 틱에 같은 규칙으로 판정된다.
    /// </summary>
    private bool TryChooseSwarmBotCampTarget(
        long matchingId, BotPlayerState bot, out AreaType campArea, out Vector3f campPosition)
    {
        DateTime nowUtc = DateTime.UtcNow;
        var visibleAliveMonsters = _swarmArenaManager.GetVisualStates(matchingId)
            .Where(monster => monster.IsAlive && monster.AreaType == bot.CurrentArea)
            .ToList();

        // 혼잡 페널티 (#222): 이미 다른 봇이 몰린 캠프는 실효 거리를 늘려 순위를 낮춘다.
        // 1명까지는 경쟁 허용(선점 다툼도 재미), 2명째부터 뭉침으로 보고 흩어지게 한다.
        var otherBotPositions = _botPlayerManager.GetBots(matchingId)
            .Where(other => other.PlayerId != bot.PlayerId && !other.IsEliminated)
            .Select(other => other.Position)
            .ToList();

        var anchors = GameMonsterCampData.GetAllAnchors()
            .Where(anchor => !IsSwarmAreaOutside(matchingId, anchor.Area))
            .Select(anchor => (anchor.Area, anchor.CampIndex,
                World: BotPlayerManager.CellToWorldPosition(MapId.School, anchor.Cell)))
            .OrderBy(anchor =>
            {
                float dx = anchor.World.X - bot.Position.X;
                float dy = anchor.World.Y - bot.Position.Y;
                int nearbyBots = otherBotPositions.Count(position =>
                {
                    float bx = position.X - anchor.World.X;
                    float by = position.Y - anchor.World.Y;
                    return bx * bx + by * by <=
                           SwarmBotCampCrowdRadius * SwarmBotCampCrowdRadius;
                });
                return (dx * dx + dy * dy) *
                       (1f + SwarmBotCampCrowdPenaltyPerBot * Math.Max(0, nearbyBots - 1));
            });

        foreach (var anchor in anchors)
        {
            var skipKey = (matchingId, bot.PlayerId, anchor.Area, anchor.CampIndex);
            if (_swarmBotCampSkipUntilUtc.TryGetValue(skipKey, out var skipUntil) && nowUtc < skipUntil)
                continue;

            if (anchor.Area == bot.CurrentArea)
            {
                bool campAlive = visibleAliveMonsters.Any(monster =>
                {
                    float dx = monster.PositionX - anchor.World.X;
                    float dy = monster.PositionY - anchor.World.Y;
                    return dx * dx + dy * dy <=
                           SwarmBotCampAliveCheckRange * SwarmBotCampAliveCheckRange;
                });
                if (!campAlive)
                {
                    _swarmBotCampSkipUntilUtc[skipKey] = nowUtc.AddSeconds(SwarmBotEmptyCampSkipSeconds);
                    continue;
                }
            }

            campArea = anchor.Area;
            campPosition = anchor.World;
            return true;
        }

        campArea = AreaType.None;
        campPosition = bot.Position;
        return false;
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

    /// <summary>봇 개봉 비용 — 사람과 같은 SB 크기 비례 비용(궤도 오브 슬롯 수 기준)을 쓴다.</summary>
    private int GetSwarmBotExploreCost(long matchingId, long botPlayerId) =>
        Config.GetSwarmExploreCost(
            _inGameInventoryManager.GetPlayerInventory(matchingId, botPlayerId).GetAllItems().Count);

    // #219 밸런스: 몹→참가자 피해 절반 (2026-08-08) — 시작 1오브 체제에서 몹 접촉이
    // 과열돼 "계속 죽는" 판정. 해골(1)은 최소 1 유지, 다트 2→1 · 탈주 5→3 · 볼러 2→1.
    private const float SwarmMonsterDamageTakenMultiplier = 0.5f;

    private void ApplySwarmParticipantDamage(
        long matchingId,
        SpotArenaPlayerDamage damage,
        List<GameClientSession> aliveSessions,
        List<BotPlayerState> aliveBots,
        List<GameClientSession> allSessions)
    {
        damage = damage with
        {
            Damage = Math.Max(1,
                (int)MathF.Round(damage.Damage * SwarmMonsterDamageTakenMultiplier))
        };
        var session = aliveSessions.FirstOrDefault(candidate =>
            candidate.PlayerId == damage.TargetPlayerId);
        if (session != null)
        {
            if (SwarmOrbHealthEnabled)
            {
                // 피해량은 몬스터 종이 결정한다 (해골 1 · 다트 2 · 탈주 5 · 볼러 2).
                ApplySwarmSquadOrbHit(matchingId, session, damage.MonsterId, damage.Damage, allSessions);
                return;
            }

            // 레거시 오염 경로 — 오염 증가·피격 피드백·일반 탈락 흐름까지 담당한다.
            session.ApplyEmotionAfterimageMonsterHit(damage.MonsterId, damage.Damage);
            return;
        }

        var bot = aliveBots.FirstOrDefault(candidate => candidate.PlayerId == damage.TargetPlayerId);
        if (bot == null)
            return;

        if (SwarmOrbHealthEnabled)
        {
            // 유닛 낱개 체력: 접촉은 오브 HP를 깎는다. 마지막 유닛을 잃으면 그 타격이
            // 곧 버스트 — 오염 만충으로 기존 탈락 파이프라인(순위·드롭)을 그대로 탄다.
            // 빈손 봇은 본체(오염)가 닳는다 — 사람과 같은 규칙.
            if (!HasAnySquadOrb(matchingId, bot.PlayerId))
            {
                bot.Corruption = Math.Min(Config.SURVIVOR_MAX_CORRUPTION,
                    bot.Corruption + GetSwarmNakedCorruption(damage.Damage));
                _swarmBotLastDamagedAtUtc[(matchingId, bot.PlayerId)] = DateTime.UtcNow;
                bot.LastDamagedAtUtc = DateTime.UtcNow;
                bot.LastDamagedAtUtc = DateTime.UtcNow;
                bot.LastDamagedAtUtc = DateTime.UtcNow;
                return;
            }

            // 버스트 즉사 제거 (#219): 봇도 빈손 생존으로 전환 — 이후는 본체(오염) 피해 경로.
            var botHit = ApplySwarmOrbHpDamage(matchingId, bot.PlayerId, damage.Damage);
            if (botHit.DestroyedItem != null)
                ScatterSwarmOrbBreakStones(
                    matchingId, botHit.DestroyedItem.ItemId, bot.CurrentArea,
                    bot.Position.X, bot.Position.Y, allSessions);
            return;
        }

        int botDamage = Math.Max(1, (int)(damage.Damage * SwarmBotContactDamageMultiplier));
        bot.Corruption = Math.Min(Config.SURVIVOR_MAX_CORRUPTION, bot.Corruption + botDamage);
        _swarmBotLastDamagedAtUtc[(matchingId, bot.PlayerId)] = DateTime.UtcNow;
    }

    // 빈손 본체 유효 HP = T1 오브(24)와 동급 (2026-08-10, T3 안에서 재하향): ×30(유효 14,
    // PvP 5초 즉사)과 1:1(유효 420, 불사) 사이 — 만충 420 ÷ T1 HP가 피해당 오염 배율이다.
    // 빈손은 "오브 하나 값"의 유예만 갖고, 생존은 도주 지시(0.5단계)와 무료 개봉이 만든다.
    private static readonly float SwarmNakedCorruptionPerDamage =
        Config.SURVIVOR_MAX_CORRUPTION / (float)SurvivorOrbData.GetSquadOrbMaxHp(1);

    private static int GetSwarmNakedCorruption(int damage) =>
        Math.Max(1, (int)MathF.Round(damage * SwarmNakedCorruptionPerDamage));

    /// <summary>
    ///     사람 피격 (유닛 낱개 체력): 오브 HP 차감 → 0이면 파괴 + 인벤 동기화, 궤도가 비면 버스트.
    ///     빈손이면 플레이어 본체(오염 게이지)가 닳고, 만충이면 기존 탈락 파이프라인을 탄다.
    ///     피격 연출·탈락은 기존 잔상 피격 경로를 재사용한다 (오염은 연출용 1, 표시는 실제 피해량).
    /// </summary>
    private void ApplySwarmSquadOrbHit(
        long matchingId, GameClientSession session, int monsterId, int damage,
        List<GameClientSession> allSessions)
    {
        if (!session.PlayerId.HasValue)
            return;

        if (!HasAnySquadOrb(matchingId, session.PlayerId.Value))
        {
            // PvP(monsterId=0)는 몬스터 피격 경로의 monsterId 가드에 걸려 증발했다 (#222 수리)
            // — 오염만 직접 반영한다. 피격 연출은 PvP VFX 브로드캐스트가 이미 담당한다.
            if (monsterId > 0)
                session.ApplyEmotionAfterimageMonsterHit(monsterId, GetSwarmNakedCorruption(damage));
            else
                session.ModifyStats(corruptionDelta: GetSwarmNakedCorruption(damage));
            return;
        }

        var hit = ApplySwarmOrbHpDamage(matchingId, session.PlayerId.Value, damage);
        if (hit.DestroyedItem != null)
        {
            session.SendInGameInventoryUpdate(hit.DestroyedItem);
            ScatterSwarmOrbBreakStones(
                matchingId, hit.DestroyedItem.ItemId, session.CurrentArea,
                session.LastValidatedPosition.X, session.LastValidatedPosition.Y, allSessions);
        }

        // 버스트(마지막 유닛 파괴)는 즉사가 아니다 (#219 SB 이탈, 2026-08-08) —
        // 빈손 생존으로 전환되고 이후 생존은 본체 HP(오염)가 결정한다. 재기 = 무료 개봉.
        session.ApplyEmotionAfterimageMonsterHit(monsterId, 1, damage);
    }

    /// <summary>
    ///     오브 HP 피해 공통 처리 (사람·봇). 최저 티어 오브의 HP를 깎고, 0이 되면 그 오브를 파괴한다.
    ///     앞줄 오브가 바뀌면(머지·획득) HP는 새 오브 만충으로 리셋된다 — 잔여 HP 이월 없음.
    /// </summary>
    /// <summary>앞줄 오브 = 최저 티어·선입(ItemUid) — 피해·표시가 같은 기준을 읽는다.</summary>
    private InGameItemInfo FindSwarmFrontOrb(long matchingId, long playerId)
    {
        return _inGameInventoryManager.GetPlayerInventory(matchingId, playerId)
            .GetAllItems()
            .Where(item => item.Count > 0 && GetSquadOrbTier(item.ItemId) > 0)
            .OrderBy(item => GetSquadOrbTier(item.ItemId))
            .ThenBy(item => item.ItemUid)
            .FirstOrDefault();
    }

    /// <summary>잼 보유량 조회 (#222 M3) — 사람은 세션, 봇은 봇 상태에서. 머리 위 공개 표시용.</summary>
    private int GetSwarmJamCount(
        long matchingId, long playerId, IReadOnlyCollection<GameClientSession> matchingSessions)
    {
        foreach (var session in matchingSessions)
        {
            if (session.PlayerId == playerId)
                return session.JamCount;
        }

        foreach (var bot in _botPlayerManager.GetBots(matchingId))
        {
            if (bot.PlayerId == playerId)
                return bot.JamCount;
        }

        return 0;
    }

    /// <summary>
    ///     잼 헌트 만료 판정 (#222 M3-2): 개전(카운트다운 종료) 후 4분이 지나면 생존자 중
    ///     잼 최다 보유자가 승리한다. 동률은 오염 낮은 쪽 → PlayerId 낮은 쪽.
    ///     클라 타이머·운동장 최종 폐쇄와 같은 시점(SWARM_MATCH_DURATION_SECONDS)에 정렬.
    /// </summary>
    private void ProcessSwarmJamHuntTimeout(
        long matchingId,
        DateTime nowUtc,
        List<GameClientSession> sessions,
        List<GameClientSession> aliveSessions,
        List<BotPlayerState> aliveBots)
    {
        if (DevFlags.DisableGameEnd || _swarmTimeoutEndedMatchings.Contains(matchingId))
            return;

        var startedAtUtc = MatchStartGate.GetGameplayStartedAtUtc(matchingId);
        if (startedAtUtc == null)
        {
            // 봇 전용 매치(어드민 검증)는 게이트가 없다 — 스웜 첫 틱을 앵커로 대신 쓴다.
            if (!_swarmMatchFallbackAnchorUtc.TryGetValue(matchingId, out var fallbackAnchor))
            {
                _swarmMatchFallbackAnchorUtc[matchingId] = nowUtc;
                return;
            }
            startedAtUtc = fallbackAnchor;
        }

        if ((nowUtc - startedAtUtc.Value).TotalSeconds < Config.SWARM_MATCH_DURATION_SECONDS)
            return;

        var candidates = aliveSessions
            .Where(session => session.PlayerId.HasValue)
            .Select(session => (
                PlayerId: session.PlayerId!.Value,
                Jam: session.JamCount,
                Corruption: session.CurrentCorruption))
            .Concat(aliveBots.Select(bot => (bot.PlayerId, Jam: bot.JamCount, bot.Corruption)))
            .OrderByDescending(candidate => candidate.Jam)
            .ThenBy(candidate => candidate.Corruption)
            .ThenBy(candidate => candidate.PlayerId)
            .ToList();
        long winnerId = candidates.Count > 0 ? candidates[0].PlayerId : 0;
        _swarmTimeoutEndedMatchings.Add(matchingId);
        logger.LogInformation(
            "Swarm jam hunt timeout: MatchingId={MatchingId}, WinnerId={WinnerId}, WinnerJam={WinnerJam}, Alive={AliveCount}",
            matchingId, winnerId, candidates.Count > 0 ? candidates[0].Jam : 0, candidates.Count);

        var resultHost = sessions.FirstOrDefault(session => !session.IsGameEnded);
        if (resultHost != null)
        {
            resultHost.TryEndSurvivorMatch(winnerId, "jam_hunt_timeout");
            CleanupSurvivorSettlementState(matchingId);
            return;
        }

        EndBotOnlyMatchIfSettled(matchingId, winnerId);
    }

    /// <summary>
    ///     젬 광산 살포 (#222 M3): 개막 버스트 후 주기적으로 잼을 광산 지점에 흩뿌린다.
    ///     매치 만료(4:00)는 잼 헌트 타임아웃이 맡으므로 여기서는 살포만 반복한다.
    /// </summary>
    private void ProcessSwarmGemMine(long matchingId, DateTime nowUtc, List<GameClientSession> sessions)
    {
        var startedAtUtc = MatchStartGate.GetGameplayStartedAtUtc(matchingId);
        if (startedAtUtc == null &&
            _swarmMatchFallbackAnchorUtc.TryGetValue(matchingId, out var fallbackAnchor))
            startedAtUtc = fallbackAnchor;
        if (startedAtUtc == null ||
            (nowUtc - startedAtUtc.Value).TotalSeconds < GemMineOpenSeconds)
            return;

        if (_swarmGemMineOpenedMatchings.Add(matchingId))
        {
            _swarmGemMineNextScatterUtc[matchingId] = nowUtc.AddSeconds(GemMineScatterIntervalSeconds);
            ScatterGemMineJam(matchingId, GemMineOpenBurstJam, sessions);
            logger.LogInformation(
                "Gem mine opened: MatchingId={MatchingId}, Position=({X},{Y})",
                matchingId, GemMineX, GemMineY);
            return;
        }

        // 폭발 피날레 (SB 0:15 문법): 대량 낙수 한 방으로 종반 쟁탈전을 만들고 살포를 끝낸다.
        if (_swarmGemMineExplodedMatchings.Contains(matchingId))
            return;
        if ((nowUtc - startedAtUtc.Value).TotalSeconds >= GemMineExplodeSeconds)
        {
            _swarmGemMineExplodedMatchings.Add(matchingId);
            ScatterGemMineJam(matchingId, GemMineExplosionJam, sessions);
            logger.LogInformation(
                "Gem mine exploded: MatchingId={MatchingId}, Jam={Jam}", matchingId, GemMineExplosionJam);
            return;
        }

        if (!_swarmGemMineNextScatterUtc.TryGetValue(matchingId, out var nextScatterUtc) ||
            nowUtc < nextScatterUtc)
            return;

        _swarmGemMineNextScatterUtc[matchingId] = nowUtc.AddSeconds(GemMineScatterIntervalSeconds);
        ScatterGemMineJam(matchingId, GemMineScatterJam, sessions);
    }

    private void ScatterGemMineJam(long matchingId, int jamCount, List<GameClientSession> sessions)
    {
        if (jamCount <= 0)
            return;

        var jamIds = Enumerable.Repeat(Config.JAM_GROUND_ITEM_ID, jamCount).ToList();
        // 사망 낙수와 같은 원형 흩뿌림 — 한 점에 뭉치면 살포가 안 읽힌다 (#222 피드백).
        var spawned = _groundItemManager.SpawnItems(
            matchingId, AreaType.Ground, GemMineX, GemMineY, jamIds,
            mapId: MapId.School,
            layout: GroundItemSpawnLayout.EliminationScatter);
        if (spawned.Count == 0)
            return;

        // 폭발 낙수(20개)는 한 패킷 버퍼(2048)를 넘는다 — 청크로 나눠 보낸다.
        int remaining = _areaItemStockManager.GetRemainingCount(matchingId, (int)AreaType.Ground);
        const int chunkSize = 8;
        for (int offset = 0; offset < spawned.Count; offset += chunkSize)
        {
            var chunk = spawned.Skip(offset).Take(chunkSize).ToList();
            using var packet = PacketMaker.G_TO_C_GROUND_ITEM_SPAWN((int)AreaType.Ground, remaining, chunk);
            foreach (var session in sessions)
                if (session.CurrentArea == AreaType.Ground)
                    session.Send(packet);
        }
    }

    /// <summary>
    ///     잼 리더보드 브로드캐스트 (#222 M3) — 전 참가자(탈락 포함) 잼 내림차순.
    ///     구역 게이트 없이 매치 전 세션에 보내며, 시그니처가 같으면 재전송하지 않는다.
    /// </summary>
    private void BroadcastSwarmJamRankings(
        long matchingId, List<GameClientSession> sessions, List<BotPlayerState> bots)
    {
        var entries = sessions
            .Where(session => session.PlayerId.HasValue)
            .Select(session => (PlayerId: session.PlayerId!.Value, Jam: session.JamCount))
            .Concat(bots.Select(bot => (bot.PlayerId, Jam: bot.JamCount)))
            .OrderByDescending(entry => entry.Jam)
            .ThenBy(entry => entry.PlayerId)
            .ToList();
        if (entries.Count == 0 || sessions.Count == 0)
            return;

        string signature = string.Join("|", entries.Select(entry => $"{entry.PlayerId}:{entry.Jam}"));
        bool isFirstBroadcast = !_swarmJamRankingsSignature.TryGetValue(matchingId, out var previous);
        if (!isFirstBroadcast && previous == signature)
            return;

        _swarmJamRankingsSignature[matchingId] = signature;
        if (isFirstBroadcast)
            logger.LogInformation(
                "Jam rankings broadcast armed: MatchingId={MatchingId}, Participants={Count}, Sessions={Sessions}",
                matchingId, entries.Count, sessions.Count);
        var message = new G_TO_C_JAM_RANKINGS
        {
            PlayerIds = entries.Select(entry => entry.PlayerId).ToList(),
            JamCounts = entries.Select(entry => entry.Jam).ToList()
        };
        using var packet = Packet.Create((int)Protocol.G_TO_C_JAM_RANKINGS);
        packet.SetBody(MessagePackSerializer.Serialize(message));
        foreach (var session in sessions)
            session.Send(packet);
    }

    /// <summary>앞줄 오브의 현재 HP — 오브별 체력바 브로드캐스트용. 빈손은 -1(만충 취급).</summary>
    private int GetSwarmFrontOrbHp(long matchingId, long playerId)
    {
        var frontOrb = FindSwarmFrontOrb(matchingId, playerId);
        if (frontOrb == null)
            return -1;

        return _swarmFrontOrbHp.TryGetValue((matchingId, playerId), out var stored) &&
               stored.ItemId == frontOrb.ItemId
            ? stored.Hp
            : GetSquadOrbMaxHp(GetSquadOrbTier(frontOrb.ItemId));
    }

    private (InGameItemInfo DestroyedItem, bool Busted) ApplySwarmOrbHpDamage(
        long matchingId, long playerId, int damage)
    {
        var inventory = _inGameInventoryManager.GetPlayerInventory(matchingId, playerId);
        var frontOrb = FindSwarmFrontOrb(matchingId, playerId);
        // 빈손(#219 M2 빈손 시작)은 스쿼드가 없으니 스쿼드 피해도 버스트도 없다 —
        // 버스트는 "마지막 유닛을 잃는 타격"에만 성립한다. 빈손 즉사 사고 방지.
        if (frontOrb == null)
            return (null, false);

        var key = (matchingId, playerId);
        int currentHp = _swarmFrontOrbHp.TryGetValue(key, out var stored) && stored.ItemId == frontOrb.ItemId
            ? stored.Hp
            : GetSquadOrbMaxHp(GetSquadOrbTier(frontOrb.ItemId));
        currentHp -= damage;
        if (currentHp > 0)
        {
            _swarmFrontOrbHp[key] = (frontOrb.ItemId, currentHp);
            return (null, false);
        }

        _swarmFrontOrbHp.Remove(key);
        inventory.TryRemoveItem(frontOrb.ItemUid, 1, out var destroyedItem);
        return (destroyedItem, !HasAnySquadOrb(matchingId, playerId));
    }

    /// <summary>
    ///     오브 파괴 낙수 (#219 SB): 깨진 오브는 소환석으로 흩어진다 — 승자의 전리품이자
    ///     도망친 주인의 회수 기회. 개봉 원가의 일부만 돌려 킬 스노볼을 제한한다.
    ///     #223 밸런싱: 2/5/10 절반으로 — 매치 2401에서 승자 98석 vs 2위 36석,
    ///     "킬 = 전력 대박"이 스노우볼 동력이었다. 승점 대박(잼 낙수·사망 잼 전량)은 유지.
    /// </summary>
    private static int GetSwarmOrbBreakStoneCount(int tier) => tier >= 3 ? 5 : tier == 2 ? 3 : 1;

    /// <summary>오브 파괴 잼 (#222 M3): 버스트 전리품이 곧 승점 — 티어 1/3/6.</summary>
    private static int GetSwarmOrbBreakJamCount(int tier) => tier >= 3 ? 6 : tier == 2 ? 3 : 1;

    private void ScatterSwarmOrbBreakStones(
        long matchingId,
        int destroyedItemId,
        AreaType area,
        float x,
        float y,
        List<GameClientSession> sessions)
    {
        int destroyedTier = GetSquadOrbTier(destroyedItemId);
        int stoneCount = GetSwarmOrbBreakStoneCount(destroyedTier);
        int jamCount = GetSwarmOrbBreakJamCount(destroyedTier);
        if (stoneCount <= 0 && jamCount <= 0 || area == AreaType.None)
            return;

        var itemIds = Enumerable.Repeat(Config.SUMMON_STONE_GROUND_ITEM_ID, stoneCount)
            .Concat(Enumerable.Repeat(Config.JAM_GROUND_ITEM_ID, jamCount))
            .ToArray();
        var spawned = _groundItemManager.SpawnItems(
            matchingId, area, x, y, itemIds,
            mapId: MapId.School,
            layout: GroundItemSpawnLayout.EliminationScatter);
        if (spawned.Count == 0)
            return;

        // T3 낙수(석 10 + 잼 6)는 한 패킷 버퍼(2048)를 넘는다 — 청크로 나눠 보낸다 (#222).
        int remaining = _areaItemStockManager.GetRemainingCount(matchingId, (int)area);
        const int chunkSize = 8;
        for (int offset = 0; offset < spawned.Count; offset += chunkSize)
        {
            var chunk = spawned.Skip(offset).Take(chunkSize).ToList();
            using var packet = PacketMaker.G_TO_C_GROUND_ITEM_SPAWN((int)area, remaining, chunk);
            foreach (var session in sessions)
            {
                if (session.PlayerId.HasValue && session.CurrentArea == area)
                    session.Send(packet);
            }
        }
    }

    private bool HasAnySquadOrb(long matchingId, long playerId)
    {
        return _inGameInventoryManager.GetPlayerInventory(matchingId, playerId)
            .GetAllItems()
            .Any(item => item.Count > 0 && GetSquadOrbTier(item.ItemId) > 0);
    }

    private static int GetSquadOrbTier(int itemId)
    {
        if (SurvivorOrbData.TryGetColorAndTier(itemId, out _, out int tier))
            return tier;
        return SurvivorOrbData.TryGetRecoveryTier(itemId, out int recoveryTier) ? recoveryTier : 0;
    }

    private void ApplySwarmPvpAttack(
        long matchingId,
        ProximityCombatAttack attack,
        List<GameClientSession> aliveSessions,
        List<BotPlayerState> aliveBots,
        List<GameClientSession> allSessions)
    {
        // PvP 전 발 적용 (#222 후반 루즈 수리): 캡 3 + 0.8초 무적창은 오브 상대 실효 DPS를
        // 3.75로 고정해, 티어 HP(24/56/120)가 커지는 후반엔 아무도 못 죽는 관전 대치를 만들었다.
        // 몬스터와 같은 규칙(발당 실데미지 전부 적용)으로 통일 — TTK가 공격 DPS vs 앞줄 HP의
        // 대칭이 되고, 빈손 오염(×17.5)도 같은 앵커를 자동으로 따른다.
        // 발당 배율(#223)은 그 대칭을 유지한 채 PvP TTK만 늘린다 — 캡·무적창 재도입 금지.
        int damage = Math.Max(1, (int)MathF.Round(attack.Damage * SwarmArenaManager.PvpDamageScale));
        if (SwarmOrbHealthEnabled)
        {
            logger.LogDebug(
                "Swarm PvP attack: MatchingId={MatchingId}, Attacker={Attacker}, Target={Target}, " +
                "Area={Area}, Weapon={Weapon}, Damage={Damage}",
                matchingId, attack.AttackerPlayerId, attack.TargetPlayerId,
                attack.Area, attack.WeaponItemId, damage);

            var pvpTargetSession = aliveSessions.FirstOrDefault(session =>
                session.PlayerId == attack.TargetPlayerId);
            if (pvpTargetSession != null)
            {
                ApplySwarmSquadOrbHit(matchingId, pvpTargetSession, monsterId: 0, damage, allSessions);
            }
            else
            {
                var pvpTargetBot = aliveBots.FirstOrDefault(candidate =>
                    candidate.PlayerId == attack.TargetPlayerId);
                if (pvpTargetBot == null)
                    return;
                pvpTargetBot.LastProximityAttackerPlayerId = attack.AttackerPlayerId;
                // 오브가 맞아도 "피격 중"이다 (#222, 매치 2379 -131) — 피격 반응 판단의 입력.
                _swarmBotLastDamagedAtUtc[(matchingId, pvpTargetBot.PlayerId)] = DateTime.UtcNow;
                pvpTargetBot.LastDamagedAtUtc = DateTime.UtcNow;
                if (!HasAnySquadOrb(matchingId, pvpTargetBot.PlayerId))
                {
                    pvpTargetBot.Corruption = Math.Min(Config.SURVIVOR_MAX_CORRUPTION,
                        pvpTargetBot.Corruption + GetSwarmNakedCorruption(damage));
                }
                else
                {
                    // 버스트 즉사 제거 (#219): 빈손 전환 후는 본체 피해 경로가 결정한다.
                    var pvpBotHit = ApplySwarmOrbHpDamage(matchingId, pvpTargetBot.PlayerId, damage);
                    if (pvpBotHit.DestroyedItem != null)
                        ScatterSwarmOrbBreakStones(
                            matchingId, pvpBotHit.DestroyedItem.ItemId, pvpTargetBot.CurrentArea,
                            pvpTargetBot.Position.X, pvpTargetBot.Position.Y, allSessions);
                }
            }

            allSessions.FirstOrDefault(session => session.PlayerId == attack.AttackerPlayerId)
                ?.SendProximityAutoCombatAttackFeedback(
                    attack.TargetPlayerId, attack.Area, attack.WeaponItemId, damage);
            BroadcastSpotArenaAttackVfxToTargetAndObservers(attack, allSessions);
            return;
        }

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
        foreach (var key in _swarmStartingOrbGrantedPlayers
                     .Where(key => key.MatchingId == matchingId).ToList())
            _swarmStartingOrbGrantedPlayers.Remove(key);
        foreach (var key in _swarmBotCampSkipUntilUtc.Keys
                     .Where(key => key.MatchingId == matchingId).ToList())
            _swarmBotCampSkipUntilUtc.Remove(key);
        _swarmArenaManager.RemoveMatching(matchingId);
        foreach (var key in _swarmMovementSamples.Keys.Where(key => key.MatchingId == matchingId).ToList())
            _swarmMovementSamples.Remove(key);
        foreach (var key in _swarmBotLastDamagedAtUtc.Keys.Where(key => key.MatchingId == matchingId).ToList())
            _swarmBotLastDamagedAtUtc.Remove(key);
        foreach (var key in _swarmBotNextRecoveryAtUtc.Keys.Where(key => key.MatchingId == matchingId).ToList())
            _swarmBotNextRecoveryAtUtc.Remove(key);
        _swarmFieldStartedAtUtc.TryRemove(matchingId, out _);
        _swarmFieldWarnedAreas.Remove(matchingId);
        _swarmFieldOutsideAreas.Remove(matchingId);
        foreach (var key in _swarmFrontOrbHp.Keys.Where(key => key.MatchingId == matchingId).ToList())
            _swarmFrontOrbHp.Remove(key);
        _pendingSwarmMonsterHits.RemoveAll(hit => hit.MatchingId == matchingId);
        _swarmJamRankingsSignature.Remove(matchingId);
        _swarmTimeoutEndedMatchings.Remove(matchingId);
        _swarmMatchFallbackAnchorUtc.Remove(matchingId);
        _swarmGemMineOpenedMatchings.Remove(matchingId);
        _swarmGemMineExplodedMatchings.Remove(matchingId);
        _swarmGemMineNextScatterUtc.Remove(matchingId);
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
                // SB 타겟 규칙 (#219): 적 오브(0) > 적 본체(1) > 몬스터(2)
                TargetPriority: 2));
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
        // SB 타겟 규칙 (#219): 사거리 안에 적 오브(스쿼드)가 있으면 그쪽이 먼저, 없을 때만
        // 빈손 본체를 노린다 — 피격 라우팅(오브 HP vs 본체 오염)과 대칭인 표적 우선순위.
        var fallback = CreateSwarmParticipantActor(spatial, armed) with
        {
            TargetPriority = HasAnySquadOrb(matchingId, spatial.PlayerId) ? 0 : 1
        };
        var inventory = _inGameInventoryManager.GetPlayerInventory(matchingId, spatial.PlayerId);
        if (!inventory.GetAllItems().Any(item => item.Count > 0))
        {
            // #219 M2 빈손 시작: 기본 공격 폴백 퇴역 — 빈손은 무기(가디언 오브 비주얼)도
            // 화력도 없고 피격 대상으로만 존재한다. 첫 화력은 드래프트에서 나온다.
            actors.Add(fallback with { WeaponItemId = 0, Damage = 0 });
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
        // #219 M2 색 스탯: 태양=공격력 배율, 파도=사거리 가산 (바람=이속은 클라 이동에서).
        var colorStats = SurvivorOrbData.GetSwarmColorStats(inventory.GetAllItems());
        int orbActorCount = Math.Max(1, actors.Count - before);
        for (int index = before; index < actors.Count; index++)
        {
            var actor = actors[index];
            float baseInterval = actor.AttackIntervalSeconds * SwarmOrbIntervalMultiplier;
            // 연사화 + 스팸 캡: 캡으로 주기가 달라져도 발당 데미지를 주기 비율로 맞춰
            // 오브별 DPS(원 데미지/원 주기)를 보존한다. 바람(공속)은 주기만 줄여 실DPS를
            // 올린다 — dpsScale에 공속 배율을 곱해 "바람 = DPS 상승"이 캡에도 유지되게.
            float interval = MathF.Max(
                baseInterval * SwarmOrbRapidFireScale / colorStats.AttackSpeedMultiplier,
                orbActorCount * SwarmOrbMinShotSpacingSeconds);
            float dpsScale = baseInterval > 0f
                ? interval * colorStats.AttackSpeedMultiplier / baseInterval
                : 1f;
            actors[index] = actor with
            {
                Damage = armed
                    ? (int)MathF.Round(
                        actor.Damage * SwarmOrbDamageMultiplier * colorStats.AttackMultiplier * dpsScale)
                    : 0,
                AttackIntervalSeconds = interval,
                // SB 스태거: 오브들이 간격을 균등 분할해 엇박으로 쏜다 — 일제사격 금지.
                // 총 DPS는 그대로, 발사 밀도가 오브 수에 비례해 촘촘해진다.
                InitialAttackDelaySeconds = actor.InitialAttackDelaySeconds +
                                            interval * ((index - before) / (float)orbActorCount),
                // 사거리는 CSV 티어값 대신 단일 기준 + 파도 가산 — 링 표시가 곧 판정.
                AttackRange = Config.SWARM_ORB_ATTACK_RANGE + colorStats.RangeBonus
            };
        }
    }

    /// <summary>
    ///     아이소메트릭 타원 사거리: 이 맵의 월드 y는 셀 스케일이 x의 절반이라, 유클리드
    ///     원은 화면상 위아래로 과하게 길다. dy를 2배 보정한 타원(= 셀 공간 등거리)이
    ///     기울인 사거리 링(x회전 60°, cos=0.5)과 정확히 일치한다.
    /// </summary>
    private static bool IsWithinSwarmOrbRange(ProximityCombatActor attacker, ProximityCombatActor target)
    {
        // 파도 사거리 가산이 액터에 실려 온다 — 없으면(0) 기본 사거리.
        float range = attacker.AttackRange > 0f ? attacker.AttackRange : Config.SWARM_ORB_ATTACK_RANGE;
        float dx = target.Position.X - attacker.Position.X;
        float dy = (target.Position.Y - attacker.Position.Y) * 2f;
        return dx * dx + dy * dy <= range * range;
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
