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

    // 오브 HP 전투 퇴역 (#226 재개편): 미사일·물폭탄·몹 공격은 전부 본체 오염 직행 —
    // 오브 파괴는 열 절단(+폐쇄) 전용이라야 절단이 독립 전투 동사로 산다.
    private static readonly bool SwarmOrbHealthEnabled = false;

    // PvP 오염 환산 (#226 재개편): 본체 상시 피격 체제의 TTK 앵커. 0.15 = 혼성 6오브
    // 원시 DPS(~28)를 오염 ~4.2/s로 눌러 동급 정면 TTK ~24초(목표 22~28). PvE는 원시
    // 피해 유지(배율 분리). 소수 이월 누산으로 정수 반올림 왜곡(바람 최소 1 인플레)을 막는다.
    // 0.15 → 0.35 (2026-08-12 유도탄 복귀 후 재보정): 게이지 420 기준 0.15는 풀히트로도
    // TTK ~56초+자연회복 — "안 박히는" 체감의 수치적 실체. 0.35 = TTK 22~28초 목표 정렬.
    private const float SwarmPvpCorruptionPerDamage = 0.35f;
    private readonly Dictionary<(long MatchingId, long PlayerId), float> _swarmPvpCorruptionCarry = new();

    /// <summary>PvP 피해 → 본체 오염 이월 누산. 반환 = 이번 타에 실제 적용할 오염(0 가능).</summary>
    private int ConsumeSwarmPvpCorruption(long matchingId, long victimId, int rawDamage)
    {
        var key = (matchingId, victimId);
        float total = (_swarmPvpCorruptionCarry.TryGetValue(key, out float carry) ? carry : 0f) +
                      rawDamage * SwarmPvpCorruptionPerDamage;
        int whole = (int)total;
        _swarmPvpCorruptionCarry[key] = total - whole;
        return whole;
    }

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

    // PvP 유도탄 착탄 지연 (2026-08-12 복귀): 발사 확정, 피해는 비행시간 뒤 — 회피 없음.
    private readonly List<(long MatchingId, ProximityCombatAttack Attack, DateTime DueAtUtc)>
        _pendingSwarmPvpHits = new();

    // 잼 리더보드 (#222 M3): 마지막 브로드캐스트 시그니처 — 변동이 없으면 재전송하지 않는다.
    private readonly Dictionary<long, string> _swarmJamRankingsSignature = new();

    // 만료 판정 (#222 M3-2): 중복 정산 가드 + 게이트 없는 매치(봇 전용)의 대체 앵커.
    private readonly HashSet<long> _swarmTimeoutEndedMatchings = new();
    private readonly Dictionary<long, DateTime> _swarmMatchFallbackAnchorUtc = new();

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
            {
                // 처치 계측 (#226 E): 종·구역·처치자 — 요약의 몹 처치 지표가 이 이벤트를 읽는다.
                _gameEventLogManager.LogEmotionAfterimageKilled(
                    matchingId, damageResult.MonsterId,
                    damageResult.MonsterState.AreaType.ToString(),
                    isCore: damageResult.Kind == SwarmMonsterKind.RunawayGoblin,
                    firstAttackerPlayerId: hit.AttackerId,
                    lastAttackerPlayerId: hit.AttackerId,
                    new Dictionary<long, int> { [hit.AttackerId] = hit.Damage });
                SpawnSpotArenaSummonStone(
                    matchingId, damageResult.MonsterState, sessions,
                    damageResult.HeartReward, damageResult.BootsReward, damageResult.KeyReward);
            }
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
            GameClientSession.SwarmDummyMoveCallback ??= MoveSwarmCutDummy;
            GameClientSession.SwarmGrowthPickCallback ??= HandleSwarmGrowthPick;
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

        // #226 단계 B: 시작 스쿼드 = 랜덤 T1 오브 3개 (사람·봇 공통) — 시작부터 열이 보여야
        // "오브 수 = 점수"가 첫 관전에서 읽힌다. 빈손이 되면 개봉 무료 규칙이 재기를 보장.
        foreach (var session in sessions)
        {
            if (!session.PlayerId.HasValue ||
                !_swarmStartingOrbGrantedPlayers.Add((matchingId, session.PlayerId.Value)))
                continue;

            // 잼 지갑 리셋 (#222 M3) — 세션이 매치를 넘어 살아있으므로 시작 지급 시점에 초기화.
            session.ResetJam();
            session.FreeSummonCharges = 0;
            for (int grant = 0; grant < Config.SWARM_STARTING_ORB_COUNT; grant++)
                session.GrantSwarmArenaOrb(
                    SwarmStartingOrbPool[Random.Shared.Next(SwarmStartingOrbPool.Length)]);
        }

        foreach (var bot in bots)
        {
            if (!_swarmStartingOrbGrantedPlayers.Add((matchingId, bot.PlayerId)))
                continue;

            for (int grant = 0; grant < Config.SWARM_STARTING_ORB_COUNT; grant++)
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

        // 실험장 자동 세팅 (#226): 사람이 있는 매치는 첫 틱에 절단 더미가 자동으로 선다.
        // 봇 전용 검증 매치는 제외 — 게이트 계측이 오염되지 않게.
        if (SwarmCutDummyAutoSetup && aliveSessions.Count > 0 && aliveBots.Count > 0 &&
            _swarmCutDummyAutoSetupDone.Add(matchingId))
        {
            SetupSwarmCutDummy(matchingId);
            // 실험장 격리: 더미 외 봇은 조용히 퇴장 — 순위·드롭 이벤트 없이 화면에서 사라진다.
            foreach (var other in _botPlayerManager.GetBots(matchingId))
            {
                if (other.IsSwarmCutDummy || other.IsEliminated)
                    continue;
                other.IsEliminated = true;
                using var leavePacket = PacketMaker.G_TO_C_AREA_PLAYER_LEAVE(other.PlayerId);
                foreach (var session in sessions)
                    session.Send(leavePacket);
            }
        }

        // 더미(#226 실험 과녁)는 웨이브 디렉터에서 제외 — 몹이 몰려들지 않아 실험장이 조용하다.
        var dummyIds = aliveBots.Where(bot => bot.IsSwarmCutDummy)
            .Select(bot => bot.PlayerId).ToHashSet();
        var directorParticipants = dummyIds.Count == 0
            ? participants
            : participants.Where(participant => !dummyIds.Contains(participant.PlayerId)).ToList();
        // 실험장 (#226): 몹은 나오되(색 무기 과녁) 공격 피해만 아래 게이트에서 꺼진다.
        var tick = _swarmArenaManager.Tick(matchingId, directorParticipants, nowUtc);

        // 공급 스폰 계측 (#226 E): 공급지·무리 차수·마릿수·석 보상 기록.
        foreach (var supplySpawn in tick.SupplyPackSpawns)
            _gameEventLogManager.LogSystem(
                matchingId,
                $"supply_pack area={supplySpawn.Area} pack={supplySpawn.PackIndex} " +
                $"monsters={supplySpawn.MonsterCount} stones={supplySpawn.StoneTotal}");

        // 절단 실험 더미 (#226): 불사 + 오브 리필 — 절단·포위 타격감 튜닝용 과녁.
        // 리필 기준은 피격 시각이 아니라 오브 수다 (#227 수리): 피격 스탬프는 PvP 미사일이
        // 매 발 갱신해 3초 유예가 영영 지나지 않았다 — 끊어도 다시 안 차던 원인.
        foreach (var dummyBot in aliveBots)
        {
            if (!dummyBot.IsSwarmCutDummy)
                continue;
            dummyBot.Corruption = 0;
            ProcessSwarmCutDummyRefill(matchingId, dummyBot, nowUtc);
        }

        // 오브열 (#226 α/C/B): 경로 기록 → 이동 선분의 상대 열 절단 → 고리 완성 포위 사격.
        UpdateSwarmOrbTrails(matchingId, participants);
        ProcessSwarmTrailCuts(matchingId, nowUtc, participants, aliveSessions, aliveBots, sessions);
        // 반격 보호 창 결산 (#227 7단계) — 만료된 쌍만 CUT_RETALIATION_WINDOW로 남긴다.
        ProcessSwarmRetaliationWindows(matchingId, nowUtc, participants);
        ProcessSwarmEncirclements(matchingId, nowUtc, participants, aliveSessions, aliveBots, sessions);
        // 실험장 (#227): 더미 매치에서는 물폭탄도 끈다 — 파도 오브가 계속 터지면
        // 절단 궤적 실험이 폭발 연출·피해에 묻힌다. 미사일 비무장(AddSwarmParticipantCombatActors)과
        // 같은 조건을 쓴다 — 옵트인 환경변수 자체가 실험장 스위치다.
        if (!SwarmCutDummyAutoSetup && dummyIds.Count == 0)
            ProcessSwarmWaveBombs(matchingId, nowUtc, participants, aliveSessions, aliveBots, sessions);

        // 실험장 (#226): 더미가 있는 매치는 몹 공격도 끈다 — 절단 튜닝 중 방해 금지.
        if (dummyIds.Count == 0)
            foreach (var damage in tick.PlayerDamage)
                ApplySwarmParticipantDamage(matchingId, damage, aliveSessions, aliveBots, sessions);
        ProcessSwarmBotRecovery(matchingId, aliveBots, nowUtc);

        // 봇도 사람과 같은 규칙으로 성장한다: 소환석 5개 + 스팟 소진. 공짜 버튼 소환 없음.
        ProcessSwarmBotExplores(matchingId, aliveBots, sessions);

        if (TryConsumeMonsterPositionBroadcastSlot(matchingId, nowUtc))
            BroadcastMonsterMinimapSnapshot(sessions, _swarmArenaManager.GetVisualStates(matchingId));

        UpdateSwarmMovementSamples(matchingId, participants, nowUtc);
        var actors = BuildSwarmArenaCombatActors(matchingId, aliveSessions, aliveBots, nowUtc);
        ProcessSwarmPvpAttackEvents(
            matchingId, nowUtc, participants, aliveSessions, aliveBots, sessions);
        ProcessSurvivorOrbRecovery(matchingId, actors, aliveSessions, aliveBots, nowUtc);
        BroadcastSurvivorOrbVisualStates(matchingId, actors, sessions);
        BroadcastSwarmOrbRankings(matchingId, sessions, bots);
        // 성장 카드 (#226 단계 C): 소환석이 비용에 닿는 즉시 3택 오퍼 — 상자 트리거 퇴역.
        ProcessSwarmGrowthOffers(matchingId, nowUtc, aliveSessions, aliveBots);
        ProcessSwarmScoreTimeout(matchingId, nowUtc, sessions, aliveSessions, aliveBots);
        // 지난 틱에 예약된 착탄들을 먼저 정산한다 — 체력바가 폭발 시점에 맞춰 닳는다.
        ProcessPendingSwarmMonsterHits(matchingId, nowUtc, sessions);

        // PvP 유도탄 착탄 정산 (2026-08-12 복귀): 비행시간이 지난 발은 확정 명중이다.
        for (int index = _pendingSwarmPvpHits.Count - 1; index >= 0; index--)
        {
            var pending = _pendingSwarmPvpHits[index];
            if (pending.MatchingId != matchingId || nowUtc < pending.DueAtUtc)
                continue;
            _pendingSwarmPvpHits.RemoveAt(index);
            ApplySwarmPvpAttack(matchingId, pending.Attack, aliveSessions, aliveBots, sessions,
                broadcastVfx: false);
        }

        var attacks = _proximityAutoCombatResolver.Resolve(
            matchingId,
            actors,
            nowUtc,
            // PvP도 몬스터와 같은 타원(dy×2) 판정 (#222): 링 스프라이트가 아이소 타원이라
            // 원형 판정은 세로 방향에서 보이는 링의 2배 거리까지 공격이 성립했다 —
            // "링이 겹치기만 해도 공격"으로 읽히던 체감의 원인. 이제 표시가 곧 판정이다.
            // 몹 = 같은 구역이면 사거리 무시 공격 (#226 유저 결정) — 파밍이 막히지 않는다.
            // 플레이어는 색 사거리(타원)+시야 유지.
            // #227 6단계: 이 공용 리졸버는 스웜에서 PvE만 담당한다. PvP는 오브별 독립
            // 연사가 아니라 ProcessSwarmPvpAttackEvents의 속성별 사건으로 정산한다.
            (attacker, target) => !attacker.IsMonsterTarget && target.IsMonsterTarget &&
                                  attacker.Area == target.Area,
            // PvP 조준 계측 (#226 진단): 획득이 없으면 필터, 획득만 있고 발사가 없으면 케이던스.
            onTargetAcquired: targetEvent =>
            {
                if (targetEvent.TargetPlayerId > -1_000_000_000_000L)
                    logger.LogInformation(
                        "Swarm pvp aim acquired: MatchingId={MatchingId}, Attacker={Attacker}, Target={Target}, Weapon={Weapon}",
                        matchingId, targetEvent.AttackerPlayerId, targetEvent.TargetPlayerId,
                        targetEvent.WeaponItemId);
            },
            onTargetLost: targetEvent =>
            {
                if (targetEvent.TargetPlayerId > -1_000_000_000_000L)
                    logger.LogInformation(
                        "Swarm pvp aim lost: MatchingId={MatchingId}, Attacker={Attacker}, Target={Target}, Reason={Reason}",
                        matchingId, targetEvent.AttackerPlayerId, targetEvent.TargetPlayerId,
                        targetEvent.Reason);
            });
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

            // 유령 발사 가드 (#226 진단): 같은 틱에 죽은 몬스터의 CombatTargetId(-4e18대)가
            // 몬스터 분기(monsterId=0 조회)를 통과해 PvP 분기로 새던 문제 — 음수 대역 차단.
            if (attack.TargetPlayerId < -1_000_000_000_000L)
                continue;

            // 태양·바람 유도탄 (2026-08-12 복귀): 발사 연출 즉시 + 비행시간 뒤 착탄 확정 —
            // 직선탄 회피 실험은 상시 이동에서 유령 사격이 됐다. 클라 투사체는 표적을 추적한다.
            if (SurvivorOrbData.TryGetColorAndTier(attack.WeaponItemId, out var pvpColor, out _) &&
                pvpColor is SurvivorOrbColor.Red or SurvivorOrbColor.Green)
            {
                BroadcastSpotArenaAttackVfxToTargetAndObservers(attack, sessions);
                actorById ??= actors
                    .GroupBy(actor => actor.PlayerId)
                    .ToDictionary(group => group.Key, group => group.First());
                float pvpDistance = actorById.TryGetValue(attack.AttackerPlayerId, out var pvpAttacker) &&
                                    actorById.TryGetValue(attack.TargetPlayerId, out var pvpTarget)
                    ? Vector3f.Distance(pvpAttacker.Position, pvpTarget.Position)
                    : Config.SWARM_ORB_ATTACK_RANGE;
                double pvpDelaySeconds =
                    SurvivorOrbData.GetPvpProjectileImpactDelaySeconds(attack.WeaponItemId, pvpDistance);
                _pendingSwarmPvpHits.Add((matchingId, attack, nowUtc.AddSeconds(pvpDelaySeconds)));
                // PvP 발사 계측 (#226 진단): 유저 신고 "오브가 플레이어를 공격 안 함" 추적.
                logger.LogInformation(
                    "Swarm pvp launch: MatchingId={MatchingId}, Attacker={Attacker}, Target={Target}, Weapon={Weapon}",
                    matchingId, attack.AttackerPlayerId, attack.TargetPlayerId, attack.WeaponItemId);
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

        if (closureTick.ClosedAreas.Count == 0)
            return;

        // 폐쇄 = 즉사 + 문 잠금 (#227): 지속 오염으로 서서히 죽는 구조는 "언제 나가야 하는가"의
        // 판단을 흐렸다. 닫히는 순간 안에 있으면 죽고, 그 뒤로는 들어갈 수 없다.
        // 문을 먼저 잠근다 — 죽는 순간에 남이 밀고 들어오면 규칙이 뒤집혀 보인다.
        var lockedDoorIds = _doorStateManager.CloseDoorsForAreas(matchingId, closureTick.ClosedAreas);
        BroadcastDoorStateChanges(sessions, lockedDoorIds, false, 0);

        // 꼬리 파괴가 먼저다: 본인은 밖에 있고 꼬리만 남은 경우가 무보상 파괴 대상이고,
        // 안에 있던 사람은 어차피 탈락 드롭으로 정산된다.
        DestroySwarmOrbsInClosedAreas(matchingId, closureTick.ClosedAreas, sessions);
        EliminateEveryoneInClosedAreas(
            matchingId, closureTick.ClosedAreas, sessions,
            _botPlayerManager.GetBots(matchingId).ToList());
    }

    /// <summary>
    ///     폐쇄 잔류 오브 파괴 (#226 E): 폐쇄 완료 순간, 폐쇄 구역에 남아 있는 꼬리 접미를
    ///     끝에서부터 무보상 파괴한다 — 소환석 낙수 없음. 긴 꼬리는 점수·화력이 높지만
    ///     폐쇄 전에 더 일찍 철수해야 한다는 관리 비용이 여기서 성립한다.
    /// </summary>
    private void DestroySwarmOrbsInClosedAreas(
        long matchingId, IReadOnlyCollection<AreaType> closedAreas, List<GameClientSession> sessions)
    {
        var closed = closedAreas.ToHashSet();
        var owners = new List<(long PlayerId, Vector3f Position, GameClientSession Session)>();
        foreach (var session in sessions)
        {
            if (session.PlayerId.HasValue && !session.IsEliminated &&
                session.LastValidatedPosition != null)
                owners.Add((session.PlayerId.Value, session.LastValidatedPosition, session));
        }

        foreach (var bot in _botPlayerManager.GetBots(matchingId))
        {
            if (!bot.IsEliminated && !bot.IsSwarmCutDummy)
                owners.Add((bot.PlayerId, bot.Position, null));
        }

        foreach (var (playerId, ownerPosition, ownerSession) in owners)
        {
            int orbCount = CountSwarmSquadOrbs(matchingId, playerId);
            if (orbCount == 0)
                continue;
            var closureTiers = GetSwarmOrbTiersInOrder(matchingId, playerId);

            // 꼬리는 경로를 따르므로 폐쇄 구역 잔류분은 항상 접미다 — 끝에서부터 스캔한다.
            int suffixStart = orbCount;
            Vector3f suffixPosition = null;
            for (int ordinal = orbCount - 1; ordinal >= 0; ordinal--)
            {
                var position = GetSwarmOrbTrailPosition(
                    matchingId, playerId, ordinal, ownerPosition, closureTiers);
                var cell = ProximityCombatLineOfSight.WorldPositionToCell(MapId.School, position);
                if (!closed.Contains(GameMapData.GetCurrentArea(MapId.School, cell)))
                    break;
                suffixStart = ordinal;
                suffixPosition = position;
            }

            if (suffixStart >= orbCount || suffixPosition == null)
                continue;

            var destroyed = DestroySwarmOrbsFromOrdinal(matchingId, playerId, suffixStart);
            foreach (var destroyedItem in destroyed)
            {
                _swarmOrbCutCracks.Remove((matchingId, playerId, destroyedItem.ItemUid));
                _swarmOrbDurabilityBonus.Remove((matchingId, playerId, destroyedItem.ItemUid));
                ownerSession?.SendInGameInventoryUpdate(destroyedItem);
            }

            // 파열 연출은 절단 링 재사용 — 전리품은 흩뿌리지 않는다 (폐쇄 파괴 무보상).
            var closedArea = GameMapData.GetCurrentArea(
                MapId.School,
                ProximityCombatLineOfSight.WorldPositionToCell(MapId.School, suffixPosition));
            SendSwarmRingVfx(
                closedArea, playerId, suffixPosition.X, suffixPosition.Y,
                SwarmTrailCutFlashRadius, sessions, SwarmRingVfxKindCut,
                victimId: playerId, fromOrdinal: suffixStart);
            _gameEventLogManager.LogSystem(
                matchingId,
                $"closure_orb_destroyed player={playerId} from={suffixStart} count={destroyed.Count}");
            logger.LogInformation(
                "Swarm closure orb destruction: MatchingId={MatchingId}, PlayerId={PlayerId}, FromOrdinal={FromOrdinal}, Count={Count}",
                matchingId, playerId, suffixStart, destroyed.Count);
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

        // #226 단계 C: 상자 = 소모품 공급처 (사람과 같은 규칙) — 오브 성장은 성장 카드가 맡는다.
        if (!_summonStoneManager.TrySpendStones(
                matchingId, bot.PlayerId, Config.SWARM_BOX_OPEN_COST, out _))
        {
            RngCollectCooldownStore.ClearCooldown(matchingId, spotId);
            return;
        }

        int dropItemId = Random.Shared.Next(100) < 60
            ? Config.HEART_GROUND_ITEM_ID
            : Config.BOOTS_GROUND_ITEM_ID;
        var dropped = _groundItemManager.SpawnItems(
            matchingId, bot.CurrentArea, bot.Position.X, bot.Position.Y, [dropItemId],
            mapId: MapId.School,
            layout: GroundItemSpawnLayout.EliminationScatter);
        if (dropped.Count > 0)
        {
            int remaining = _areaItemStockManager.GetRemainingCount(matchingId, (int)bot.CurrentArea);
            using var packet = PacketMaker.G_TO_C_GROUND_ITEM_SPAWN(
                (int)bot.CurrentArea, remaining, dropped.ToList());
            foreach (var session in sessions)
                if (session.PlayerId.HasValue && session.CurrentArea == bot.CurrentArea)
                    session.Send(packet);
        }

        BroadcastSwarmExploreConsumed(spotId, Config.SWARM_EXPLORE_REGEN_SECONDS, sessions);
        logger.LogInformation(
            "Swarm bot box consumable: MatchingId={MatchingId}, BotId={BotId}, InteractId={InteractId}, Drop={DropItemId}",
            matchingId, bot.PlayerId, spotId, dropItemId);
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
    // 왕복 억제 (#226 F): 방금 떠난 구역으로 수 초 내 복귀하는 지시는 판단 떨림이다 —
    // 봇 매치 계측에서 2초 내 직전 구역 복귀가 112회 나왔다. 피격 도주·경계 대피는 예외.
    private const double SwarmBotAreaReturnCooldownSeconds = 5d;

    private readonly Dictionary<(long MatchingId, long PlayerId),
        (AreaType Area, AreaType PreviousArea, DateTime LeftAtUtc)> _swarmBotAreaMemory = new();

    // 이번 틱 지시가 도주·대피였는가 — 왕복 억제의 유일한 예외. 피격 시간 창을 예외로 쓰면
    // 예외 창 안의 경제 지시(공급·순례) 복귀까지 전부 통과해 "맞음→도주→즉시 복귀" 사이클이 산다.
    private readonly HashSet<(long MatchingId, long PlayerId)> _swarmBotFleeDirective = new();

    // 절단 후 회수 창 (#226 F): 이 시간 동안은 약자 추격보다 바닥 소환석 회수가 먼저다.
    private const double SwarmBotPostCutLootSeconds = 5d;
    private readonly Dictionary<(long MatchingId, long PlayerId), DateTime> _swarmBotLastTrailCutAtUtc = new();

    // 폐쇄 조기 철수 (#226 F): 경고 잔여가 (기본 + 오브당 가산) 이하로 내려오면 나간다 —
    // 긴 꼬리는 문 통과가 느리고, 폐쇄 잔류 꼬리는 무보상 파괴된다.
    private const double SwarmBotClosureEvacuateBaseSeconds = 4d;
    private const double SwarmBotClosureEvacuatePerOrbSeconds = 0.5d;

    private SpotArenaBotDirective ResolveSwarmBotDirective(long matchingId, long botPlayerId)
    {
        var directive = ResolveSwarmBotDirectiveCore(matchingId, botPlayerId);
        var bot = _botPlayerManager.GetBots(matchingId)
            .FirstOrDefault(candidate => candidate.PlayerId == botPlayerId);
        if (bot == null || bot.IsEliminated || bot.CurrentArea == AreaType.None)
            return directive;

        var key = (matchingId, botPlayerId);
        if (!_swarmBotAreaMemory.TryGetValue(key, out var memory))
        {
            _swarmBotAreaMemory[key] = (bot.CurrentArea, AreaType.None, DateTime.MinValue);
            return directive;
        }

        if (memory.Area != bot.CurrentArea)
        {
            memory = (bot.CurrentArea, memory.Area, DateTime.UtcNow);
            _swarmBotAreaMemory[key] = memory;
        }

        if (directive.Mode != SpotArenaBotMode.Escort)
            return directive;

        // 도주·대피 지시는 어디로든 즉시 — 억제는 경제·추격 지시의 판단 떨림에만 건다.
        if (_swarmBotFleeDirective.Contains(key))
            return directive;

        if (directive.DestinationArea == memory.PreviousArea &&
            directive.DestinationArea != bot.CurrentArea &&
            (DateTime.UtcNow - memory.LeftAtUtc).TotalSeconds < SwarmBotAreaReturnCooldownSeconds)
        {
            // 복귀 지시 강등: 쿨다운 동안 현 구역 제자리 — 자동 전투·픽업은 계속 돈다.
            return new SpotArenaBotDirective(
                SpotArenaBotMode.Escort,
                bot.CurrentArea,
                ProximityCombatLineOfSight.WorldPositionToCell(MapId.School, bot.Position),
                bot.Position);
        }

        return directive;
    }

    private SpotArenaBotDirective ResolveSwarmBotDirectiveCore(long matchingId, long botPlayerId)
    {
        _swarmBotFleeDirective.Remove((matchingId, botPlayerId));
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
            _swarmBotFleeDirective.Add((matchingId, botPlayerId));
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

        // 0.3) 폐쇄 조기 철수 (#226 F): 경고 구역에서는 꼬리 길이에 비례해 일찍 나간다.
        var closureSnapshot = _areaClosureManager.GetClientStateSnapshot(matchingId);
        if (closureSnapshot.WarningAreas.Contains(bot.CurrentArea))
        {
            int trailOrbCount = CountSwarmSquadOrbs(matchingId, botPlayerId);
            double evacuateLeadSeconds = SwarmBotClosureEvacuateBaseSeconds +
                                         trailOrbCount * SwarmBotClosureEvacuatePerOrbSeconds;
            if (closureSnapshot.WarningSeconds <= evacuateLeadSeconds)
            {
                // 대피는 도주 예외 — 왕복 억제를 우회해 어디로든 즉시 나간다.
                _swarmBotFleeDirective.Add((matchingId, botPlayerId));
                AreaType closureEvacuationArea = SwarmHuntingAreas
                    .Where(area => !IsSwarmAreaOutside(matchingId, area) &&
                                   !closureSnapshot.WarningAreas.Contains(area))
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
                Cell closureEvacuationCell =
                    GameMapData.GetAreaSpawnCell(MapId.School, closureEvacuationArea);
                return new SpotArenaBotDirective(
                    SpotArenaBotMode.Escort,
                    closureEvacuationArea,
                    closureEvacuationCell,
                    BotPlayerManager.CellToWorldPosition(MapId.School, closureEvacuationCell));
            }
        }

        // 0.5) 상대 전력 비교 (#222): 티어 가중 전력(1/1.75/4)으로 비교한다.
        //      "싸움을 건다 = 유리하다" — 확실히 우세(×1.25 이상)일 때만 추격하고,
        //      동수 포함 그 이하는 회피한다. 동수 대치(뭉쳐서 수동 오브 소모전)가 성립하지
        //      않게 하는 규칙. 임계 사이 구간(1.0~1.25)은 중립 밴드 = 판단 떨림 방지.
        //      빈손은 화력이 0이라 몹도 강자로 취급해 피한다.
        float squadPower = GetSwarmSquadPower(matchingId, botPlayerId);
        bool hasSquadOrbs = squadPower > 0f;
        // 빈손 이속 (#223): 이동 배율이 읽는 플래그 — 판단 틱이 단일 갱신 지점이다.
        bot.IsSwarmBareHanded = !hasSquadOrbs;
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
                // 우세 피격 반응 (#226 재수리): 수직 와리가리는 버킷을 늘려도 촐싹거렸다 —
                // 이긴다고 판단한 봇은 공격자를 향해 압박 전진한다 (이동 중 공격이라 화력 손실 없음).
                // 이미 붙어 있으면(1.5 이내) 지시 없이 통과 — 교전은 자동전투가 맡는다.
                float pressDx = recentAttackerPosition.X - bot.Position.X;
                float pressDy = recentAttackerPosition.Y - bot.Position.Y;
                if (pressDx * pressDx + pressDy * pressDy > 2.25f)
                {
                    Cell pressCell = ProximityCombatLineOfSight.WorldPositionToCell(
                        MapId.School, recentAttackerPosition);
                    if (GameMapData.IsMoveablePosition(MapId.School, pressCell) &&
                        GameMapData.GetCurrentArea(MapId.School, pressCell) is var pressArea &&
                        pressArea != AreaType.None)
                    {
                        return new SpotArenaBotDirective(
                            SpotArenaBotMode.Escort,
                            pressArea,
                            pressCell,
                            BotPlayerManager.CellToWorldPosition(MapId.School, pressCell));
                    }
                }
            }
        }
        if (strongerPosition != null)
        {
            // 위협 앞에서는 채집 채널 홀드도 끊고 뛴다 — 홀드 채로 맞다 죽는 사고 방지 (매치 2372 봇 -78).
            _swarmBotFleeDirective.Add((matchingId, botPlayerId));
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

                var fleeWorld = BotPlayerManager.CellToWorldPosition(MapId.School, fleeCell);
                // 도주지가 제자리면 도주가 아니다 (#223 구석 정지 수리) — 다음 폴백으로 넘긴다.
                if (IsFarEnoughSwarmFleeTarget(bot, fleeWorld))
                    return new SpotArenaBotDirective(
                        SpotArenaBotMode.Escort, fleeArea, fleeCell, fleeWorld);
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
                var fleeFallbackWorld = BotPlayerManager.CellToWorldPosition(MapId.School, fleeFallbackCell);
                // 구석에서 벽에 막힌 probe는 제자리로 수렴한다 (#223) — 가까우면 구역 이탈로.
                if (IsFarEnoughSwarmFleeTarget(bot, fleeFallbackWorld))
                    return new SpotArenaBotDirective(
                        SpotArenaBotMode.Escort, fleeFallbackArea, fleeFallbackCell, fleeFallbackWorld);
            }

            // 벽 방향이거나 도주지가 제자리면 위협 반대편에서 가장 가까운 열린 사냥 구역
            // 스폰으로 물러난다 — 구역을 아예 벗어나야 진짜 도주다 (#223 구석 정지 수리).
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

        // 절단 직후 회수 (#226 F): 방금 끊은 전리품부터 줍는다 — 추격은 그 다음이다.
        if (_swarmBotLastTrailCutAtUtc.TryGetValue((matchingId, botPlayerId), out var lastCutAtUtc) &&
            (DateTime.UtcNow - lastCutAtUtc).TotalSeconds < SwarmBotPostCutLootSeconds &&
            TryFindNearestSwarmGroundStone(matchingId, bot, out Vector3f lootPosition))
        {
            return new SpotArenaBotDirective(
                SpotArenaBotMode.Escort,
                bot.CurrentArea,
                ProximityCombatLineOfSight.WorldPositionToCell(MapId.School, lootPosition),
                lootPosition);
        }

        // 선두 점수 보존 (#226 F): 오브 선두는 약자 추격을 자제한다 — 이기고 있을 때
        // 싸움은 절단(상대의 유일한 역전 수단)에 점수를 노출하는 행동이다.
        if (hasSquadOrbs && weakerRival.HasValue &&
            !IsSwarmOrbLeader(matchingId, botPlayerId))
        {
            // 약자 추격: 접근하면 자동전투(오브 우선 타겟)가 나머지를 한다.
            return new SpotArenaBotDirective(
                SpotArenaBotMode.Escort,
                weakerRival.Value.Area,
                ProximityCombatLineOfSight.WorldPositionToCell(MapId.School, weakerRival.Value.Position),
                weakerRival.Value.Position);
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
        if (TryFindNearestSwarmGroundStone(matchingId, bot, out Vector3f stonePosition))
        {
            return new SpotArenaBotDirective(
                SpotArenaBotMode.Escort,
                bot.CurrentArea,
                ProximityCombatLineOfSight.WorldPositionToCell(MapId.School, stonePosition),
                stonePosition);
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

        // 3.5) 소환석 기근 (#219): 다음 개봉 비용이 부족하면 사냥을 나간다.
        //      지역 공급 (#226 단계 B): 몹이 남은 가장 가까운 공급 무리로 향한다 — 몹은
        //      찾아가는 공유 자원이고, 미니맵 스냅샷으로 사람에게도 같은 정보가 보인다.
        //      빈손 봇은 개봉이 무료라 1)에서 이미 스팟 순례로 빠진다.
        if (_summonStoneManager.GetSnapshot(matchingId, botPlayerId).StoneCount <
            GetSwarmBotExploreCost(matchingId, botPlayerId) &&
            hasSquadOrbs)
        {
            if (SwarmArenaManager.RegionSupplyModeEnabled &&
                TryFindNearestSwarmSupplyMonster(matchingId, bot, out var supplyArea,
                    out var supplyPosition))
            {
                return new SpotArenaBotDirective(
                    SpotArenaBotMode.Escort,
                    supplyArea,
                    ProximityCombatLineOfSight.WorldPositionToCell(MapId.School, supplyPosition),
                    supplyPosition);
            }

            // 캠프 모드 폴백: 봇은 캠프 '위치'만 알고(지도 지식) 생사는 모른다.
            if (!SwarmArenaManager.RegionSupplyModeEnabled &&
                TryChooseSwarmBotCampTarget(matchingId, bot, out var campArea, out var campPosition))
            {
                return new SpotArenaBotDirective(
                    SpotArenaBotMode.Escort,
                    campArea,
                    ProximityCombatLineOfSight.WorldPositionToCell(MapId.School, campPosition),
                    campPosition);
            }
        }

        // 4) 마른 방 탈출: 시작방·복도(또는 몹이 마른 지역 공급 구역)에서 사냥터로 이주한다.
        if (SwarmArenaManager.RegionSupplyModeEnabled)
        {
            // 지역 공급: 현재 구역에 살아있는 몹도, 열 수 있는 스팟 용무도 없으면
            // 몹이 남은 공급 구역으로 이주 — 스폰이 멈춘 종반에는 지시 없이 배회(디렉터 몫).
            bool currentAreaHasSupply = _swarmArenaManager.GetVisualStates(matchingId)
                .Any(monster => monster.IsAlive && monster.AreaType == bot.CurrentArea);
            if (!currentAreaHasSupply &&
                TryFindNearestSwarmSupplyMonster(matchingId, bot, out var migrateArea,
                    out var migratePosition))
            {
                return new SpotArenaBotDirective(
                    SpotArenaBotMode.Escort,
                    migrateArea,
                    ProximityCombatLineOfSight.WorldPositionToCell(MapId.School, migratePosition),
                    migratePosition);
            }

            return directive;
        }

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

    /// <summary>
    ///     지역 공급 사냥 목적지 (#226 단계 B): 폐쇄·경계 밖을 제외하고 살아있는 공급 몹 중
    ///     가장 가까운 개체의 위치. 봇의 파밍 이동은 항상 Escort 모드로 나가야 개봉 채널
    ///     완료 로직이 산다 (Return 단락 사고 2026-08-12).
    /// </summary>
    private bool TryFindNearestSwarmSupplyMonster(
        long matchingId, BotPlayerState bot, out AreaType area, out Vector3f position)
    {
        area = AreaType.None;
        position = null;
        float bestSquared = float.MaxValue;
        foreach (var monster in _swarmArenaManager.GetVisualStates(matchingId))
        {
            if (!monster.IsAlive || IsSwarmAreaOutside(matchingId, monster.AreaType))
                continue;
            float dx = monster.PositionX - bot.Position.X;
            float dy = monster.PositionY - bot.Position.Y;
            float distanceSquared = dx * dx + dy * dy;
            if (distanceSquared >= bestSquared)
                continue;
            bestSquared = distanceSquared;
            area = monster.AreaType;
            position = new Vector3f(monster.PositionX, monster.PositionY, 0f);
        }

        return position != null;
    }

    // 라이벌 스캔 반경: 이 안의 참가자와 전력을 비교해 회피/추격을 정한다.
    // 최대 공격 사거리(기본 7 + 파도 가산 3)보다 넓어야 한다 — 6이던 시절, 파도 빌드가
    // 봇의 인지 밖(6~10)에서 일방적으로 쏘는 사각이 있었다 (매치 2376 봇 -108).
    private const float SwarmBotRivalScanRadius = 11f;

    // 도주 방향 앞의 가상 지점 — 이 지점 기준 최근접 스팟이 "위협 반대편 재기 스팟"이 된다.
    private const float SwarmBotFleeProbeDistance = 8f;

    // 도주지 최소 거리 (#223 구석 정지 수리): 구석에서 벽에 막힌 도주지는 제자리로
    // 수렴한다 — 이보다 가까우면 도주가 아니므로 다음 폴백(구역 이탈)으로 넘긴다.
    private const float SwarmBotMinFleeTargetDistance = 3f;

    private static bool IsFarEnoughSwarmFleeTarget(BotPlayerState bot, Vector3f target)
    {
        float dx = target.X - bot.Position.X;
        float dy = target.Y - bot.Position.Y;
        return dx * dx + dy * dy >=
               SwarmBotMinFleeTargetDistance * SwarmBotMinFleeTargetDistance;
    }

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
    // 스트레이프(수직 와리가리)는 퇴역 (#226): 1초 반전은 좌우 연타, 3초 버킷도 촐싹거림 —
    // 우세 피격 반응은 압박 전진(ChooseSwarmBotDirective)으로 대체됐다.

    // ===== 오브열 (#226 실험 α/β): 서버 경로 추적 — 오브별 공격 원점·본체 접촉 판정의 좌표 =====

    private const float SwarmTrailSampleMinDistance = 0.08f;
    private const float SwarmTrailTeleportResetDistance = 5f;

    // 열 절단 (#226 단계 A 정규화): 대상 오브(ItemUid)별 래치 — 0.12초 내부 중복 억제 +
    // 같은 오브를 다시 때리려면 판정 타원 밖으로 완전히 나갔다 와야 한다(이탈 재무장).
    // 서로 다른 오브 연속 타격은 자유 — 꼬리를 따라 달리면 순차로 금이 간다.
    // 0.12 → 0.7 (2026-08-12 내구 모델): 움직이는 열이 절단자를 스치면 오브가 타원을 벗어나며
    // 매 틱 재무장돼 내구 2가 0.24초에 소진됐다 — 같은 오브 재타는 별개의 통과여야 한다.
    private const double SwarmTrailCutSameOrbDebounceSeconds = 0.7d;
    private const float SwarmTrailCutMaxSegmentLength = 2f;
    // 절단자 한정 반격 보호 (#227 7단계): 실제 꼬리 상실 순간부터 1.2초.
    // 전역 무적이 아니라 '방금 내 꼬리를 자른 그 사람에게 되갚을 시간'이다 —
    // 제3자·잔상·폐쇄 피해는 그대로 들어오고, 피해자는 이동·사격·역절단을 다 할 수 있다.
    private const double SwarmCutRetaliationWindowSeconds = 1.2d;
    // 오브 관통 판정 (정규화 dy×2 공간): 링크 선을 스치는 게 아니라 오브를 밟아야 끊긴다.
    // 반경 0.35 원형 + 판정 중심 위 오프셋 (2026-08-12 확정): 스프라이트가 떠 있어 위 접근이
    // 짜던 문제는 중심 오프셋만으로 해결 — 반경을 키우면 옆 오브(간격 0.9)까지 문다.
    private const float SwarmTrailCutOrbHitRadiusX = 0.35f;
    private const float SwarmTrailCutOrbHitRadiusY = 0.35f;
    private const float SwarmTrailCutOrbHitYOffset = 0.15f;
    // 절단 파열 플래시 반경 — 포위 링과 같은 원형을 작게 띄운다.
    private const float SwarmTrailCutFlashRadius = 0.7f;

    private readonly Dictionary<(long MatchingId, long PlayerId), List<Vector3f>> _swarmOrbTrails = new();
    private readonly Dictionary<(long MatchingId, long PlayerId), Vector3f> _swarmTrailLastTickPositions = new();
    // ItemUid별 절단 래치 (단계 A): 마지막 타격 시각 — 중복 억제·이탈 재무장의 기준.
    private readonly Dictionary<(long MatchingId, long CutterId, long ItemUid), DateTime> _swarmOrbCutLatches =
        new();
    /// <summary>
    ///     반격 보호 창 (#227 7단계): 절단자–피해자 <b>쌍</b>으로 연다. 같은 키가 다시 열리면
    ///     시간만 연장하고 집계는 이어간다 — 만료 시 CUT_RETALIATION_WINDOW로 결산한다.
    /// </summary>
    private sealed class SwarmRetaliationWindow
    {
        public DateTime OpenedAtUtc;
        public DateTime ExpiresAtUtc;
        public int BlockedDamage;
        public int BlockedHits;
        public int BlockedCuts;
        public bool Retaliated;
        public AreaType OpenedArea;
    }

    private readonly Dictionary<(long MatchingId, long CutterId, long VictimId), SwarmRetaliationWindow>
        _swarmCutRetaliationWindows = new();

    /// <summary>이 절단자가 이 피해자에게 손댈 수 없는 상태인가 — 절단·본체 피해 공통 관문.</summary>
    private bool IsSwarmRetaliationGuarded(long matchingId, long cutterId, long victimId, DateTime nowUtc)
    {
        return _swarmCutRetaliationWindows.TryGetValue((matchingId, cutterId, victimId), out var window) &&
               nowUtc < window.ExpiresAtUtc;
    }

    /// <summary>
    ///     보호 창을 연다. 같은 쌍이 다시 열리면 시간만 연장하고 집계는 이어간다.
    ///     반대 방향 창이 살아 있으면 = 방금 나를 자른 사람을 내가 잘랐다 = 역절단 성립이다.
    /// </summary>
    private void OpenSwarmRetaliationWindow(
        long matchingId, long cutterId, long victimId, AreaType area, DateTime nowUtc,
        List<GameClientSession> allSessions)
    {
        if (_swarmCutRetaliationWindows.TryGetValue((matchingId, victimId, cutterId), out var opposite) &&
            nowUtc < opposite.ExpiresAtUtc)
            opposite.Retaliated = true;

        var key = (matchingId, cutterId, victimId);
        if (!_swarmCutRetaliationWindows.TryGetValue(key, out var window))
        {
            window = new SwarmRetaliationWindow { OpenedAtUtc = nowUtc, OpenedArea = area };
            _swarmCutRetaliationWindows[key] = window;
        }

        window.ExpiresAtUtc = nowUtc.AddSeconds(SwarmCutRetaliationWindowSeconds);

        // 잔광은 피해자 본인과 그 절단자에게만 — 제3자는 정상 공격할 수 있으니 보지 않는다.
        SendSwarmRetaliationVfx(
            cutterId, victimId, area, SwarmRingVfxKindRetaliationGuard,
            (float)SwarmCutRetaliationWindowSeconds, allSessions);
    }

    /// <summary>만료된 창을 결산해 CUT_RETALIATION_WINDOW로 남긴다 — 매 틱 호출.</summary>
    private void ProcessSwarmRetaliationWindows(
        long matchingId, DateTime nowUtc, List<SpotArenaPlayerSpatial> participants)
    {
        var expired = _swarmCutRetaliationWindows
            .Where(pair => pair.Key.MatchingId == matchingId && nowUtc >= pair.Value.ExpiresAtUtc)
            .ToList();
        foreach (var (key, window) in expired)
        {
            _swarmCutRetaliationWindows.Remove(key);

            // 양측 이탈: 창이 닫히는 시점에 둘이 같은 구역에 없다 = 싸움을 접고 갈라섰다.
            var cutter = participants.FirstOrDefault(p => p.PlayerId == key.CutterId);
            var victim = participants.FirstOrDefault(p => p.PlayerId == key.VictimId);
            bool bothDisengaged = cutter == null || victim == null || cutter.Area != victim.Area;

            _gameEventLogManager.LogSwarmRetaliationWindow(
                matchingId, key.CutterId, key.VictimId,
                window.BlockedDamage, window.BlockedHits, window.BlockedCuts,
                window.Retaliated, bothDisengaged, window.OpenedArea.ToString());
        }
    }

    /// <summary>보호 창 전용 연출 — 피해자와 그 절단자에게만 보낸다(제3자 제외).</summary>
    private void SendSwarmRetaliationVfx(
        long cutterId, long victimId, AreaType area, int kind, float seconds,
        List<GameClientSession> allSessions)
    {
        using var packet = Packet.Create((int)Protocol.G_TO_C_SWARM_ENCIRCLE_VFX);
        packet.SetBody(MessagePackSerializer.Serialize(new G_TO_C_SWARM_ENCIRCLE_VFX
        {
            OwnerPlayerId = cutterId,
            CenterX = 0f,
            CenterY = 0f,
            Radius = seconds,
            Kind = kind,
            VictimPlayerId = victimId,
            FromOrdinal = 0
        }));
        foreach (var session in allSessions)
        {
            if (!session.PlayerId.HasValue || session.CurrentArea != area)
                continue;
            if (session.PlayerId.Value == victimId || session.PlayerId.Value == cutterId)
                session.Send(packet);
        }
    }

    /// <summary>클라 PlayerTool.UpdateOrbTrail과 같은 규칙 — 정지하면 경로가 얼어 열이 남는다.</summary>
    private void UpdateSwarmOrbTrails(long matchingId, List<SpotArenaPlayerSpatial> participants)
    {
        foreach (var participant in participants)
        {
            var key = (matchingId, participant.PlayerId);
            if (!_swarmOrbTrails.TryGetValue(key, out var points))
            {
                points = new List<Vector3f>();
                _swarmOrbTrails[key] = points;
            }

            var center = participant.Position;
            if (points.Count == 0)
            {
                points.Add(new Vector3f(center.X, center.Y, 0f));
                continue;
            }

            float moved = Vector3f.Distance(center, points[0]);
            if (moved >= SwarmTrailTeleportResetDistance)
            {
                points.Clear();
                points.Add(new Vector3f(center.X, center.Y, 0f));
                continue;
            }

            if (moved >= SwarmTrailSampleMinDistance)
                points.Insert(0, new Vector3f(center.X, center.Y, 0f));

            // 실제 오브 수 기준 트림 — 상한(99) 기준이면 참가자당 수천 포인트가 쌓인다.
            int trailOrbCount = Math.Max(CountSwarmSquadOrbs(matchingId, participant.PlayerId) + 2, 4);
            float neededLength = Config.SWARM_ORB_TRAIL_FIRST_OFFSET +
                                 trailOrbCount * Config.SWARM_ORB_TRAIL_SPACING + 1f;
            float accumulated = 0f;
            for (int pointIndex = 1; pointIndex < points.Count; pointIndex++)
            {
                accumulated += Vector3f.Distance(points[pointIndex - 1], points[pointIndex]);
                if (accumulated <= neededLength) continue;
                points.RemoveRange(pointIndex + 1, points.Count - pointIndex - 1);
                break;
            }
        }
    }

    /// <summary>순번째 오브의 열 좌표 — 경로를 순번 × 간격만큼 거슬러 올라간 지점 (클라와 동일 규칙).</summary>
    /// <summary>
    ///     열 순서대로의 티어 목록 — 간격이 오브 크기를 따르므로 좌표 계산의 입력이다 (#227).
    ///     인벤토리 정렬(ItemUid 오름차순)은 절단 체인·전투 액터가 쓰는 순서와 같다.
    /// </summary>
    private List<int> GetSwarmOrbTiersInOrder(long matchingId, long playerId)
    {
        return _inGameInventoryManager.GetPlayerInventory(matchingId, playerId)
            .GetAllItems()
            .Where(item => item.Count > 0 && GetSquadOrbTier(item.ItemId) > 0)
            .OrderBy(item => item.ItemUid)
            .Select(item => GetSquadOrbTier(item.ItemId))
            .ToList();
    }

    private Vector3f GetSwarmOrbTrailPosition(
        long matchingId, long playerId, int ordinal, Vector3f anchor,
        IReadOnlyList<int> orderedTiers = null)
    {
        // 호출부가 목록을 들고 있으면 그걸 쓴다 — 순번마다 인벤토리를 다시 훑지 않게.
        float targetDistance = SurvivorOrbData.GetSwarmTrailDistance(
            orderedTiers ?? GetSwarmOrbTiersInOrder(matchingId, playerId), ordinal);
        if (!_swarmOrbTrails.TryGetValue((matchingId, playerId), out var points) || points.Count == 0)
            return new Vector3f(anchor.X, anchor.Y - targetDistance * 0.2f, 0f);

        Vector3f previous = anchor;
        float accumulated = 0f;
        foreach (var point in points)
        {
            float segment = Vector3f.Distance(previous, point);
            if (segment > 0.0001f && accumulated + segment >= targetDistance)
            {
                float t = (targetDistance - accumulated) / segment;
                return new Vector3f(
                    previous.X + (point.X - previous.X) * t,
                    previous.Y + (point.Y - previous.Y) * t,
                    0f);
            }

            accumulated += segment;
            previous = point;
        }

        Vector3f tailDirection = new(0f, -0.5f, 0f);
        if (points.Count >= 2)
        {
            var last = points[^1];
            var beforeLast = points[^2];
            float dx = last.X - beforeLast.X;
            float dy = last.Y - beforeLast.Y;
            float length = MathF.Sqrt(dx * dx + dy * dy);
            if (length > 0.0001f) tailDirection = new Vector3f(dx / length, dy / length, 0f);
        }

        float remaining = targetDistance - accumulated;
        return new Vector3f(
            previous.X + tailDirection.X * remaining,
            previous.Y + tailDirection.Y * remaining,
            0f);
    }

    /// <summary>
    ///     열 절단 (#226 C): 본체 이동 선분이 상대 오브 링크(오브i-오브i+1)를 가로지르면
    ///     링크의 꼬리 쪽 오브를 즉시 파괴한다. 접촉 오염(β)은 퇴역 — 이동이 곧 공격 동사라는
    ///     문법은 유지하되, 비용이 "본체가 깎임"이 아니라 "상대 성장물이 끊김"으로 바뀐다.
    ///     본체-첫 오브 링크는 절단 불가. 한 이동 선분당 가장 먼저 교차한 링크 하나만 처리한다.
    ///     절단자 자해 없음(P0) — 적 화망 안으로 들어가는 위험이 비용이다.
    /// </summary>
    private void ProcessSwarmTrailCuts(
        long matchingId,
        DateTime nowUtc,
        List<SpotArenaPlayerSpatial> participants,
        List<GameClientSession> aliveSessions,
        List<BotPlayerState> aliveBots,
        List<GameClientSession> allSessions)
    {
        // 소유자별 열 좌표·개체 uid는 틱당 1회만 계산한다.
        // 몹(잔상) 절단은 P0에서 비활성 (단계 A 확정) — 절단은 플레이어의 동사다.
        // 머리 보호 제거 (단계 A 마감): 첫 오브·본체-첫 오브 링크도 절단 대상 — 1오브 열도 잘린다.
        var chains =
            new Dictionary<long, (AreaType Area, Vector3f OwnerPosition, List<Vector3f> Points,
                List<long> Uids, List<int> ItemIds)>();
        foreach (var owner in participants)
        {
            var orbs = _inGameInventoryManager.GetPlayerInventory(matchingId, owner.PlayerId)
                .GetAllItems()
                .Where(item => item.Count > 0 && GetSquadOrbTier(item.ItemId) > 0)
                .OrderBy(item => item.ItemUid)
                .ToList();
            if (orbs.Count == 0)
                continue;
            var points = new List<Vector3f>(orbs.Count);
            var uids = new List<long>(orbs.Count);
            var itemIds = new List<int>(orbs.Count);
            var chainTiers = orbs.Select(item => GetSquadOrbTier(item.ItemId)).ToList();
            for (int ordinal = 0; ordinal < orbs.Count; ordinal++)
            {
                points.Add(GetSwarmOrbTrailPosition(
                    matchingId, owner.PlayerId, ordinal, owner.Position, chainTiers));
                uids.Add(orbs[ordinal].ItemUid);
                itemIds.Add(orbs[ordinal].ItemId);
            }

            chains[owner.PlayerId] = (owner.Area, owner.Position, points, uids, itemIds);
        }

        foreach (var cutter in participants)
        {
            var positionKey = (matchingId, cutter.PlayerId);
            bool hasPrevious = _swarmTrailLastTickPositions.TryGetValue(positionKey, out var previous);
            _swarmTrailLastTickPositions[positionKey] =
                new Vector3f(cutter.Position.X, cutter.Position.Y, 0f);
            if (!hasPrevious)
                continue;
            TryPerformSwarmTrailCut(matchingId, cutter.PlayerId, cutter.PlayerId, cutter.Area,
                previous, cutter.Position, chains, nowUtc, aliveSessions, aliveBots, allSessions);
        }
    }

    /// <summary>공격에 기여하는 오브 수 — 회복 오브는 사격에 참여하지 않아 뺀다 (#227 5단계).</summary>
    private static int CountSwarmAttackOrbs(IReadOnlyList<int> orderedItemIds)
    {
        int count = 0;
        foreach (int itemId in orderedItemIds)
            if (SurvivorOrbData.TryGetColorAndTier(itemId, out _, out _))
                count++;
        return count;
    }

    /// <summary>열 순서대로의 아이템 ID — 절단 후 재조회용.</summary>
    private List<int> GetSwarmOrbItemIdsInOrder(long matchingId, long playerId)
    {
        return _inGameInventoryManager.GetPlayerInventory(matchingId, playerId)
            .GetAllItems()
            .Where(item => item.Count > 0 && GetSquadOrbTier(item.ItemId) > 0)
            .OrderBy(item => item.ItemUid)
            .Select(item => item.ItemId)
            .ToList();
    }

    /// <summary>
    ///     현재 순위(1부터) — 리더보드 브로드캐스트와 같은 정렬(오브 수 → 티어 합 → id).
    ///     절단 한 건당 1회만 부르므로 전수 조회를 그대로 쓴다.
    /// </summary>
    private int GetSwarmPlayerRank(
        long matchingId, long playerId,
        List<GameClientSession> aliveSessions, List<BotPlayerState> aliveBots)
    {
        var entries = aliveSessions
            .Where(session => session.PlayerId.HasValue)
            .Select(session => session.PlayerId!.Value)
            .Concat(aliveBots.Select(bot => bot.PlayerId))
            .Select(id =>
            {
                var (orbCount, tierSum) = GetSwarmOrbScore(matchingId, id);
                return (Id: id, Orbs: orbCount, TierSum: tierSum);
            })
            .OrderByDescending(entry => entry.Orbs)
            .ThenByDescending(entry => entry.TierSum)
            .ThenBy(entry => entry.Id)
            .ToList();

        int index = entries.FindIndex(entry => entry.Id == playerId);
        return index < 0 ? 0 : index + 1;
    }

    /// <summary>
    ///     화망 밀도 (#227 6단계): 주어진 자리를 사거리 안에 두는 적 오브 수.
    ///     사격하는 색(태양·바람)만 센다 — 파도는 미사일을 쏘지 않아 화망이 아니다.
    ///     발사 원점은 본체가 아니라 각 오브의 열 좌표다. ownerFilter를 주면 그 소유자만 센다.
    /// </summary>
    private static int CountSwarmOrbGunsCovering(
        Dictionary<long, (AreaType Area, Vector3f OwnerPosition, List<Vector3f> Points,
            List<long> Uids, List<int> ItemIds)> chains,
        long excludePlayerId,
        AreaType area,
        Vector3f position,
        long? ownerFilter = null)
    {
        int count = 0;
        foreach (var (ownerId, chain) in chains)
        {
            if (ownerId == excludePlayerId || chain.Area != area)
                continue;
            if (ownerFilter.HasValue && ownerId != ownerFilter.Value)
                continue;

            for (int ordinal = 0; ordinal < chain.Points.Count; ordinal++)
            {
                float range = GetSwarmOrbPvpRange(chain.ItemIds[ordinal]);
                if (range > 0f && IsWithinSwarmOrbRange(chain.Points[ordinal], range, position))
                    count++;
            }
        }

        return count;
    }

    private void TryPerformSwarmTrailCut(
        long matchingId,
        long cutterId,
        long creditPlayerId,
        AreaType cutterArea,
        Vector3f previous,
        Vector3f current,
        Dictionary<long, (AreaType Area, Vector3f OwnerPosition, List<Vector3f> Points,
            List<long> Uids, List<int> ItemIds)> chains,
        DateTime nowUtc,
        List<GameClientSession> aliveSessions,
        List<BotPlayerState> aliveBots,
        List<GameClientSession> allSessions)
    {
        float segmentDx = current.X - previous.X;
        float segmentDy = current.Y - previous.Y;
        float segmentLengthSquared = segmentDx * segmentDx + segmentDy * segmentDy;
        // 제자리는 절단이 아니고, 큰 선분(순간이동·좌표 보정·구역 워프)은 오절단 방지로 배제.
        if (segmentLengthSquared < 0.0004f ||
            segmentLengthSquared > SwarmTrailCutMaxSegmentLength * SwarmTrailCutMaxSegmentLength)
            return;

        long bestOwnerId = 0;
        int bestTailOrdinal = -1;
        long bestOrbUid = 0;
        float bestT = float.MaxValue;
        Vector3f bestOrbPosition = null;
        AreaType bestArea = AreaType.None;
        foreach (var (ownerId, chain) in chains)
        {
            if (ownerId == cutterId || chain.Area != cutterArea)
                continue;
            // 반격 보호 (#227 7단계): 방금 이 피해자를 자른 '그 절단자'만 막힌다 —
            // 제3자는 정상적으로 자를 수 있다(전역 면역 퇴역).
            // 막힌 쪽도 교차 판정까지는 돌린다: 실제로 끊었을 교차만 세야 계측이 의미를 갖는다.
            bool guarded = IsSwarmRetaliationGuarded(matchingId, cutterId, ownerId, nowUtc);

            // 절단 판정: 오브 관통(반경 0.35, 중심 위 0.15) 또는 링크(오브 i-1 ↔ i)
            // 가로지르기 — 둘 다 그 오브(i)에 귀속. 머리 보호 제거(단계 A 마감): 첫 오브도
            // 대상이고, 순번 0의 링크는 본체-첫 오브 선분이다.
            for (int ordinal = 0; ordinal < chain.Points.Count; ordinal++)
            {
                var hitPoint = new Vector3f(
                    chain.Points[ordinal].X,
                    chain.Points[ordinal].Y + SwarmTrailCutOrbHitYOffset,
                    0f);

                // ItemUid별 래치 (단계 A): 0.12초 내부 중복 억제 + 이탈 재무장 —
                // 선분 시작점이 아직 판정 타원 안이면(겹친 채 체류) 같은 오브 재타는 없다.
                if (_swarmOrbCutLatches.TryGetValue(
                        (matchingId, cutterId, chain.Uids[ordinal]), out var lastHitAtUtc) &&
                    ((nowUtc - lastHitAtUtc).TotalSeconds < SwarmTrailCutSameOrbDebounceSeconds ||
                     IsInsideOrbHitEllipse(previous, hitPoint)))
                    continue;

                bool hit = TrySegmentHitsPoint(previous, current, hitPoint, out float t);
                if (!hit)
                {
                    var linkStart = ordinal == 0 ? chain.OwnerPosition : chain.Points[ordinal - 1];
                    hit = TrySegmentIntersection(
                        previous, current, linkStart, chain.Points[ordinal], out t);
                }
                if (!hit)
                    continue;
                if (guarded)
                {
                    // 이 교차는 보호 창이 삼켰다 — 한 번의 통과당 1회만 센다.
                    if (_swarmCutRetaliationWindows.TryGetValue(
                            (matchingId, cutterId, ownerId), out var guardWindow))
                        guardWindow.BlockedCuts++;
                    break;
                }

                if (t >= bestT)
                    continue;
                bestT = t;
                bestOwnerId = ownerId;
                bestTailOrdinal = ordinal;
                bestOrbUid = chain.Uids[ordinal];
                bestOrbPosition = chain.Points[ordinal];
                bestArea = chain.Area;
            }
        }

        if (bestOwnerId == 0)
            return;

        _swarmOrbCutLatches[(matchingId, cutterId, bestOrbUid)] = nowUtc;

        // 오브 체력 (#227): 최대 5칸 중 남은 칸이 곧 내구다. 소환 직후는 1/5(크랙 4단계),
        // 방어 강화는 5/5(크랙 0단계). 크랙 단계 = 5 - 남은 칸이라 표시가 곧 판정이다.
        var crackKey = (matchingId, bestOwnerId, bestOrbUid);
        int crackCount = (_swarmOrbCutCracks.TryGetValue(crackKey, out int storedCracks)
            ? storedCracks
            : 0) + 1;
        int requiredHits = SwarmTrailCutBreakHits +
                           _swarmOrbDurabilityBonus.GetValueOrDefault(
                               (matchingId, bestOwnerId, bestOrbUid));

        // 절단 진입 계측 (#227 3·6단계): 들어간 순간의 판단 재료를 통째로 남긴다 —
        // 공격자·피해자·후보 ordinal·맞기 직전 내구·이 교차가 끊었을 때의 손실,
        // 그리고 그 자리를 덮던 적 오브 사거리 수(국소 화망).
        // 크랙만 나든 꼬리가 끊기든 '들어간 순간'은 같으므로 분기 전에 남긴다.
        _gameEventLogManager.LogSwarmCutAttempt(
            matchingId, creditPlayerId, bestOwnerId, bestTailOrdinal,
            CountSwarmOrbGunsCovering(chains, cutterId, cutterArea, current),
            CountSwarmOrbGunsCovering(chains, cutterId, cutterArea, current, bestOwnerId),
            durabilityBeforeHit: requiredHits - crackCount + 1,
            expectedOrbLoss: Math.Max(0, chains[bestOwnerId].Points.Count - bestTailOrdinal),
            breaksNow: crackCount >= requiredHits,
            area: bestArea.ToString());

        if (crackCount < requiredHits)
        {
            _swarmOrbCutCracks[crackKey] = crackCount;
            _gameEventLogManager.LogSwarmOrbCrackAdvanced(
                matchingId, creditPlayerId, bestOwnerId, crackCount, requiredHits,
                bestArea.ToString());
            // 클라에는 누적 타격 수가 아니라 표시할 크랙 단계를 보낸다 — 시작 단계가
            // 오브마다 다르므로(1/5 vs 5/5) 절대값이어야 화면과 남은 칸이 일치한다.
            int displayCrackStage = Math.Clamp(
                SwarmOrbMaxDurability - requiredHits + crackCount,
                0, SwarmOrbMaxDurability - 1);
            SendSwarmRingVfx(
                bestArea, creditPlayerId, bestOrbPosition.X, bestOrbPosition.Y,
                radius: displayCrackStage, allSessions, SwarmRingVfxKindCutCrack,
                victimId: bestOwnerId, fromOrdinal: bestTailOrdinal);
            return;
        }

        // 스네이크 문법 (2026-08-12): 끊긴 지점 이후 꼬리 전체가 잘려나간다 — 절단 지점이
        // 머리에 가까울수록 손실이 크고, 잘린 오브들은 각자 자리에서 전리품으로 흩어진다.
        var destroyedItems = DestroySwarmOrbsFromOrdinal(matchingId, bestOwnerId, bestTailOrdinal);
        if (destroyedItems.Count == 0)
            return;
        // 반격 보호 개시 (#227 7단계) — 크랙만 난 접촉은 여기까지 오지 않는다.
        OpenSwarmRetaliationWindow(matchingId, creditPlayerId, bestOwnerId, bestArea, nowUtc, allSessions);
        // 방어 강화 오브 낙수 보너스 (#226 D): 내구 투자분은 +1석으로 정산 — 제거 전에 기억한다.
        var armoredUids = new HashSet<long>();
        foreach (var destroyedItem in destroyedItems)
        {
            _swarmOrbCutCracks.Remove((matchingId, bestOwnerId, destroyedItem.ItemUid));
            if (_swarmOrbDurabilityBonus.Remove((matchingId, bestOwnerId, destroyedItem.ItemUid)))
                armoredUids.Add(destroyedItem.ItemUid);
        }
        var ownerSession = aliveSessions.FirstOrDefault(session => session.PlayerId == bestOwnerId);
        var ownerChain = chains[bestOwnerId];
        for (int index = 0; index < destroyedItems.Count; index++)
        {
            var destroyedItem = destroyedItems[index];
            ownerSession?.SendInGameInventoryUpdate(destroyedItem);
            int dropOrdinal = bestTailOrdinal + index;
            var dropPosition = dropOrdinal < ownerChain.Points.Count
                ? ownerChain.Points[dropOrdinal]
                : bestOrbPosition;
            ScatterSwarmOrbBreakStones(
                matchingId, destroyedItem.ItemId, bestArea,
                dropPosition.X, dropPosition.Y, allSessions,
                hadDurabilityBonus: armoredUids.Contains(destroyedItem.ItemUid));
        }

        // 절단 파열 플래시: 링 + 잘린 꼬리 오브 섬광 — "어디부터 끊겼다"가 화면에서 읽히게.
        SendSwarmRingVfx(
            bestArea, creditPlayerId, bestOrbPosition.X, bestOrbPosition.Y,
            SwarmTrailCutFlashRadius, allSessions, SwarmRingVfxKindCut,
            victimId: bestOwnerId, fromOrdinal: bestTailOrdinal);

        // 절단 후 회수 (#226 F): 봇 절단자는 잠시 전리품 회수를 추격보다 앞세운다 —
        // 방금 흩어진 소환석이 발밑에 있는데 추격부터 가면 제3자가 줍는다.
        if (BotPlayerManager.IsBotPlayerId(creditPlayerId))
            _swarmBotLastTrailCutAtUtc[(matchingId, creditPlayerId)] = nowUtc;

        var ownerBot = aliveBots.FirstOrDefault(candidate => candidate.PlayerId == bestOwnerId);
        if (ownerBot != null)
        {
            // 절단당한 봇은 피격 반응(도주 판단)으로 즉시 넘어간다.
            ownerBot.LastProximityAttackerPlayerId = creditPlayerId;
            ownerBot.LastDamagedAtUtc = nowUtc;
            _swarmBotLastDamagedAtUtc[(matchingId, ownerBot.PlayerId)] = nowUtc;
            ownerBot.CancelInteractionHold();
        }

        // 절단 전후 대차대조 (#227 5단계): 오브 수(=점수)·순위·공격 기여 수를 한 줄에 묶는다.
        // "몇 개 잃음 → 순위가 바뀜 → 다음 화력이 줄었다"가 한 이벤트에서 확인돼야
        // 전략이 먹혔는지 로그만으로 판정할 수 있다. 전은 파괴 직전 체인, 후는 재조회다.
        int attackOrbsBefore = CountSwarmAttackOrbs(ownerChain.ItemIds);
        int orbsBefore = ownerChain.ItemIds.Count;
        int orbsAfter = CountSwarmSquadOrbs(matchingId, bestOwnerId);
        int attackOrbsAfter = CountSwarmAttackOrbs(GetSwarmOrbItemIdsInOrder(matchingId, bestOwnerId));
        int rankAfter = GetSwarmPlayerRank(matchingId, bestOwnerId, aliveSessions, aliveBots);

        _gameEventLogManager.LogSwarmTrailCut(
            matchingId, creditPlayerId, bestOwnerId, bestTailOrdinal, destroyedItems.Count,
            orbsBefore, orbsAfter, attackOrbsBefore, attackOrbsAfter, rankAfter,
            bestArea.ToString());
        logger.LogInformation(
            "Swarm trail cut: MatchingId={MatchingId}, CutterId={CutterId}, OwnerId={OwnerId}, TailOrdinal={TailOrdinal}, DestroyedCount={DestroyedCount}",
            matchingId, cutterId, bestOwnerId, bestTailOrdinal, destroyedItems.Count);
    }

    // ===== 포위 사격 (#226 B): 이동으로 고리를 완성하면 안쪽을 집중사격한다 =====
    // 상한 99에서 "전체 열 참여"는 닫히지 않는다 — 머리쪽 연속 오브 0..K가 고리를 이루면
    // (오브0-오브K 거리 ≤ 닫힘 임계) 성립하는 부분 고리로 재해석한다. 꼬리는 밖에 남는다.
    private const int SwarmEncircleMinOrbs = 6;
    private const float SwarmEncircleCloseDistance = 1.2f;
    private const float SwarmEncircleMinNormalizedArea = 2f;
    private const double SwarmEncircleHoldSeconds = 0.15d;
    private const double SwarmEncircleCooldownSeconds = 2.5d;
    private const float SwarmEncircleCorruptionCapRatio = 0.35f;
    // 몬스터 포위 피해 (#226 B + 스펙 §6): 해골(12)·다트(18)는 일격, 볼러(48)는 반파.
    private const int SwarmEncircleMonsterDamage = 35;

    private readonly Dictionary<(long MatchingId, long PlayerId), DateTime> _swarmEncircleCandidateSinceUtc =
        new();
    private readonly Dictionary<(long MatchingId, long PlayerId), DateTime> _swarmEncircleCooldownUtc = new();

    /// <summary>
    ///     포위 판정·발사 (#226 B): 후보(고리 완성 + 내부 대상)를 0.15초 유지하면 내부 전원의
    ///     본체에 직접 오염(최대 오염의 35%, 오브 보호 우회)을 가한다. 재무장은 P0에서 쿨다운
    ///     2.5초로 근사한다(경로 소비·거리 조건은 후속). 연출은 클라 후속 — 서버 판정 먼저.
    /// </summary>
    private void ProcessSwarmEncirclements(
        long matchingId,
        DateTime nowUtc,
        List<SpotArenaPlayerSpatial> participants,
        List<GameClientSession> aliveSessions,
        List<BotPlayerState> aliveBots,
        List<GameClientSession> allSessions)
    {
        foreach (var owner in participants)
        {
            var ownerKey = (matchingId, owner.PlayerId);
            if (_swarmEncircleCooldownUtc.TryGetValue(ownerKey, out var cooldownUntil) &&
                nowUtc < cooldownUntil)
            {
                _swarmEncircleCandidateSinceUtc.Remove(ownerKey);
                continue;
            }

            var polygon = TryBuildSwarmEncirclePolygon(matchingId, owner);
            List<SpotArenaPlayerSpatial> victims = null;
            List<SwarmArenaCombatTarget> monsterVictims = null;
            if (polygon != null)
            {
                foreach (var victim in participants)
                {
                    if (victim.PlayerId == owner.PlayerId || victim.Area != owner.Area)
                        continue;
                    if (!IsPointInsidePolygon(polygon, victim.Position))
                        continue;
                    victims ??= new List<SpotArenaPlayerSpatial>();
                    victims.Add(victim);
                }

                // 몬스터도 유효 대상 (스펙 §6) — 웨이브 몹을 가둬 일격하는 것이 첫 포위 경험이 된다.
                foreach (var target in _swarmArenaManager.GetCombatTargets(matchingId))
                {
                    if (target.Area != owner.Area || !IsPointInsidePolygon(polygon, target.Position))
                        continue;
                    monsterVictims ??= new List<SwarmArenaCombatTarget>();
                    monsterVictims.Add(target);
                }
            }

            if (victims == null && monsterVictims == null)
            {
                _swarmEncircleCandidateSinceUtc.Remove(ownerKey);
                continue;
            }

            if (!_swarmEncircleCandidateSinceUtc.TryGetValue(ownerKey, out var candidateSince))
            {
                _swarmEncircleCandidateSinceUtc[ownerKey] = nowUtc;
                continue;
            }

            if ((nowUtc - candidateSince).TotalSeconds < SwarmEncircleHoldSeconds)
                continue;

            _swarmEncircleCandidateSinceUtc.Remove(ownerKey);
            _swarmEncircleCooldownUtc[ownerKey] = nowUtc.AddSeconds(SwarmEncircleCooldownSeconds);
            BroadcastSwarmEncircleVfx(owner, polygon, allSessions);
            if (monsterVictims != null)
            {
                // 몬스터 피해는 지연 정산 파이프라인 재사용 — 킬 보상·상태 브로드캐스트가 따라온다.
                foreach (var target in monsterVictims)
                    _pendingSwarmMonsterHits.Add((matchingId, target.CombatTargetId, owner.PlayerId,
                        SwarmEncircleMonsterDamage, nowUtc));
                logger.LogInformation(
                    "Swarm encirclement monster barrage: MatchingId={MatchingId}, OwnerId={OwnerId}, Monsters={MonsterCount}, PolygonOrbs={PolygonOrbs}",
                    matchingId, owner.PlayerId, monsterVictims.Count, polygon.Count);
            }

            int barrageCorruption = Math.Max(1,
                (int)(Config.SURVIVOR_MAX_CORRUPTION * SwarmEncircleCorruptionCapRatio));
            foreach (var victim in victims ?? [])
            {
                var victimSession = aliveSessions.FirstOrDefault(session =>
                    session.PlayerId == victim.PlayerId);
                if (victimSession != null)
                {
                    victimSession.ModifyStats(corruptionDelta: barrageCorruption,
                        attackerPlayerId: owner.PlayerId);
                }
                else
                {
                    var victimBot = aliveBots.FirstOrDefault(candidate =>
                        candidate.PlayerId == victim.PlayerId);
                    if (victimBot == null)
                        continue;
                    victimBot.Corruption = Math.Min(Config.SURVIVOR_MAX_CORRUPTION,
                        victimBot.Corruption + barrageCorruption);
                    victimBot.LastProximityAttackerPlayerId = owner.PlayerId;
                    victimBot.LastDamagedAtUtc = nowUtc;
                    _swarmBotLastDamagedAtUtc[(matchingId, victimBot.PlayerId)] = nowUtc;
                    victimBot.CancelInteractionHold();
                }

                logger.LogInformation(
                    "Swarm encirclement barrage: MatchingId={MatchingId}, OwnerId={OwnerId}, VictimId={VictimId}, Corruption={Corruption}, PolygonOrbs={PolygonOrbs}",
                    matchingId, owner.PlayerId, victim.PlayerId, barrageCorruption, polygon.Count);
            }
        }
    }

    /// <summary>
    ///     포위 링 연출 브로드캐스트 (#226 B): 다각형의 중심과 정규화(dy×2) 최대 반경을 같은
    ///     구역 세션에 보낸다 — 클라는 사거리 링 원형을 그 크기로 잠깐 띄운다.
    /// </summary>
    private void BroadcastSwarmEncircleVfx(
        SpotArenaPlayerSpatial owner, List<Vector3f> polygon, List<GameClientSession> sessions)
    {
        float centerX = 0f, centerY = 0f;
        foreach (var point in polygon)
        {
            centerX += point.X;
            centerY += point.Y;
        }

        centerX /= polygon.Count;
        centerY /= polygon.Count;
        float radius = 0f;
        foreach (var point in polygon)
        {
            float dx = point.X - centerX;
            float dy = (point.Y - centerY) * 2f;
            float distance = MathF.Sqrt(dx * dx + dy * dy);
            if (distance > radius) radius = distance;
        }

        SendSwarmRingVfx(owner.Area, owner.PlayerId, centerX, centerY, radius, sessions);
    }

    // 링 연출 종류: 클라가 색·효과음을 분기한다. 크랙(3)은 링 없이 슬롯 크랙 + 크랙음만 —
    // Radius 필드에 단계(1~4)를 실어 보낸다.
    private const int SwarmRingVfxKindEncircle = 0;
    private const int SwarmRingVfxKindCut = 1;
    private const int SwarmRingVfxKindWaveBomb = 2;
    private const int SwarmRingVfxKindCutCrack = 3;
    // 철갑 소모 (#226 단계 C): 외피가 절단을 1회 막고 깨질 때의 은색 파열 링.
    private const int SwarmRingVfxKindArmorBreak = 4;
    // 반격 보호 (#227 7단계): 5 = 피해자 남은 꼬리의 유리 잔광 개시(Radius에 지속 초),
    // 6 = 그 절단자의 투사체가 잔광 앞에서 깨짐(피해 숫자 없음).
    private const int SwarmRingVfxKindRetaliationGuard = 5;
    private const int SwarmRingVfxKindRetaliationBlocked = 6;

    // 5단계 → 즉시 절단 (2026-08-12): 밟으면 바로 그 지점부터 꼬리가 끊긴다.
    // 크랙 단계 시스템(1~4 금 + 5타 파괴)은 값만 되돌리면 복원된다.
    private const int SwarmTrailCutBreakHits = 1;
    // 오브 체력 모델 (#227): 모든 오브의 최대 내구는 5칸이고, 소환 직후는 1/5로 시작한다.
    // 방어 강화는 5/5로 채우는 카드다 — 크랙 5단계가 곧 남은 칸이라 표시가 곧 판정.
    private const int SwarmOrbMaxDurability = 5;
    private const int SwarmArmorDurabilityBonus = SwarmOrbMaxDurability - SwarmTrailCutBreakHits;
    private readonly Dictionary<(long MatchingId, long OwnerId, long ItemUid), int> _swarmOrbCutCracks = new();

    /// <summary>링 연출 공용 전송 — 포위 완성(대형)·절단 파열(소형)·물폭탄(파랑)이 같은 원형을 쓴다.</summary>
    private void SendSwarmRingVfx(
        AreaType area, long ownerId, float centerX, float centerY, float radius,
        List<GameClientSession> sessions, int kind = SwarmRingVfxKindEncircle,
        long victimId = 0, int fromOrdinal = 0)
    {
        using var packet = Packet.Create((int)Protocol.G_TO_C_SWARM_ENCIRCLE_VFX);
        packet.SetBody(MessagePackSerializer.Serialize(new G_TO_C_SWARM_ENCIRCLE_VFX
        {
            OwnerPlayerId = ownerId,
            CenterX = centerX,
            CenterY = centerY,
            Radius = radius,
            Kind = kind,
            VictimPlayerId = victimId,
            FromOrdinal = fromOrdinal
        }));
        foreach (var session in sessions)
        {
            if (session.PlayerId.HasValue && session.CurrentArea == area)
                session.Send(packet);
        }
    }

    // ===== 파도 물폭탄 (#226 색 무기): 파도 오브는 미사일 대신 주기마다 자기 위치에
    // 물폭탄을 떨군다. 허공 주기 투하(대상 불요) — 링 텔레그래프 후 반경 내 적 피해.
    // 판정은 기폭 순간 위치 기준(타원 dy×2) — 표시가 곧 판정, 회피는 위치 판단이다. =====
    // 2.5 → 1.2 (2026-08-12): 파도 = 근접 거부 지뢰 — 깨러 접근한 절단자를 빠르게 처벌한다.
    private const double SwarmWaveBombIntervalSeconds = 1.2d;
    private const double SwarmWaveBombFuseSeconds = 0.55d;
    // 1.5 → 2.0 → 2.6 (2026-08-12 2차): 근접 거부 반경이 좁아 존재감이 약했다 — 표시·판정 동시 확장.
    private const float SwarmWaveBombRadius = 2.6f;
    // 허공 투하 기각 (2026-08-12): 적(참가자·몹)이 이 반경 안에 있는 오브만 폭탄을 떨군다.
    private const float SwarmWaveBombTriggerRadius = 3.1f;
    // 판정 여유 (2026-08-12): 클라 링(오브 렌더 위치 정렬)과 서버 좌표의 오차 흡수 —
    // 링 안에 보이는데 안 맞는 억울함 방지. 표시 2.0 vs 판정 2.5.
    private const float SwarmWaveBombJudgeRadius = SwarmWaveBombRadius + 0.5f;
    // PvP·몹 피해 분리 (2026-08-12): PvP는 치명급(오염 환산 0.35 경유 ≈ 105 — 게이지 420의
    // 1/4, 눌러앉으면 연속 피폭) — 절단 접근의 실질 카운터. 몹은 원킬 학살 방지로 저피해 유지.
    private const int SwarmWaveBombPvpDamage = 300;
    private const int SwarmWaveBombMonsterDamage = 14;

    private readonly Dictionary<(long MatchingId, long PlayerId), DateTime> _swarmWaveBombNextDropAtUtc =
        new();
    private readonly List<(long MatchingId, long OwnerId, AreaType Area, Vector3f Position, DateTime
        ExplodeAtUtc)> _pendingSwarmWaveBombs = new();

    private void ProcessSwarmWaveBombs(
        long matchingId,
        DateTime nowUtc,
        List<SpotArenaPlayerSpatial> participants,
        List<GameClientSession> aliveSessions,
        List<BotPlayerState> aliveBots,
        List<GameClientSession> allSessions)
    {
        // 1) 기폭: 예약된 폭탄 정산.
        for (int index = _pendingSwarmWaveBombs.Count - 1; index >= 0; index--)
        {
            var bomb = _pendingSwarmWaveBombs[index];
            if (bomb.MatchingId != matchingId || nowUtc < bomb.ExplodeAtUtc)
                continue;
            _pendingSwarmWaveBombs.RemoveAt(index);
            ExplodeSwarmWaveBomb(matchingId, bomb.OwnerId, bomb.Area, bomb.Position, nowUtc,
                participants, aliveSessions, aliveBots, allSessions);
        }

        // 2) 투하: 파도 오브 보유 참가자별 주기. 비무장(소환·채집 중)은 쉰다 — 미사일과 같은 규칙.
        foreach (var owner in participants)
        {
            var key = (matchingId, owner.PlayerId);
            if (!_swarmWaveBombNextDropAtUtc.TryGetValue(key, out var nextDropAtUtc))
            {
                _swarmWaveBombNextDropAtUtc[key] = nowUtc.AddSeconds(SwarmWaveBombIntervalSeconds);
                continue;
            }

            if (nowUtc < nextDropAtUtc)
                continue;
            _swarmWaveBombNextDropAtUtc[key] = nowUtc.AddSeconds(SwarmWaveBombIntervalSeconds);
            if (!IsSwarmAttackArmed(matchingId, owner.PlayerId, nowUtc))
                continue;

            var bombTiers = GetSwarmOrbTiersInOrder(matchingId, owner.PlayerId);
            foreach (int ordinal in GetSwarmBlueOrbOrdinals(matchingId, owner.PlayerId))
            {
                var position = GetSwarmOrbTrailPosition(
                    matchingId, owner.PlayerId, ordinal, owner.Position, bombTiers);
                // 허공 투하 기각: 그 오브 주변에 적이 있을 때만 떨군다.
                if (!HasSwarmWaveBombTargetNear(matchingId, owner, position, participants))
                    continue;
                _pendingSwarmWaveBombs.Add((matchingId, owner.PlayerId, owner.Area, position,
                    nowUtc.AddSeconds(SwarmWaveBombFuseSeconds)));
                // 소유자·순번 동봉 — 클라가 실제 렌더 슬롯 위치에 링·이펙트를 정렬한다.
                SendSwarmRingVfx(owner.Area, owner.PlayerId, position.X, position.Y,
                    SwarmWaveBombRadius, allSessions, SwarmRingVfxKindWaveBomb,
                    victimId: owner.PlayerId, fromOrdinal: ordinal);
            }
        }
    }

    /// <summary>물폭탄 투하 조건: 오브 반경 안(타원 dy×2)에 적 참가자 또는 몹이 있는가.</summary>
    private bool HasSwarmWaveBombTargetNear(
        long matchingId, SpotArenaPlayerSpatial owner, Vector3f position,
        List<SpotArenaPlayerSpatial> participants)
    {
        // #227 6단계: 기존 오브별 물폭탄은 PvE 전용으로 남긴다. 플레이어 대상 파도 공격은
        // 속성별 한 번의 합산 이벤트가 담당하므로 참가자를 여기서 트리거로 쓰지 않는다.
        _ = participants;
        float radiusSquared = SwarmWaveBombTriggerRadius * SwarmWaveBombTriggerRadius;
        foreach (var target in _swarmArenaManager.GetCombatTargets(matchingId))
        {
            if (target.Area != owner.Area)
                continue;
            float dx = target.Position.X - position.X;
            float dy = (target.Position.Y - position.Y) * 2f;
            if (dx * dx + dy * dy <= radiusSquared)
                return true;
        }

        return false;
    }

    /// <summary>액터 순번과 같은 인벤토리 순서에서 파도(파랑) 오브의 열 순번들을 뽑는다.</summary>
    private List<int> GetSwarmBlueOrbOrdinals(long matchingId, long playerId)
    {
        var ordinals = new List<int>();
        int ordinal = 0;
        foreach (var item in _inGameInventoryManager.GetPlayerInventory(matchingId, playerId).GetAllItems())
        {
            if (item.Count <= 0 || GetSquadOrbTier(item.ItemId) <= 0)
                continue;
            if (SurvivorOrbData.TryGetColorAndTier(item.ItemId, out var color, out _) &&
                color == SurvivorOrbColor.Blue)
                ordinals.Add(ordinal);
            ordinal++;
        }

        return ordinals;
    }

    private void ExplodeSwarmWaveBomb(
        long matchingId,
        long ownerId,
        AreaType area,
        Vector3f position,
        DateTime nowUtc,
        List<SpotArenaPlayerSpatial> participants,
        List<GameClientSession> aliveSessions,
        List<BotPlayerState> aliveBots,
        List<GameClientSession> allSessions)
    {
        // PvP는 ProcessSwarmPvpAttackEvents의 단일 파도 사건이 담당한다. 이 레거시 경로는
        // 잔상 PvE만 유지한다.
        _ = participants;
        _ = aliveSessions;
        _ = aliveBots;
        _ = allSessions;
        _ = nowUtc;
        float radiusSquared = SwarmWaveBombJudgeRadius * SwarmWaveBombJudgeRadius;
        // 몹: 착탄 지연 정산 파이프라인 재사용 — 킬 보상·상태 브로드캐스트가 따라온다.
        foreach (var target in _swarmArenaManager.GetCombatTargets(matchingId))
        {
            if (target.Area != area)
                continue;
            float dx = target.Position.X - position.X;
            float dy = (target.Position.Y - position.Y) * 2f;
            if (dx * dx + dy * dy > radiusSquared)
                continue;
            _pendingSwarmMonsterHits.Add((matchingId, target.CombatTargetId, ownerId,
                SwarmWaveBombMonsterDamage, nowUtc));
        }
    }

    /// <summary>
    ///     머리쪽 부분 고리 탐색: 오브 0..K(K ≥ 최소-1)에서 오브0-오브K가 닫힘 거리 안이면
    ///     그 구간을 다각형으로 만든다. 정규화 (x, y×2) 슈레이스 최소 면적과 자기 교차를
    ///     검증한다. 벽·문 차폐 검증은 지형 통합(D)에서 붙인다.
    /// </summary>
    private List<Vector3f> TryBuildSwarmEncirclePolygon(long matchingId, SpotArenaPlayerSpatial owner)
    {
        int orbCount = CountSwarmSquadOrbs(matchingId, owner.PlayerId);
        if (orbCount < SwarmEncircleMinOrbs)
            return null;

        var encircleTiers = GetSwarmOrbTiersInOrder(matchingId, owner.PlayerId);
        var points = new List<Vector3f>(orbCount);
        for (int ordinal = 0; ordinal < orbCount; ordinal++)
            points.Add(GetSwarmOrbTrailPosition(
                matchingId, owner.PlayerId, ordinal, owner.Position, encircleTiers));

        var head = points[0];
        for (int closeIndex = SwarmEncircleMinOrbs - 1; closeIndex < points.Count; closeIndex++)
        {
            float dx = points[closeIndex].X - head.X;
            float dy = points[closeIndex].Y - head.Y;
            if (dx * dx + dy * dy > SwarmEncircleCloseDistance * SwarmEncircleCloseDistance)
                continue;

            var polygon = points.GetRange(0, closeIndex + 1);
            if (ComputeNormalizedPolygonArea(polygon) < SwarmEncircleMinNormalizedArea)
                return null;
            return IsSimplePolygon(polygon) ? polygon : null;
        }

        return null;
    }

    /// <summary>정규화 (x, y×2) 슈레이스 면적 — 아이소 세로 압축 보정 후의 실질 포위 면적.</summary>
    private static float ComputeNormalizedPolygonArea(List<Vector3f> polygon)
    {
        float doubledArea = 0f;
        for (int index = 0; index < polygon.Count; index++)
        {
            var current = polygon[index];
            var next = polygon[(index + 1) % polygon.Count];
            doubledArea += current.X * (next.Y * 2f) - next.X * (current.Y * 2f);
        }

        return MathF.Abs(doubledArea) * 0.5f;
    }

    /// <summary>자기 교차 검증 — 인접(정점 공유) 변을 제외한 변끼리 교차하면 단순 다각형이 아니다.</summary>
    private static bool IsSimplePolygon(List<Vector3f> polygon)
    {
        int count = polygon.Count;
        for (int i = 0; i < count; i++)
        {
            var a1 = polygon[i];
            var a2 = polygon[(i + 1) % count];
            for (int j = i + 1; j < count; j++)
            {
                if (j == (i + 1) % count || (j + 1) % count == i)
                    continue;
                var b1 = polygon[j];
                var b2 = polygon[(j + 1) % count];
                if (TrySegmentIntersection(a1, a2, b1, b2, out _))
                    return false;
            }
        }

        return true;
    }

    /// <summary>레이 캐스팅 내부 판정 — 균등 스케일이라 정규화 없이 원좌표로 충분하다.</summary>
    private static bool IsPointInsidePolygon(List<Vector3f> polygon, Vector3f point)
    {
        bool inside = false;
        for (int i = 0, j = polygon.Count - 1; i < polygon.Count; j = i++)
        {
            var a = polygon[i];
            var b = polygon[j];
            bool crosses = a.Y > point.Y != b.Y > point.Y &&
                           point.X < (b.X - a.X) * (point.Y - a.Y) / (b.Y - a.Y) + a.X;
            if (crosses)
                inside = !inside;
        }

        return inside;
    }

    // ===== 절단 실험 더미 (#226): 매치의 봇 하나를 운동장 과녁으로 바꾼다 —
    // 정지·불사·오브 10개 일자 꼬리(자동 리필)·비무장·몹 절단 면제. 웨이브 디렉터 제외.
    // 명시적 분리 (단계 0): DEV_CUT_DUMMY=1 환경변수 옵트인 — 일반 매치는 순정으로 돈다. =====
    private static readonly bool SwarmCutDummyAutoSetup =
        Environment.GetEnvironmentVariable("DEV_CUT_DUMMY") == "1";
    private const int SwarmCutDummyOrbCount = 10;
    // 과녁 열은 단색 태양 T1 — 시작 지급의 무작위 색이 섞이면 파도(물폭탄)가 딸려 온다.
    private const int SwarmCutDummyOrbItemId = 107000010;
    // 절단 직후 3초는 비워 둔다 — 즉시 채우면 "끊어도 안 줄어드는" 것처럼 보인다.
    private const double SwarmCutDummyRefillDelaySeconds = 3d;
    private readonly HashSet<long> _swarmCutDummyAutoSetupDone = new();
    private readonly Dictionary<(long MatchingId, long PlayerId), DateTime> _swarmCutDummyRefillAtUtc = new();

    /// <summary>
    ///     더미 오브 리필 (#227 수리): 판단 기준은 오브 수 — 열이 줄어든 걸 본 시점부터
    ///     3초를 세고 채운다. 피격 시각 기준이던 시절엔 PvP 미사일이 스탬프를 매 발 갱신해
    ///     유예가 끝나지 않았다(리필 정지).
    /// </summary>
    private void ProcessSwarmCutDummyRefill(long matchingId, BotPlayerState dummy, DateTime nowUtc)
    {
        var key = (matchingId, dummy.PlayerId);
        if (CountSwarmSquadOrbs(matchingId, dummy.PlayerId) >= SwarmCutDummyOrbCount)
        {
            _swarmCutDummyRefillAtUtc.Remove(key);
            return;
        }

        if (!_swarmCutDummyRefillAtUtc.TryGetValue(key, out var refillAtUtc))
        {
            _swarmCutDummyRefillAtUtc[key] = nowUtc.AddSeconds(SwarmCutDummyRefillDelaySeconds);
            return;
        }

        if (nowUtc < refillAtUtc)
            return;

        _swarmCutDummyRefillAtUtc.Remove(key);
        RefillSwarmCutDummyOrbs(matchingId, dummy);
    }

    /// <summary>봇 플래그 조회 — 참가자 id가 더미인지. 사람(양수)은 항상 false.</summary>
    private bool IsSwarmCutDummyPlayer(long matchingId, long playerId)
    {
        if (playerId >= 0)
            return false;
        foreach (var bot in _botPlayerManager.GetBots(matchingId))
        {
            if (bot.PlayerId == playerId)
                return bot.IsSwarmCutDummy;
        }

        return false;
    }

    private void RefillSwarmCutDummyOrbs(long matchingId, BotPlayerState dummy)
    {
        for (int index = CountSwarmSquadOrbs(matchingId, dummy.PlayerId);
             index < SwarmCutDummyOrbCount;
             index++)
            _inGameInventoryManager.TryAddItemWithCapacity(
                matchingId, dummy.PlayerId, SwarmCutDummyOrbItemId, Config.SWARM_ORB_CAPACITY, out _);

        // 실험 과녁 (#227): 머리쪽 절반은 방어 강화(5/5), 나머지 절반은 맨 오브(1/5) —
        // 같은 열에서 두 내구를 나란히 밟아 비교할 수 있다.
        var trailOrbs = GetSwarmTrailOrbs(matchingId, dummy.PlayerId);
        int armoredCount = (trailOrbs.Count + 1) / 2;
        for (int ordinal = 0; ordinal < trailOrbs.Count; ordinal++)
        {
            var key = (matchingId, dummy.PlayerId, trailOrbs[ordinal].ItemUid);
            if (ordinal < armoredCount)
                _swarmOrbDurabilityBonus[key] = SwarmArmorDurabilityBonus;
            else
                _swarmOrbDurabilityBonus.Remove(key);
        }
    }

    /// <summary>
    ///     절단 실험 더미 세팅 (어드민): 매치의 봇 하나를 운동장 중앙 동쪽에 고정하고
    ///     서쪽으로 일자 꼬리를 심는다. 같은 봇에 재호출하면 위치·꼬리를 재정렬한다.
    /// </summary>
    public object SetupSwarmCutDummy(long matchingId)
    {
        if (matchingId <= 0)
        {
            // 사람이 있는 매치 우선 — 봇 전용 검증 매치(큰 id)가 최신을 가로채지 않게.
            var activeIds = GetActiveInstanceIds().ToList();
            var humanIds = activeIds.Where(id => GetSessionsByInstance(MapId.School, id)
                .Any(session => session.PlayerId.HasValue)).ToList();
            matchingId = (humanIds.Count > 0 ? humanIds : activeIds).DefaultIfEmpty(0).Max();
        }

        if (matchingId <= 0)
            return new { error = "no active match" };

        var bots = _botPlayerManager.GetBots(matchingId)
            .Where(bot => !bot.IsEliminated).ToList();
        var dummy = bots.FirstOrDefault(bot => bot.IsSwarmCutDummy) ?? bots.FirstOrDefault();
        if (dummy == null)
            return new { error = "no alive bot in match " + matchingId };

        var groundCell = GameMapData.GetAreaSpawnCell(MapId.School, AreaType.Ground);
        var center = BotPlayerManager.CellToWorldPosition(MapId.School, groundCell);
        var fromArea = dummy.CurrentArea;
        var fromCell = dummy.Cell;
        dummy.IsSwarmCutDummy = true;
        dummy.CurrentArea = AreaType.Ground;
        dummy.Position = new Vector3f(center.X + 4f, center.Y + 3f, 0f);
        dummy.Cell = MapCoordinateConverter.WorldToCell(MapId.School, dummy.Position);
        dummy.Path.Clear();
        dummy.PathIndex = 0;
        dummy.Corruption = 0;

        // 꼬리: 동→서 일자 경로를 미리 심는다 — points[0] = 현재 위치(최신).
        var trailPoints = new List<Vector3f>();
        for (float distance = 0f; distance <= 12f; distance += 0.3f)
            trailPoints.Add(new Vector3f(dummy.Position.X - distance, dummy.Position.Y, 0f));
        _swarmOrbTrails[(matchingId, dummy.PlayerId)] = trailPoints;
        _swarmTrailLastTickPositions[(matchingId, dummy.PlayerId)] =
            new Vector3f(dummy.Position.X, dummy.Position.Y, 0f);
        // 시작 지급의 무작위 색(파도 포함)을 비우고 단색 태양 열로 재구성한다 (#227).
        _inGameInventoryManager.TakeAllItems(matchingId, dummy.PlayerId);
        _swarmCutDummyRefillAtUtc.Remove((matchingId, dummy.PlayerId));
        RefillSwarmCutDummyOrbs(matchingId, dummy);

        var sessions = GetSessionsByInstance(MapId.School, matchingId).ToList();
        BroadcastBotMovement(matchingId, new BotMovementEvent
        {
            BotPlayerId = dummy.PlayerId,
            FromArea = fromArea,
            ToArea = AreaType.Ground,
            FromCell = fromCell,
            ToCell = dummy.Cell,
            Position = dummy.Position,
            Velocity = new Vector3f(0f, 0f, 0f),
            Rotation = 0f,
            IsAreaTransition = fromArea != AreaType.Ground
        }, sessions);
        logger.LogInformation(
            "Swarm cut dummy ready: MatchingId={MatchingId}, DummyId={DummyId}, Position=({X},{Y})",
            matchingId, dummy.PlayerId, dummy.Position.X, dummy.Position.Y);
        return new
        {
            matchingId,
            dummyId = dummy.PlayerId,
            x = dummy.Position.X,
            y = dummy.Position.Y,
            orbs = SwarmCutDummyOrbCount
        };
    }

    /// <summary>
    ///     더미 WASD 조종 (#226 실험장): 클라 방향 입력을 스텝 이동으로 적용하고 걷기를
    ///     브로드캐스트한다 — 더미가 움직여야 클라 열이 자연 간격(0.9)으로 펼쳐진다.
    /// </summary>
    private void MoveSwarmCutDummy(long matchingId, float dirX, float dirY)
    {
        var dummy = _botPlayerManager.GetBots(matchingId)
            .FirstOrDefault(bot => bot.IsSwarmCutDummy && !bot.IsEliminated);
        if (dummy == null)
            return;

        float length = MathF.Sqrt(dirX * dirX + dirY * dirY);
        if (length < 0.01f)
            return;

        // 10Hz 전송 기준 스텝 0.5 = 5u/s — 플레이어 달리기와 동급.
        const float step = 0.5f;
        var proposed = new Vector3f(
            dummy.Position.X + dirX / length * step,
            dummy.Position.Y + dirY / length * step,
            0f);
        var proposedCell = MapCoordinateConverter.WorldToCell(MapId.School, proposed);
        if (!GameMapData.IsMoveablePosition(MapId.School, proposedCell))
            return;

        var fromArea = dummy.CurrentArea;
        var fromCell = dummy.Cell;
        dummy.Position = proposed;
        dummy.Cell = proposedCell;
        var currentArea = GameMapData.GetCurrentArea(MapId.School, proposedCell);
        if (currentArea != AreaType.None)
            dummy.CurrentArea = currentArea;

        BroadcastBotMovement(matchingId, new BotMovementEvent
        {
            BotPlayerId = dummy.PlayerId,
            FromArea = fromArea,
            ToArea = dummy.CurrentArea,
            FromCell = fromCell,
            ToCell = proposedCell,
            Position = proposed,
            Velocity = new Vector3f(dirX / length * 5f, dirY / length * 5f, 0f),
            Rotation = 0f,
            IsAreaTransition = fromArea != dummy.CurrentArea
        }, GetSessionsByInstance(MapId.School, matchingId).ToList());
    }

    /// <summary>
    ///     열 순번부터 꼬리 끝까지 인벤토리에서 즉시 파괴한다 (스네이크 문법). 순번 매핑은
    ///     전투 액터·클라 슬롯과 같은 인벤토리 순서(스쿼드 오브 필터).
    /// </summary>
    private List<InGameItemInfo> DestroySwarmOrbsFromOrdinal(long matchingId, long playerId, int fromOrdinal)
    {
        var destroyed = new List<InGameItemInfo>();
        var inventory = _inGameInventoryManager.GetPlayerInventory(matchingId, playerId);
        var orbs = inventory.GetAllItems()
            .Where(item => item.Count > 0 && GetSquadOrbTier(item.ItemId) > 0)
            .OrderBy(item => item.ItemUid)
            .ToList();
        if (fromOrdinal < 0 || fromOrdinal >= orbs.Count)
            return destroyed;

        for (int ordinal = fromOrdinal; ordinal < orbs.Count; ordinal++)
        {
            if (inventory.TryRemoveItem(orbs[ordinal].ItemUid, 1, out var destroyedItem) &&
                destroyedItem != null)
                destroyed.Add(destroyedItem);
        }

        if (destroyed.Count > 0)
            _swarmFrontOrbHp.Remove((matchingId, playerId));
        return destroyed;
    }

    /// <summary>점이 오브 판정 타원 안에 있는지 — 래치 이탈 재무장 판정.</summary>
    private static bool IsInsideOrbHitEllipse(Vector3f point, Vector3f orbHitPoint)
    {
        float dx = (point.X - orbHitPoint.X) / SwarmTrailCutOrbHitRadiusX;
        float dy = (point.Y * 2f - orbHitPoint.Y * 2f) / SwarmTrailCutOrbHitRadiusY;
        return dx * dx + dy * dy <= 1f;
    }

    /// <summary>
    ///     이동 선분이 오브(점)를 관통했는지 — 정규화(dy×2) 공간에서 최근접점을 구한 뒤
    ///     가로 좁고 세로 후한 타원으로 판정한다 (이웃 오차 방지 + 부양 스프라이트 보정).
    /// </summary>
    private static bool TrySegmentHitsPoint(
        Vector3f from, Vector3f to, Vector3f point, out float t)
    {
        float ax = from.X;
        float ay = from.Y * 2f;
        float abx = to.X - ax;
        float aby = to.Y * 2f - ay;
        float px = point.X;
        float py = point.Y * 2f;
        float lengthSquared = abx * abx + aby * aby;
        t = lengthSquared > 0.000001f
            ? Math.Clamp(((px - ax) * abx + (py - ay) * aby) / lengthSquared, 0f, 1f)
            : 0f;
        float dx = (ax + abx * t - px) / SwarmTrailCutOrbHitRadiusX;
        float dy = (ay + aby * t - py) / SwarmTrailCutOrbHitRadiusY;
        return dx * dx + dy * dy <= 1f;
    }

    /// <summary>선분 교차 판정 — t는 절단자 선분 위의 교차 지점 비율(가장 이른 링크 선택 기준).</summary>
    private static bool TrySegmentIntersection(
        Vector3f a1, Vector3f a2, Vector3f b1, Vector3f b2, out float t)
    {
        t = 0f;
        float rx = a2.X - a1.X;
        float ry = a2.Y - a1.Y;
        float sx = b2.X - b1.X;
        float sy = b2.Y - b1.Y;
        float denominator = rx * sy - ry * sx;
        if (MathF.Abs(denominator) < 0.000001f)
            return false;

        float qpx = b1.X - a1.X;
        float qpy = b1.Y - a1.Y;
        t = (qpx * sy - qpy * sx) / denominator;
        float u = (qpx * ry - qpy * rx) / denominator;
        return t >= 0f && t <= 1f && u >= 0f && u <= 1f;
    }

    /// <summary>
    ///     같은 구역의 반응 지연 지난 최근접 바닥 소환석 — 봇 회수 지시의 목적지.
    ///     반응 지연 (#222): 갓 떨어진 돌은 무시 — 사람이 먼저 주울 시간을 준다.
    /// </summary>
    private bool TryFindNearestSwarmGroundStone(
        long matchingId, BotPlayerState bot, out Vector3f position)
    {
        position = null;
        float bestDistanceSquared = float.MaxValue;
        foreach (var item in _groundItemManager.GetSnapshot(matchingId, bot.CurrentArea))
        {
            if (item.ItemId != Config.SUMMON_STONE_GROUND_ITEM_ID ||
                _groundItemManager.IsYoungerThan(
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

    /// <summary>오브 선두 판독 (#226 F): 내 오브 수가 생존자 최다와 같거나 크면 선두다.</summary>
    private bool IsSwarmOrbLeader(long matchingId, long playerId)
    {
        int myOrbCount = GetSwarmOrbScore(matchingId, playerId).OrbCount;
        return myOrbCount > 0 && myOrbCount >= GetSwarmTopOrbCount(matchingId);
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

    /// <summary>
    ///     봇 개봉 문턱 (#226 단계 C): 실지불은 상자 고정가(1)지만, 성장 카드 비용을 지키고도
    ///     남는 여유가 있을 때만 상자로 향한다 — 석을 하트에 다 태워 투자를 굶는 사고 방지.
    /// </summary>
    private int GetSwarmBotExploreCost(long matchingId, long botPlayerId) =>
        GetSwarmGrowthCostBreakdown(matchingId, botPlayerId).FinalCost + Config.SWARM_BOX_OPEN_COST;

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

        // 보스 공격 연출 (#223): 고정 포대의 원거리 타격은 투사체로 보여야 읽힌다 —
        // 같은 구역 전원에게 공격 VFX를 쏘고, 클라가 보스 여부(피통)로 투사체를 그린다.
        if (_swarmArenaManager.IsBossMonster(matchingId, damage.MonsterId))
        {
            using var vfxPacket = Packet.Create((int)Protocol.G_TO_C_MONSTER_ATTACK_VFX);
            vfxPacket.SetBody(MessagePackSerializer.Serialize(new G_TO_C_MONSTER_ATTACK_VFX
            {
                MonsterId = damage.MonsterId,
                TargetPlayerId = damage.TargetPlayerId,
                AreaType = damage.Area
            }));
            foreach (var vfxSession in allSessions)
            {
                if (!vfxSession.IsEliminated && vfxSession.CurrentArea == damage.Area)
                    vfxSession.Send(vfxPacket);
            }
        }

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
            // 몹 피격도 "맞는 중"이다 (#223, 매치 2453 -90): 스탬프가 없으면 보스 링 안에서
            // 채집을 계속하다 27초간 포격당한다 — 홀드를 풀어 몹 회피 반사(최우선)가 잡게 한다.
            _swarmBotLastDamagedAtUtc[(matchingId, bot.PlayerId)] = DateTime.UtcNow;
            bot.LastDamagedAtUtc = DateTime.UtcNow;
            bot.CancelInteractionHold();

            var botHit = ApplySwarmOrbHpDamage(matchingId, bot.PlayerId, damage.Damage);
            if (botHit.DestroyedItem != null)
                ScatterSwarmOrbBreakStones(
                    matchingId, botHit.DestroyedItem.ItemId, bot.CurrentArea,
                    bot.Position.X, bot.Position.Y, allSessions,
                    hadDurabilityBonus: botHit.HadDurabilityBonus);
            return;
        }

        int botDamage = Math.Max(1, (int)(damage.Damage * SwarmBotContactDamageMultiplier));
        bot.Corruption = Math.Min(Config.SURVIVOR_MAX_CORRUPTION, bot.Corruption + botDamage);
        _swarmBotLastDamagedAtUtc[(matchingId, bot.PlayerId)] = DateTime.UtcNow;
    }

    // 빈손 본체 유효 HP = T1 오브 두 개 값 (#223 재상향): T1 한 개 값(×17.5)은 후반 T3
    // 앞에서 2~3발 0.2초 증발이었다(매치 2403 +262×2 · 2404 +455 한 방) — 읽고 도망칠
    // 시간이 없는 죽음은 전투를 관전으로 만든다. 두 개 값(×8.75)이면 빈손 도주 창이
    // 2~3초 생기고, 재기는 여전히 도주 지시(0.5단계)·빈손 이속·무료 개봉이 만든다.
    private static readonly float SwarmNakedCorruptionPerDamage =
        Config.SURVIVOR_MAX_CORRUPTION / (float)(SurvivorOrbData.GetSquadOrbMaxHp(1) * 2);

    private static int GetSwarmNakedCorruption(int damage) =>
        Math.Max(1, (int)MathF.Round(damage * SwarmNakedCorruptionPerDamage));

    /// <summary>
    ///     사람 피격 (유닛 낱개 체력): 오브 HP 차감 → 0이면 파괴 + 인벤 동기화, 궤도가 비면 버스트.
    ///     빈손이면 플레이어 본체(오염 게이지)가 닳고, 만충이면 기존 탈락 파이프라인을 탄다.
    ///     피격 연출·탈락은 기존 잔상 피격 경로를 재사용한다 (오염은 연출용 1, 표시는 실제 피해량).
    /// </summary>
    private void ApplySwarmSquadOrbHit(
        long matchingId, GameClientSession session, int monsterId, int damage,
        List<GameClientSession> allSessions, long attackerPlayerId = 0)
    {
        if (!session.PlayerId.HasValue)
            return;

        if (!HasAnySquadOrb(matchingId, session.PlayerId.Value))
        {
            // PvP(monsterId=0)는 몬스터 피격 경로의 monsterId 가드에 걸려 증발했다 (#222 수리)
            // — 오염만 직접 반영한다. 피격 연출은 PvP VFX 브로드캐스트가 이미 담당한다.
            // 공격자 전달 (#223): 빈손 PvP 킬이 by=0 · src=mental로 남던 크레딧 증발 수리.
            if (monsterId > 0)
                session.ApplyEmotionAfterimageMonsterHit(monsterId, GetSwarmNakedCorruption(damage));
            else
                session.ModifyStats(corruptionDelta: GetSwarmNakedCorruption(damage),
                    attackerPlayerId: attackerPlayerId);
            return;
        }

        var hit = ApplySwarmOrbHpDamage(matchingId, session.PlayerId.Value, damage);
        if (hit.DestroyedItem != null)
        {
            session.SendInGameInventoryUpdate(hit.DestroyedItem);
            ScatterSwarmOrbBreakStones(
                matchingId, hit.DestroyedItem.ItemId, session.CurrentArea,
                session.LastValidatedPosition.X, session.LastValidatedPosition.Y, allSessions,
                hadDurabilityBonus: hit.HadDurabilityBonus);
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

    /// <summary>본체 오염 조회 (#226 가시화) — 세션·봇 공통. 못 찾으면 -1(클라 표시 유지).</summary>
    private int GetSwarmBodyCorruption(
        long matchingId, long playerId, IReadOnlyCollection<GameClientSession> matchingSessions)
    {
        foreach (var session in matchingSessions)
        {
            if (session.PlayerId == playerId)
                return session.CurrentCorruption;
        }

        foreach (var bot in _botPlayerManager.GetBots(matchingId))
        {
            if (bot.PlayerId == playerId)
                return bot.Corruption;
        }

        return -1;
    }

    /// <summary>
    ///     5분 점수 만료 판정 (#226 단계 B): 개전 후 5분이 지나면 생존자 중 오브 최다
    ///     보유자가 승리한다. 동점은 총 티어 합 → (철갑, 단계 C 예정) → 본체 게이지(오염
    ///     낮은 쪽) → PlayerId 낮은 쪽. 단독 생존 조기 종료와 같은 TryEndSurvivorMatch
    ///     경로라 결과 화면도 같다. 잼 승점(#222 M3-2)은 퇴역.
    /// </summary>
    private void ProcessSwarmScoreTimeout(
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
                Corruption: session.CurrentCorruption))
            .Concat(aliveBots.Select(bot => (bot.PlayerId, bot.Corruption)))
            .Select(candidate =>
            {
                var (orbCount, tierSum) = GetSwarmOrbScore(matchingId, candidate.PlayerId);
                return (candidate.PlayerId, OrbCount: orbCount, TierSum: tierSum,
                    candidate.Corruption);
            })
            .OrderByDescending(candidate => candidate.OrbCount)
            .ThenByDescending(candidate => candidate.TierSum)
            // 철갑(내구 보너스 합) 3차 키 (#226): 같은 열이면 방어 투자한 쪽이 앞선다.
            .ThenByDescending(candidate => _swarmOrbDurabilityBonus
                .Where(pair => pair.Key.MatchingId == matchingId &&
                               pair.Key.PlayerId == candidate.PlayerId)
                .Sum(pair => pair.Value))
            .ThenBy(candidate => candidate.Corruption)
            .ThenBy(candidate => candidate.PlayerId)
            .ToList();
        long winnerId = candidates.Count > 0 ? candidates[0].PlayerId : 0;
        _swarmTimeoutEndedMatchings.Add(matchingId);
        // 최종 점수표 (#226 F 계측): 순위 순 pid:오브:티어합 — 300초 목표(1위 11~15) 검증 근거.
        _gameEventLogManager.LogSystem(
            matchingId,
            "match_score_result " + string.Join(",", candidates.Select(candidate =>
                $"{candidate.PlayerId}:{candidate.OrbCount}:{candidate.TierSum}")));
        logger.LogInformation(
            "Swarm score timeout: MatchingId={MatchingId}, WinnerId={WinnerId}, WinnerOrbs={WinnerOrbs}, WinnerTierSum={WinnerTierSum}, Alive={AliveCount}",
            matchingId, winnerId,
            candidates.Count > 0 ? candidates[0].OrbCount : 0,
            candidates.Count > 0 ? candidates[0].TierSum : 0,
            candidates.Count);

        var resultHost = sessions.FirstOrDefault(session => !session.IsGameEnded);
        if (resultHost != null)
        {
            resultHost.TryEndSurvivorMatch(winnerId, "orb_score_timeout");
            CleanupSurvivorSettlementState(matchingId);
            return;
        }

        EndBotOnlyMatchIfSettled(matchingId, winnerId);
    }

    /// <summary>
    ///     오브 점수 헬퍼 (#226 단계 B): 궤도 열의 오브 수와 총 티어 합. 사람·봇 공통
    ///     (봇도 같은 인게임 인벤토리를 쓴다).
    /// </summary>
    private (int OrbCount, int TierSum) GetSwarmOrbScore(long matchingId, long playerId)
    {
        var inventory = _inGameInventoryManager.GetPlayerInventory(matchingId, playerId);
        int orbCount = 0;
        int tierSum = 0;
        foreach (var item in inventory.GetAllItems())
        {
            int tier = GetSquadOrbTier(item.ItemId);
            if (item.Count <= 0 || tier <= 0)
                continue;
            // 레거시 스택(같은 색·티어가 한 항목) 호환 — 항목이 아니라 수량이 오브 수다.
            orbCount += item.Count;
            tierSum += tier * item.Count;
        }

        return (orbCount, tierSum);
    }

    /// <summary>
    ///     오브 순위 브로드캐스트 (#226 단계 B): 오브 수가 곧 점수다. 패킷은 잼 순위 시절의
    ///     G_TO_C_JAM_RANKINGS를 그대로 쓰되 수치가 오브 수로 바뀌었다 — 클라 RankDisplay가
    ///     이름·수치·본인 순위를 그대로 비춘다.
    /// </summary>
    private void BroadcastSwarmOrbRankings(
        long matchingId, List<GameClientSession> sessions, List<BotPlayerState> bots)
    {
        var entries = sessions
            .Where(session => session.PlayerId.HasValue)
            .Select(session => (PlayerId: session.PlayerId!.Value,
                Eliminated: session.IsEliminated))
            .Concat(bots.Select(bot => (bot.PlayerId, Eliminated: bot.IsEliminated)))
            .Select(entry =>
            {
                var (orbCount, tierSum) = GetSwarmOrbScore(matchingId, entry.PlayerId);
                // 탈락자는 0점 — 순위표에서 자연히 바닥으로 내려간다.
                return (entry.PlayerId,
                    Orbs: entry.Eliminated ? 0 : orbCount,
                    TierSum: entry.Eliminated ? 0 : tierSum);
            })
            .OrderByDescending(entry => entry.Orbs)
            .ThenByDescending(entry => entry.TierSum)
            .ThenBy(entry => entry.PlayerId)
            .ToList();
        if (entries.Count == 0 || sessions.Count == 0)
            return;

        string signature = string.Join("|", entries.Select(entry => $"{entry.PlayerId}:{entry.Orbs}"));
        bool isFirstBroadcast = !_swarmJamRankingsSignature.TryGetValue(matchingId, out var previous);
        if (!isFirstBroadcast && previous == signature)
            return;

        _swarmJamRankingsSignature[matchingId] = signature;
        if (isFirstBroadcast)
            logger.LogInformation(
                "Orb rankings broadcast armed: MatchingId={MatchingId}, Participants={Count}, Sessions={Sessions}",
                matchingId, entries.Count, sessions.Count);
        var message = new G_TO_C_JAM_RANKINGS
        {
            PlayerIds = entries.Select(entry => entry.PlayerId).ToList(),
            JamCounts = entries.Select(entry => entry.Orbs).ToList()
        };
        using var packet = Packet.Create((int)Protocol.G_TO_C_JAM_RANKINGS);
        packet.SetBody(MessagePackSerializer.Serialize(message));
        foreach (var session in sessions)
            session.Send(packet);
    }

    // ===== 성장 카드 3택 (#226 단계 C) =====
    // 소환석이 카드 비용에 도달하면 즉시 오퍼가 뜬다 (상자 개방 트리거 퇴역).
    // 카드: 0=증식(무작위 T1 +1) · 1=강화(선두 T1→T2, 없으면 T2→T3) · 2=철갑(선두 무외피 오브).
    private const double SwarmGrowthOfferCooldownSeconds = 3d;
    private const int SwarmGrowthCardMultiply = 0;
    private const int SwarmGrowthCardEnhance = 1;
    private const int SwarmGrowthCardArmor = 2;

    /// <summary>오퍼 시점에 확정되는 카드 구성 (#226 C 등급): 픽은 이 서술자를 그대로 집행한다.</summary>
    private readonly record struct SwarmGrowthOfferState(
        int OfferId, int Cost, int SpawnItemId, int EnhanceTargetTier, int ArmorCount);

    private readonly Dictionary<(long MatchingId, long PlayerId), SwarmGrowthOfferState>
        _swarmGrowthOffers = new();
    private readonly Dictionary<(long MatchingId, long PlayerId), DateTime> _swarmGrowthNextOfferAtUtc = new();
    // 오브 내구 보너스 (#226 방어 강화 = 내구 모델, 2026-08-12): 기본 내구 1 + 보너스.
    // 내구 2+ 오브는 밟혀도 금만 가고(크랙 재활성), 내구만큼 밟혀야 절단된다.
    // 파괴·매치 정리에서 함께 지운다. 크랙은 유지된다(방어 강화가 균열을 지우지 않는다).
    private readonly Dictionary<(long MatchingId, long PlayerId, long ItemUid), int>
        _swarmOrbDurabilityBonus = new();
    private int _nextSwarmGrowthOfferId = 1;

    /// <summary>열 순서의 오브 목록 — 강화·철갑의 "가장 앞" 판정과 트레일 순번의 단일 출처.</summary>
    private List<InGameItemInfo> GetSwarmTrailOrbs(long matchingId, long playerId) =>
        _inGameInventoryManager.GetPlayerInventory(matchingId, playerId)
            .GetAllItems()
            .Where(item => item.Count > 0 && GetSquadOrbTier(item.ItemId) > 0)
            // uid 오름차순 = 열 순번·링 마스크·카드 대상의 단일 정렬 (2026-08-12 통일)
            .OrderBy(item => item.ItemUid)
            .ToList();

    /// <summary>강화 대상 티어: 선두 T1이 있으면 1(T1→T2), 없으면 선두 T2 기준 2, 전부 T3면 0.</summary>
    private int GetSwarmEnhanceTargetTier(long matchingId, long playerId)
    {
        var orbs = GetSwarmTrailOrbs(matchingId, playerId);
        if (orbs.Any(item => GetSquadOrbTier(item.ItemId) == 1))
            return 1;
        return orbs.Any(item => GetSquadOrbTier(item.ItemId) == 2) ? 2 : 0;
    }

    /// <summary>
    ///     방어 강화(내구 2+) 오브 순번 마스크 — 클라 은백 링 표시용 (#226).
    ///     순서는 비주얼 브로드캐스트의 OrbItemIds와 동일한 ItemUid 오름차순 — 슬롯 인덱스 정합.
    /// </summary>
    private long GetSwarmArmorMask(long matchingId, long playerId)
    {
        long mask = 0;
        var orbs = GetSwarmTrailOrbs(matchingId, playerId);
        for (int ordinal = 0; ordinal < orbs.Count && ordinal < 64; ordinal++)
            if (_swarmOrbDurabilityBonus.ContainsKey((matchingId, playerId, orbs[ordinal].ItemUid)))
                mask |= 1L << ordinal;
        return mask;
    }

    // 방어 강화 유효 대상 = 미강화(내구 보너스 0) 오브 — 전부 강화 상태면 비활성.
    private bool HasSwarmArmorTarget(long matchingId, long playerId) =>
        GetSwarmTrailOrbs(matchingId, playerId)
            .Any(item => !_swarmOrbDurabilityBonus.ContainsKey((matchingId, playerId, item.ItemUid)));

    /// <summary>
    ///     성장 비용 3요소 (#226 C 잔여): 기본 = 3+floor(N/3), 할증 = 오브 수 구간,
    ///     최종 = min(10, 기본+할증). 카드 품질은 기본에만 연동 — 선두가 할증을 냈다는
    ///     이유로 더 좋은 카드까지 받지 않는다. 0오브는 최종 3(재건 보장).
    /// </summary>
    private (int BaseCost, int Surcharge, int FinalCost, int OrbCount) GetSwarmGrowthCostBreakdown(
        long matchingId, long playerId)
    {
        var (orbCount, _) = GetSwarmOrbScore(matchingId, playerId);
        int growthCount = _summonStoneManager.GetGrowthSuccessCount(matchingId, playerId);
        return (
            Config.GetSwarmGrowthBaseCost(growthCount),
            Config.GetSwarmGrowthScoreSurcharge(orbCount),
            Config.GetSwarmGrowthCardCost(growthCount, orbCount),
            orbCount);
    }

    /// <summary>
    ///     오퍼 구성 확정 (#226 C 등급): 품질은 기본 비용 구간이 정한다.
    ///     오브 생성 = 색 균등 + 티어(기본 5+ T2 20% · 기본 7+ T2 35%/T3 10%),
    ///     공격 강화 = 선두 유효 대상 티어(I=T1→T2, II=T2→T3),
    ///     방어 강화 = 외피 장수(기본 1, 기본 5+ 15% · 기본 7+ 30% 확률로 2 — 무외피 수 캡).
    ///     0오브(재건 보장): 비용 3의 T1 생성만 유효 — 이 재건도 N에 포함된다.
    /// </summary>
    private SwarmGrowthOfferState GenerateSwarmGrowthOffer(
        long matchingId, long playerId, int finalCost, int qualityCost, int orbCount)
    {
        if (orbCount <= 0)
        {
            int rebuildItemId = SwarmStartingOrbPool[Random.Shared.Next(SwarmStartingOrbPool.Length)];
            return new SwarmGrowthOfferState(
                _nextSwarmGrowthOfferId++, finalCost, rebuildItemId,
                EnhanceTargetTier: 0, ArmorCount: 0);
        }

        int spawnTier = 1;
        int tierRoll = Random.Shared.Next(100);
        if (qualityCost >= 7 && tierRoll < 10)
            spawnTier = 3;
        else if (qualityCost >= 7 && tierRoll < 45)
            spawnTier = 2;
        else if (qualityCost >= 5 && tierRoll < 20)
            spawnTier = 2;
        int spawnItemId =
            SwarmStartingOrbPool[Random.Shared.Next(SwarmStartingOrbPool.Length)] + spawnTier - 1;

        int armorSlots = GetSwarmTrailOrbs(matchingId, playerId)
            .Count(item => !_swarmOrbDurabilityBonus.ContainsKey((matchingId, playerId, item.ItemUid)));
        int armorCount = armorSlots <= 0 ? 0 : 1;
        if (armorCount > 0 && armorSlots >= 2)
        {
            int armorRoll = Random.Shared.Next(100);
            if (qualityCost >= 7 && armorRoll < 30 || qualityCost >= 5 && armorRoll < 15)
                armorCount = 2;
        }

        return new SwarmGrowthOfferState(
            _nextSwarmGrowthOfferId++,
            finalCost,
            spawnItemId,
            GetSwarmEnhanceTargetTier(matchingId, playerId),
            armorCount);
    }

    /// <summary>
    ///     성장 오퍼 틱: 사람은 소환석이 비용에 닿는 즉시 오퍼 패킷(3택), 봇은 같은 규칙으로
    ///     즉시 자동 투자. 선택·적용 후 3초 쿨이 지나야 다음 오퍼가 뜬다.
    /// </summary>
    private void ProcessSwarmGrowthOffers(
        long matchingId, DateTime nowUtc,
        List<GameClientSession> aliveSessions, List<BotPlayerState> aliveBots)
    {
        foreach (var session in aliveSessions)
        {
            if (!session.PlayerId.HasValue)
                continue;
            long playerId = session.PlayerId.Value;
            var key = (matchingId, playerId);
            if (_swarmGrowthOffers.ContainsKey(key))
                continue;
            if (_swarmGrowthNextOfferAtUtc.TryGetValue(key, out var nextAtUtc) && nowUtc < nextAtUtc)
                continue;

            var (baseCost, surcharge, finalCost, orbCount) =
                GetSwarmGrowthCostBreakdown(matchingId, playerId);
            if (_summonStoneManager.GetSnapshot(matchingId, playerId).StoneCount < finalCost)
                continue;

            var offer = GenerateSwarmGrowthOffer(matchingId, playerId, finalCost, baseCost, orbCount);
            _swarmGrowthOffers[key] = offer;
            _gameEventLogManager.LogSwarmGrowthOffered(
                matchingId, playerId, isBot: false, baseCost, surcharge, finalCost, orbCount);
            session.SendSwarmGrowthOffer(
                offer.OfferId, offer.Cost, offer.SpawnItemId, offer.EnhanceTargetTier, offer.ArmorCount);
        }

        foreach (var bot in aliveBots)
        {
            if (bot.IsSwarmCutDummy)
                continue;
            var key = (matchingId, bot.PlayerId);
            if (_swarmGrowthNextOfferAtUtc.TryGetValue(key, out var nextAtUtc) && nowUtc < nextAtUtc)
                continue;

            var (baseCost, surcharge, finalCost, orbCount) =
                GetSwarmGrowthCostBreakdown(matchingId, bot.PlayerId);
            if (_summonStoneManager.GetSnapshot(matchingId, bot.PlayerId).StoneCount < finalCost)
                continue;

            var offer = GenerateSwarmGrowthOffer(matchingId, bot.PlayerId, finalCost, baseCost, orbCount);
            int cardIndex = ChooseSwarmBotGrowthCard(
                matchingId, bot, offer, orbCount, aliveSessions, aliveBots);
            bool applied = ApplySwarmGrowthCard(matchingId, bot.PlayerId, cardIndex, offer, session: null);
            _swarmGrowthNextOfferAtUtc[key] = nowUtc.AddSeconds(SwarmGrowthOfferCooldownSeconds);
            if (applied)
            {
                int successCountBefore = _summonStoneManager.GetGrowthSuccessCount(matchingId, bot.PlayerId);
                _summonStoneManager.RecordGrowthSuccess(matchingId, bot.PlayerId);
                _gameEventLogManager.LogSwarmGrowthSelected(
                    matchingId, bot.PlayerId, isBot: true,
                    GetSwarmGrowthCardRole(cardIndex), GetSwarmGrowthCardGrade(cardIndex, offer),
                    baseCost, surcharge, finalCost, successCountBefore, orbCount);
                logger.LogInformation(
                    "Swarm bot growth: MatchingId={MatchingId}, BotId={BotId}, Card={Card}, Base={Base}, Surcharge={Surcharge}, Cost={Cost}, Orbs={Orbs}",
                    matchingId, bot.PlayerId, cardIndex, baseCost, surcharge, finalCost, orbCount);
            }
        }
    }

    /// <summary>
    ///     봇 투자 정책 (#226 F 상황 판단): 큰 점수 열세(선두와 3+ 격차)는 생성 몰빵,
    ///     선두는 방어(절단 = 상대의 유일한 역전 수단), 처치각(같은 구역 약자)은 공격 강화,
    ///     그 외 기존 45/30/25. 무효 카드는 생성으로 대체. 초반(오브 5 미만)은 생성 고정 —
    ///     발사점·점수가 곧 생존이다.
    /// </summary>
    private int ChooseSwarmBotGrowthCard(
        long matchingId, BotPlayerState bot, SwarmGrowthOfferState offer, int orbCount,
        List<GameClientSession> aliveSessions, List<BotPlayerState> aliveBots)
    {
        if (orbCount < 5)
            return SwarmGrowthCardMultiply;

        int topOrbCount = GetSwarmTopOrbCount(matchingId);
        bool isLeader = orbCount >= topOrbCount;
        int leaderGap = topOrbCount - orbCount;
        bool hasPrey = HasSwarmPreyInArea(matchingId, bot, aliveSessions, aliveBots);

        var (multiply, enhance) = leaderGap >= 3 ? (70, 20) :
            isLeader ? (35, 20) :
            hasPrey ? (35, 45) : (45, 30);

        int roll = Random.Shared.Next(100);
        if (roll < multiply)
            return SwarmGrowthCardMultiply;
        if (roll < multiply + enhance)
            return offer.EnhanceTargetTier > 0 ? SwarmGrowthCardEnhance : SwarmGrowthCardMultiply;
        return offer.ArmorCount > 0 ? SwarmGrowthCardArmor : SwarmGrowthCardMultiply;
    }

    /// <summary>생존자 최다 오브 수 — 봇 성장·추격 판단의 순위 기준.</summary>
    private int GetSwarmTopOrbCount(long matchingId)
    {
        int top = 0;
        foreach (var session in _clientSessions.Values)
        {
            if (session.PlayerId.HasValue && session.CurrentMapSubId == matchingId &&
                !session.IsEliminated)
                top = Math.Max(top, GetSwarmOrbScore(matchingId, session.PlayerId.Value).OrbCount);
        }

        foreach (var bot in _botPlayerManager.GetBots(matchingId))
        {
            if (!bot.IsEliminated && !bot.IsSwarmCutDummy)
                top = Math.Max(top, GetSwarmOrbScore(matchingId, bot.PlayerId).OrbCount);
        }

        return top;
    }

    /// <summary>처치각 판독 (#226 F): 같은 구역에 확실히 약한(전력 ×1.25 미만) 적이 있는가.</summary>
    private bool HasSwarmPreyInArea(
        long matchingId, BotPlayerState bot,
        List<GameClientSession> aliveSessions, List<BotPlayerState> aliveBots)
    {
        float myPower = GetSwarmSquadPower(matchingId, bot.PlayerId);
        if (myPower <= 0f)
            return false;

        foreach (var session in aliveSessions)
        {
            if (session.PlayerId.HasValue && session.CurrentArea == bot.CurrentArea &&
                GetSwarmSquadPower(matchingId, session.PlayerId.Value) *
                SwarmBotChasePowerAdvantage <= myPower)
                return true;
        }

        foreach (var other in aliveBots)
        {
            if (other.PlayerId != bot.PlayerId && !other.IsSwarmCutDummy &&
                other.CurrentArea == bot.CurrentArea &&
                GetSwarmSquadPower(matchingId, other.PlayerId) *
                SwarmBotChasePowerAdvantage <= myPower)
                return true;
        }

        return false;
    }

    /// <summary>계측용 카드 역할 라벨 (#226 F) — 요약의 역할 분포 집계가 이 문자열을 센다.</summary>
    private static string GetSwarmGrowthCardRole(int cardIndex) => cardIndex switch
    {
        SwarmGrowthCardMultiply => "multiply",
        SwarmGrowthCardEnhance => "enhance",
        SwarmGrowthCardArmor => "armor",
        _ => "unknown"
    };

    /// <summary>계측용 카드 등급 (#226 F) — 생성=지급 티어, 공격=강화 대상 티어(I/II), 방어=내구 증가량(I/II).</summary>
    private static int GetSwarmGrowthCardGrade(int cardIndex, SwarmGrowthOfferState offer)
    {
        switch (cardIndex)
        {
            case SwarmGrowthCardMultiply:
                return SurvivorOrbData.TryGetColorAndTier(offer.SpawnItemId, out _, out int tier)
                    ? tier
                    : 1;
            case SwarmGrowthCardEnhance:
                return offer.EnhanceTargetTier;
            case SwarmGrowthCardArmor:
                return offer.ArmorCount;
            default:
                return 0;
        }
    }

    /// <summary>성장 카드 선택 처리 — 세션 라우팅 콜백의 종착지. 실패 시 오퍼는 유지된다.</summary>
    internal void HandleSwarmGrowthPick(GameClientSession session, long matchingId, int offerId, int cardIndex)
    {
        if (!session.PlayerId.HasValue || matchingId <= 0)
            return;

        long playerId = session.PlayerId.Value;
        var key = (matchingId, playerId);
        if (!_swarmGrowthOffers.TryGetValue(key, out var offer) || offer.OfferId != offerId)
        {
            session.SendSwarmGrowthResult(offerId, cardIndex, success: false);
            return;
        }

        // 선택 시점 상태 (#226 F 계측): 비용 분해·오브 수는 적용 전 값을 남긴다.
        var (baseCost, surcharge, _, orbCountBefore) = GetSwarmGrowthCostBreakdown(matchingId, playerId);
        bool success = ApplySwarmGrowthCard(matchingId, playerId, cardIndex, offer, session);
        if (success)
        {
            _swarmGrowthOffers.Remove(key);
            _swarmGrowthNextOfferAtUtc[key] = DateTime.UtcNow.AddSeconds(SwarmGrowthOfferCooldownSeconds);
            // N 누적 (#226 C 잔여): 성공한 선택만 — 실패(재검증 탈락)는 비용 곡선을 밀지 않는다.
            int successCountBefore = _summonStoneManager.GetGrowthSuccessCount(matchingId, playerId);
            _summonStoneManager.RecordGrowthSuccess(matchingId, playerId);
            _gameEventLogManager.LogSwarmGrowthSelected(
                matchingId, playerId, isBot: false,
                GetSwarmGrowthCardRole(cardIndex), GetSwarmGrowthCardGrade(cardIndex, offer),
                baseCost, surcharge, offer.Cost, successCountBefore, orbCountBefore);
        }

        session.SendSwarmGrowthResult(offerId, cardIndex, success);
        session.SendSummonStoneState();
        logger.LogInformation(
            "Swarm growth pick: MatchingId={MatchingId}, PlayerId={PlayerId}, Card={Card}, Cost={Cost}, Success={Success}",
            matchingId, playerId, cardIndex, offer.Cost, success);
    }

    /// <summary>
    ///     카드 효과 적용 (사람·봇 공통): 오퍼 시점에 확정된 서술자를 그대로 집행한다.
    ///     비용 차감이 성립할 때만 효과가 나가고, 픽 시점 재검증 실패면 오퍼가 유지된다.
    /// </summary>
    private bool ApplySwarmGrowthCard(
        long matchingId, long playerId, int cardIndex, SwarmGrowthOfferState offer,
        GameClientSession session)
    {
        int cost = offer.Cost;
        var inventory = _inGameInventoryManager.GetPlayerInventory(matchingId, playerId);
        switch (cardIndex)
        {
            case SwarmGrowthCardMultiply:
                {
                    if (inventory.GetAllItems().Count >= Config.SWARM_ORB_CAPACITY)
                        return false;
                    if (!_summonStoneManager.TrySpendStones(matchingId, playerId, cost, out _))
                        return false;
                    if (session != null)
                        session.GrantSwarmArenaOrb(offer.SpawnItemId);
                    else
                        inventory.TryAddItemWithCapacity(offer.SpawnItemId, Config.SWARM_ORB_CAPACITY, out _);
                    return true;
                }
            case SwarmGrowthCardEnhance:
                {
                    if (offer.EnhanceTargetTier is not (1 or 2))
                        return false;
                    var target = GetSwarmTrailOrbs(matchingId, playerId)
                        .FirstOrDefault(item => GetSquadOrbTier(item.ItemId) == offer.EnhanceTargetTier);
                    if (target == null)
                        return false;
                    if (!_summonStoneManager.TrySpendStones(matchingId, playerId, cost, out _))
                        return false;
                    if (!inventory.TryUpgradeSurvivorOrb(target.ItemUid, out _))
                        return false;
                    session?.SendInGameInventoryUpdate(target);
                    return true;
                }
            case SwarmGrowthCardArmor:
                {
                    if (offer.ArmorCount <= 0)
                        return false;
                    var targets = GetSwarmTrailOrbs(matchingId, playerId)
                        .Where(item =>
                            !_swarmOrbDurabilityBonus.ContainsKey((matchingId, playerId, item.ItemUid)))
                        .Take(offer.ArmorCount)
                        .ToList();
                    if (targets.Count == 0)
                        return false;
                    if (!_summonStoneManager.TrySpendStones(matchingId, playerId, cost, out _))
                        return false;
                    foreach (var target in targets)
                        _swarmOrbDurabilityBonus[(matchingId, playerId, target.ItemUid)] =
                            SwarmArmorDurabilityBonus;
                    return true;
                }
            default:
                return false;
        }
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

    private (InGameItemInfo DestroyedItem, bool Busted, bool HadDurabilityBonus) ApplySwarmOrbHpDamage(
        long matchingId, long playerId, int damage)
    {
        var inventory = _inGameInventoryManager.GetPlayerInventory(matchingId, playerId);
        var frontOrb = FindSwarmFrontOrb(matchingId, playerId);
        // 빈손(#219 M2 빈손 시작)은 스쿼드가 없으니 스쿼드 피해도 버스트도 없다 —
        // 버스트는 "마지막 유닛을 잃는 타격"에만 성립한다. 빈손 즉사 사고 방지.
        if (frontOrb == null)
            return (null, false, false);

        var key = (matchingId, playerId);
        int currentHp = _swarmFrontOrbHp.TryGetValue(key, out var stored) && stored.ItemId == frontOrb.ItemId
            ? stored.Hp
            : GetSquadOrbMaxHp(GetSquadOrbTier(frontOrb.ItemId));
        currentHp -= damage;
        if (currentHp > 0)
        {
            _swarmFrontOrbHp[key] = (frontOrb.ItemId, currentHp);
            return (null, false, false);
        }

        _swarmFrontOrbHp.Remove(key);
        inventory.TryRemoveItem(frontOrb.ItemUid, 1, out var destroyedItem);
        // 절단 외 파괴(몹 접촉)도 내구·크랙 상태를 함께 정리한다 — 방어 투자분은 낙수 +1로 정산.
        _swarmOrbCutCracks.Remove((matchingId, playerId, frontOrb.ItemUid));
        bool hadDurabilityBonus =
            _swarmOrbDurabilityBonus.Remove((matchingId, playerId, frontOrb.ItemUid));
        return (destroyedItem, !HasAnySquadOrb(matchingId, playerId), hadDurabilityBonus);
    }

    /// <summary>
    ///     오브 파괴 낙수 (#226 D 정산): 깨진 오브는 소환석으로만 흩어진다 — 승자의 전리품이자
    ///     도망친 주인의 회수 기회. T1/2/3 = 1/2/3, 방어 강화(내구 2+) 오브는 +1.
    ///     잼 낙수는 잼 승점 퇴역과 함께 제거 — 승점은 오브 수 하나로 통일한다.
    /// </summary>
    private static int GetSwarmOrbBreakStoneCount(int tier) => Math.Clamp(tier, 1, 3);

    private void ScatterSwarmOrbBreakStones(
        long matchingId,
        int destroyedItemId,
        AreaType area,
        float x,
        float y,
        List<GameClientSession> sessions,
        bool hadDurabilityBonus = false)
    {
        int destroyedTier = GetSquadOrbTier(destroyedItemId);
        int stoneCount = GetSwarmOrbBreakStoneCount(destroyedTier) + (hadDurabilityBonus ? 1 : 0);
        if (stoneCount <= 0 || area == AreaType.None)
            return;

        var itemIds = Enumerable.Repeat(Config.SUMMON_STONE_GROUND_ITEM_ID, stoneCount)
            .ToArray();
        var spawned = _groundItemManager.SpawnItems(
            matchingId, area, x, y, itemIds,
            mapId: MapId.School,
            layout: GroundItemSpawnLayout.EliminationScatter);
        if (spawned.Count == 0)
            return;

        int remaining = _areaItemStockManager.GetRemainingCount(matchingId, (int)area);
        using var packet = PacketMaker.G_TO_C_GROUND_ITEM_SPAWN((int)area, remaining, spawned.ToList());
        foreach (var session in sessions)
        {
            if (session.PlayerId.HasValue && session.CurrentArea == area)
                session.Send(packet);
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

    /// <summary>
    ///     PvP 미사일 적용 (#226 재개편): 오브 HP·본체 보호 퇴역 — 모든 발은 본체 오염으로
    ///     환산(이월 누산)되어 직행한다. 오브 파괴는 열 절단 전용. PvpDamageScale(0.65)은
    ///     오염 환산 상수가 대체하므로 퇴역.
    /// </summary>
    private int ApplySwarmPvpAttack(
        long matchingId,
        ProximityCombatAttack attack,
        List<GameClientSession> aliveSessions,
        List<BotPlayerState> aliveBots,
        List<GameClientSession> allSessions,
        bool broadcastVfx = true,
        bool aggregateEventFeedback = false,
        bool sendAttackerFeedback = true)
    {
        // 반격 보호 (#227 7단계): 방금 이 표적의 꼬리를 자른 공격자의 본체 피해는 통과하지 못한다.
        // 착탄 시점에 보므로 창이 열리기 '전에' 발사된 대기 투사체도 함께 걸린다.
        // 제3자·잔상·폐쇄는 이 경로를 타지 않아 종전대로 들어간다.
        var nowUtc = DateTime.UtcNow;
        if (_swarmCutRetaliationWindows.TryGetValue(
                (matchingId, attack.AttackerPlayerId, attack.TargetPlayerId), out var guardWindow) &&
            nowUtc < guardWindow.ExpiresAtUtc)
        {
            guardWindow.BlockedHits++;
            guardWindow.BlockedDamage += attack.Damage;
            // 잔광 앞에서 짧게 깨지는 연출만 — 피해 숫자·피격 눌림·인카운터 배너는 만들지 않는다.
            SendSwarmRetaliationVfx(
                attack.AttackerPlayerId, attack.TargetPlayerId, attack.Area,
                SwarmRingVfxKindRetaliationBlocked, 0f, allSessions);
            return 0;
        }

        int corruption = ConsumeSwarmPvpCorruption(matchingId, attack.TargetPlayerId, attack.Damage);
        var targetSession = aliveSessions.FirstOrDefault(session =>
            session.PlayerId == attack.TargetPlayerId);
        if (targetSession != null)
        {
            if (corruption > 0)
            {
                if (aggregateEventFeedback)
                    targetSession.ApplySwarmAttackEventHit(
                        attack.AttackerPlayerId, attack.Area, attack.WeaponItemId, corruption);
                else
                    targetSession.ApplyProximityAutoCombatHit(
                        attack.AttackerPlayerId, attack.Area, attack.WeaponItemId, corruption);
            }
        }
        else
        {
            var bot = aliveBots.FirstOrDefault(candidate => candidate.PlayerId == attack.TargetPlayerId);
            if (bot == null)
                return 0;

            // 오염이 0으로 이월돼도 "피격 중" 스탬프는 매 발 — 피격 반응 판단의 입력.
            bot.LastProximityAttackerPlayerId = attack.AttackerPlayerId;
            _swarmBotLastDamagedAtUtc[(matchingId, bot.PlayerId)] = DateTime.UtcNow;
            bot.LastDamagedAtUtc = DateTime.UtcNow;
            if (corruption > 0)
            {
                // 킬 크레딧 (#226 F 계측): 봇 표적도 사람 표적과 같은 피격 로그를 남긴다 —
                // 이게 빠지면 사람이 봇을 잡아도 killCount·totalDamageDealt가 0으로 남는다.
                _gameEventLogManager.LogSurvivorHit(
                    matchingId, attack.AttackerPlayerId, bot.PlayerId, attack.WeaponItemId,
                    corruption,
                    bot.Corruption < Config.SURVIVOR_MAX_CORRUPTION &&
                    bot.Corruption + corruption >= Config.SURVIVOR_MAX_CORRUPTION,
                    BotPlayerManager.IsBotPlayerId(attack.AttackerPlayerId), DateTimeOffset.UtcNow);
                bot.Corruption = Math.Min(Config.SURVIVOR_MAX_CORRUPTION, bot.Corruption + corruption);
            }
        }

        if (corruption > 0 && sendAttackerFeedback)
        {
            var attackerSession = allSessions.FirstOrDefault(session =>
                session.PlayerId == attack.AttackerPlayerId);
            if (aggregateEventFeedback)
                attackerSession?.SendSwarmAttackEventFeedback(
                    attack.TargetPlayerId, attack.Area, attack.WeaponItemId, corruption);
            else
                attackerSession?.SendProximityAutoCombatAttackFeedback(
                    attack.TargetPlayerId, attack.Area, attack.WeaponItemId, corruption);
        }
        // 태양 착탄(#226)은 발사 시점에 이미 연출을 쐈다 — 이중 투사체 방지.
        if (broadcastVfx)
            BroadcastSpotArenaAttackVfxToTargetAndObservers(attack, allSessions);
        return corruption;
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
        _pendingSwarmPvpHits.RemoveAll(hit => hit.MatchingId == matchingId);
        CleanupSwarmPvpAttackEvents(matchingId);
        foreach (var key in _swarmOrbTrails.Keys.Where(key => key.MatchingId == matchingId).ToList())
            _swarmOrbTrails.Remove(key);
        foreach (var key in _swarmTrailLastTickPositions.Keys
                     .Where(key => key.MatchingId == matchingId).ToList())
            _swarmTrailLastTickPositions.Remove(key);
        foreach (var key in _swarmPvpCorruptionCarry.Keys
                     .Where(key => key.MatchingId == matchingId).ToList())
            _swarmPvpCorruptionCarry.Remove(key);
        _swarmCutDummyAutoSetupDone.Remove(matchingId);
        foreach (var key in _swarmCutDummyRefillAtUtc.Keys
                     .Where(key => key.MatchingId == matchingId).ToList())
            _swarmCutDummyRefillAtUtc.Remove(key);
        foreach (var key in _swarmOrbCutLatches.Keys
                     .Where(key => key.MatchingId == matchingId).ToList())
            _swarmOrbCutLatches.Remove(key);
        foreach (var key in _swarmCutRetaliationWindows.Keys
                     .Where(key => key.MatchingId == matchingId).ToList())
            _swarmCutRetaliationWindows.Remove(key);
        foreach (var key in _swarmOrbCutCracks.Keys
                     .Where(key => key.MatchingId == matchingId).ToList())
            _swarmOrbCutCracks.Remove(key);
        foreach (var key in _swarmGrowthOffers.Keys
                     .Where(key => key.MatchingId == matchingId).ToList())
            _swarmGrowthOffers.Remove(key);
        foreach (var key in _swarmGrowthNextOfferAtUtc.Keys
                     .Where(key => key.MatchingId == matchingId).ToList())
            _swarmGrowthNextOfferAtUtc.Remove(key);
        foreach (var key in _swarmOrbDurabilityBonus.Keys
                     .Where(key => key.MatchingId == matchingId).ToList())
            _swarmOrbDurabilityBonus.Remove(key);
        foreach (var key in _swarmBotAreaMemory.Keys
                     .Where(key => key.MatchingId == matchingId).ToList())
            _swarmBotAreaMemory.Remove(key);
        foreach (var key in _swarmBotFleeDirective
                     .Where(key => key.MatchingId == matchingId).ToList())
            _swarmBotFleeDirective.Remove(key);
        foreach (var key in _swarmBotLastTrailCutAtUtc.Keys
                     .Where(key => key.MatchingId == matchingId).ToList())
            _swarmBotLastTrailCutAtUtc.Remove(key);
        foreach (var key in _swarmEncircleCandidateSinceUtc.Keys
                     .Where(key => key.MatchingId == matchingId).ToList())
            _swarmEncircleCandidateSinceUtc.Remove(key);
        foreach (var key in _swarmEncircleCooldownUtc.Keys
                     .Where(key => key.MatchingId == matchingId).ToList())
            _swarmEncircleCooldownUtc.Remove(key);
        foreach (var key in _swarmWaveBombNextDropAtUtc.Keys
                     .Where(key => key.MatchingId == matchingId).ToList())
            _swarmWaveBombNextDropAtUtc.Remove(key);
        _pendingSwarmWaveBombs.RemoveAll(bomb => bomb.MatchingId == matchingId);
        _swarmJamRankingsSignature.Remove(matchingId);
        _swarmTimeoutEndedMatchings.Remove(matchingId);
        _swarmMatchFallbackAnchorUtc.Remove(matchingId);
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
        // DEV_CUT_DUMMY 매치는 절단 궤적만 읽는 실험장이다. 서버에서 공격 액터를
        // 비무장으로 만들어 태양·바람 미사일과 실제 피해가 함께 발생하지 않게 한다.
        bool armed = !SwarmCutDummyAutoSetup &&
                     IsSwarmAttackArmed(matchingId, spatial.PlayerId, nowUtc) &&
                     !IsSwarmCutDummyPlayer(matchingId, spatial.PlayerId);
        // #226 표적 정책: 본체와 몬스터는 동급(2) — 최근접 우선 + 타겟 고정. 본체(1) 우선이던
        // 시절엔 구역에 적 플레이어가 있는 한 몹이 영영 표적이 안 돼 파밍이 죽었다.
        // 오브 액터는 발사 원점일 뿐 표적이 아니다(Untargetable).
        var fallback = CreateSwarmParticipantActor(spatial, armed) with
        {
            TargetPriority = 2
        };
        var inventory = _inGameInventoryManager.GetPlayerInventory(matchingId, spatial.PlayerId);
        if (!inventory.GetAllItems().Any(item => item.Count > 0))
        {
            // #219 M2 빈손 시작: 기본 공격 폴백 퇴역 — 빈손은 무기(가디언 오브 비주얼)도
            // 화력도 없고 피격 대상으로만 존재한다. 첫 화력은 드래프트에서 나온다.
            actors.Add(fallback with { WeaponItemId = 0, Damage = 0 });
            return;
        }

        // 본체 표적 액터 (#226 단계 B 수리): 오브 액터가 전원 Untargetable이 되면서, 오브
        // 보유자는 표적 액터가 리스트에 없었다 — PvP 미사일이 쏠 대상이 0개(태양·바람 침묵).
        // 본체는 무기 없는 순수 표적으로 항상 들어간다.
        actors.Add(fallback with { WeaponItemId = 0, Damage = 0 });

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
        // 색 = 무기 동사 (#226): 스탯 배율(태양 공격·바람 공속·파도 사거리)은 퇴역.
        // 태양=구역 전체 유도 단발(느림), 바람=약한 다발 총알(빠름, DPS는 태양 상회),
        // 파도=미사일 없음 — 물폭탄은 별도 주기 시스템(ProcessSwarmWaveBombs)이 맡는다.
        // 스팸 캡은 색별 오브 수 기준 (2026-08-12): 전체 수 기준이던 시절엔 태양을 들수록
        // 바람 연사까지 느려졌다 — 색별 주기는 서로 독립이어야 한다.
        int windActorCount = 0;
        for (int index = before; index < actors.Count; index++)
            if (SurvivorOrbData.TryGetColorAndTier(actors[index].WeaponItemId, out var countColor, out _) &&
                countColor == SurvivorOrbColor.Green)
                windActorCount++;
        int sunActorCount = 0;
        for (int index = before; index < actors.Count; index++)
            if (SurvivorOrbData.TryGetColorAndTier(actors[index].WeaponItemId, out var countColor, out _) &&
                countColor == SurvivorOrbColor.Red)
                sunActorCount++;
        var actorTiers = GetSwarmOrbTiersInOrder(matchingId, spatial.PlayerId);
        for (int index = before; index < actors.Count; index++)
        {
            var actor = actors[index];
            // 오브열 (#226 α+): 공격 원점·피격 위치 = 각 오브의 열 좌표 — 표시가 곧 판정.
            var trailPosition = GetSwarmOrbTrailPosition(
                matchingId, spatial.PlayerId, index - before, spatial.Position, actorTiers);
            actor = actor with
            {
                Position = trailPosition,
                Cell = ProximityCombatLineOfSight.WorldPositionToCell(MapId.School, trailPosition)
            };
            // 오브는 표적이 아니다 (#226 재개편): 발사 원점으로만 존재 — 파괴는 절단 전용.
            actor = actor with { Untargetable = true };
            SurvivorOrbData.TryGetColorAndTier(actor.WeaponItemId, out var orbColor, out _);
            if (orbColor == SurvivorOrbColor.Blue)
            {
                // 파도: 미사일을 쏘지 않는다 — 물폭탄(별도 주기)이 화력이다.
                actors[index] = actor with { Damage = 0 };
                continue;
            }

            bool isWind = orbColor == SurvivorOrbColor.Green;
            float colorDamageMultiplier = isWind
                ? SwarmWindBulletDamageMultiplier
                : SwarmSunBulletDamageMultiplier;
            float colorIntervalMultiplier = isWind
                ? SwarmWindBulletIntervalMultiplier
                : SwarmSunHomingIntervalMultiplier;
            float baseInterval =
                actor.AttackIntervalSeconds * SwarmOrbIntervalMultiplier * colorIntervalMultiplier;
            // 연사화 + 스팸 캡: 발당 데미지를 실제 주기 비율(interval/baseInterval)로 보정해
            // 오브별 DPS(원 데미지/원 주기 × 색 배율)를 보존한다 — 캡에 걸려도 유지.
            int colorActorCount = Math.Max(1, isWind ? windActorCount : sunActorCount);
            float interval = MathF.Max(
                baseInterval * SwarmOrbRapidFireScale,
                colorActorCount * SwarmOrbMinShotSpacingSeconds);
            float dpsScale = baseInterval > 0f ? interval / baseInterval : 1f;
            actors[index] = actor with
            {
                Damage = armed
                    ? Math.Max(1, (int)MathF.Round(
                        actor.Damage * SwarmOrbDamageMultiplier * colorDamageMultiplier * dpsScale))
                    : 0,
                AttackIntervalSeconds = interval,
                // 일제사격 (2026-08-12 유저 결정): 엇박 스태거 퇴역 — 전 오브가 같은 틱에
                // 발사된다. 조준 리셋마다 스태거를 재지불하던 케이던스 손실도 함께 사라진다.
                InitialAttackDelaySeconds = actor.InitialAttackDelaySeconds,
                // 이 공용 actor는 잔상 PvE에만 쓰인다. PvP 국소 사거리는 별도 공격 사건에서
                // 오브별 원점을 기준으로 판정하므로, PvE의 같은 구역 사냥 범위는 유지한다.
                AttackRange = SwarmPveSameAreaAttackRange
            };
        }
    }

    // 색 무기 파라미터 (#226): 태양 = 느린 직선탄(회피 가능·정지 처벌 — 맞으면 아프게 1.5배),
    // 바람 = 발당 40% × 주기 40%(다발 총알).
    //
    // 국소 화망 (#227 6단계): 30 → 6.0. 사거리 30은 구역 전체를 덮어 후미 절단과 머리
    // 절단의 위험이 같았다 — 어디를 자르든 상대의 모든 오브가 사정권이었기 때문이다.
    // 6.0이면 각 오브가 자기 열 좌표 주변만 덮으므로, 깊게 자를수록 앞열 여러 오브의
    // 사거리가 겹치는 자리로 들어가야 한다. 위험을 수치가 아니라 공간이 만든다.
    private const float SwarmSunAttackRange = 6f;
    // 바람 사거리 = 태양과 동일: 색 차이는 거리표가 아니라 리듬(연사 vs 한 방)과 탄속이 만든다.
    private const float SwarmWindAttackRange = SwarmSunAttackRange;
    private const float SwarmPveSameAreaAttackRange = 1000f;
    // 1.75 → 2.5 (2026-08-12): 태양 = 무겁고 느린 한 방 — 바람(연사 소탄)과 리듬 대비.
    private const float SwarmSunHomingIntervalMultiplier = 2.5f;
    // 1.5 → 3.0 (2026-08-12 로그 실측): 발당 오염 ~3.8은 "안 박히는" 체감 — 두 배로 묵직하게.
    private const float SwarmSunBulletDamageMultiplier = 3f;
    // 0.15 → 0.35 (2026-08-12 2차): DPS 보정(0.6) 끝에 발당 1로 바닥 — 태양 2배 상향 후
    // "바람 DPS가 태양을 상회한다(근접 리스크 프리미엄)" 정체성 복원. 발당 ~3, DPS 태양의 ~1.4배.
    private const float SwarmWindBulletDamageMultiplier = 0.35f;
    private const float SwarmWindBulletIntervalMultiplier = 0.14f;

    /// <summary>
    ///     아이소메트릭 타원 사거리: 이 맵의 월드 y는 셀 스케일이 x의 절반이라, 유클리드
    ///     원은 화면상 위아래로 과하게 길다. dy를 2배 보정한 타원(= 셀 공간 등거리)이
    ///     기울인 사거리 링(x회전 60°, cos=0.5)과 정확히 일치한다.
    /// </summary>
    private static bool IsWithinSwarmOrbRange(ProximityCombatActor attacker, ProximityCombatActor target)
    {
        // 파도 사거리 가산이 액터에 실려 온다 — 없으면(0) 기본 사거리.
        float range = attacker.AttackRange > 0f ? attacker.AttackRange : Config.SWARM_ORB_ATTACK_RANGE;
        return IsWithinSwarmOrbRange(attacker.Position, range, target.Position);
    }

    /// <summary>좌표판 — 화망 밀도 계측처럼 액터가 없는 자리에서도 같은 타원을 쓴다 (#227 6단계).</summary>
    private static bool IsWithinSwarmOrbRange(Vector3f origin, float range, Vector3f target)
    {
        float dx = target.X - origin.X;
        float dy = (target.Y - origin.Y) * 2f;
        return dx * dx + dy * dy <= range * range;
    }

    /// <summary>사격하는 오브(태양·바람)의 PvP 사거리 — 파도는 미사일을 쏘지 않아 0이다.</summary>
    private static float GetSwarmOrbPvpRange(int itemId)
    {
        if (!SurvivorOrbData.TryGetColorAndTier(itemId, out var color, out _))
            return 0f;
        return color switch
        {
            SurvivorOrbColor.Red => SwarmSunAttackRange,
            SurvivorOrbColor.Green => SwarmWindAttackRange,
            _ => 0f
        };
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
