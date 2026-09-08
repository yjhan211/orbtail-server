using System.Collections.Immutable;
using game_server.network;
using game_server.sessions;
using MessagePack;
using Microsoft.Extensions.Logging;
using network.common;
using network.common.data;
using network.common.data.models;
using network.packets;
using static game_server.network.SessionSnapshotDelivery;

namespace game_server.services;

/// <summary>
///     매치의 전투 틱 순서를 조율하고 꼬리 절단·파도·점수 만료·개발 샌드박스를 처리한다.
///     상태는 MatchRuntime이 소유하며 틱 호출자는 해당 매치 잠금을 보유한다.
///     봇 판단과 개별 무기·성장·피해 규칙은 각 서비스에 위임한다.
/// </summary>
internal sealed class MatchArenaService(
    MatchRuntimeStore matchRuntimes,
    GameServerDevOptions devOptions,
    GameEventLogManager eventLogs,
    MatchCleanupService matchCleanup,
    BotEliminationService botEliminations,
    MatchEliminationService matchEliminations,
    OrbUpgradeService orbUpgrades,
    MatchGrowthService growth,
    OrbRecoveryService orbRecovery,
    OrbVisualStatePublisher orbVisuals,
    OrbTrailService orbTrails,
    MatchCombatDamageService combatDamage,
    WindBladeService windBlades,
    CrossfireService crossfires,
    MatchFieldService fieldService,
    BotMovementService botMovement,
    BotDecisionService botDecisions,
    ILogger<MatchArenaService> logger)
{
    private const int SwarmArenaBasicDamage = 12;
    // 봇도 사람과 동일한 접촉 피해 규칙을 적용한다.
    private const float SwarmBotContactDamageMultiplier = 1f;
    // 사거리는 클라 표시(PlayerRangeRing)와 공유 — Config가 단일 출처다.
    private static float SwarmArenaBasicRange => Config.SWARM_ORB_ATTACK_RANGE;
    private const float SwarmArenaBasicAttackIntervalSeconds = 1f;
    private const int SwarmArenaWeaponItemId = 107000010;
    // 소환·시작 지급·재건에 동일한 공급 색 설정을 사용한다.
    private static readonly int[] SwarmStartingOrbPool = OrbUpgradeService.CreateStartingOrbPool();

    // 플레이어 단위 지급: 매칭 단위 1회 지급은 지급 틱에 아직 접속 전인 사람을 영영 빈손으로 만든다 —
    // 늦게 합류해도 첫 등장 틱에 각자 1회 받는다 (상태는 Pacing.StartingOrbGrantedPlayers).

    // 고위험 절단 (#232 무한 꼬리): 몸으로 상대 꼬리를 유효하게 가로지르면 밟은 지점부터 꼬리 끝까지(접미 전체)
    // 깨지고 나는 체력 35를 낸다. 크랙·방어 장갑·절단 낙수는 쓰지 않는다 (TryPerformSwarmTrailCut).
    // 끄려면 절단 계약 테스트(SwarmDamagePathTests.TailCut_RemovesSuffixAndChargesAttacker)의 플래그 어서션도 같이 바꾼다.
    private static readonly bool SwarmTrailCutEnabled = true;
    // 오브는 플레이어(봇)도 조준한다 — 아래 PvP 사거리·앞열 규칙이 산다.
    private static readonly bool SwarmOrbTargetsPlayersEnabled = true;

    // 오브열: 오브가 이동 경로를 따라오는 전투열. 사격·교차사격 원점과 표적 선정은 오브별 열 좌표(10Hz 이동
    // 표본)를 쓰고, 폐쇄 잔류 파괴도 산다.

    // PvP 오염 환산: 본체 상시 피격 체제의 TTK 앵커. 소수 이월 누산으로 정수 반올림 왜곡(바람 최소 1 인플레)을
    // 막는다. 0.12는 TTK 60초대 — 몹이 사형집행자고 사람은 서로를 몹 앞에 밀어넣는 구조라, 사격은 깎는 수단이고
    // 마무리는 몹과 절단이 가져간다(값을 올리면 원거리로 끝나 몸으로 파고들 이유가 사라진다).
    // 원천은 swarm_config.csv (#325) — 미등재 시 코드 기본값.
    private static float SwarmPvpDamagePerDamage =>
        SwarmConfigData.GetFloat("SWARM_PVP_DAMAGE_PER_DAMAGE", 0.12f);

    /// <summary>PvP 피해 → 본체 체력 이월 누산. 반환 = 이번 타에 실제 적용할 오염(0 가능).</summary>
    private int ConsumeSwarmPvpDamage(long matchingId, long victimId, int rawDamage)
    {
        var key = (matchingId, victimId);
        float total = (matchRuntimes.GetRequired(matchingId).Swarm.Pacing.PvpDamageCarry.TryGetValue(key, out float carry) ? carry : 0f) +
                      rawDamage * SwarmPvpDamagePerDamage;
        int whole = (int)total;
        matchRuntimes.GetRequired(matchingId).Swarm.Pacing.PvpDamageCarry[key] = total - whole;
        return whole;
    }

    // SB 유닛 개별 체력·착탄 지연 대기열·계측 서명 등 매치 상태는 #294에서
    // 상태 홀더(matchRuntimes.GetRequired(matchingId).Swarm.Pacing 등, Services/SwarmArenaStates.cs)로 이동했다.

    private void ProcessPendingSwarmMonsterHits(
        long matchingId, DateTime nowUtc, List<GameClientSession> sessions)
    {
        for (int index = matchRuntimes.GetRequired(matchingId).Swarm.Pacing.PendingMonsterHits.Count - 1; index >= 0; index--)
        {
            var hit = matchRuntimes.GetRequired(matchingId).Swarm.Pacing.PendingMonsterHits[index];
            if (hit.MatchingId != matchingId || nowUtc < hit.ApplyAtUtc)
                continue;

            matchRuntimes.GetRequired(matchingId).Swarm.Pacing.PendingMonsterHits.RemoveAt(index);
            var damageResult = matchRuntimes.GetRequired(matchingId).Monsters.ApplyMonsterDamage(
                matchingId, hit.CombatTargetId, hit.AttackerId, hit.Damage);

            // 결과 집계 (#229): 스웜 전투는 전부 여기를 지난다. 여기서 안 세면
            // 결과 화면이 수백 킬을 "처치 0회"로 표시한다.
            if (damageResult.Applied)
            {
                eventLogs.RecordMonsterHit(
                    matchingId, hit.AttackerId, hit.Damage, damageResult.Killed);
            }

            // 기준점 잠금 (#232 1단계): 비행 중 몬스터가 이미 죽었어도 사건은 소멸하지 않는다 —
            // 발사 순간 잠근 위치에서 끝까지 처리한다. 피해는 없지만(이중 정산 없음) 2단계
            // 교차사격 모양은 여기서 그대로 터져야 "예고 뒤 몹이 죽어도 모양은 남는다"가 참이 된다.
            if (!damageResult.Applied && hit.AnchorPosition != null)
                ResolveSwarmAttackAtLockedAnchor(matchingId, hit, nowUtc);

            // 처치 정산은 교차사격 즉시 타격과 같은 경로 — 계측·처치 로그·소환석 드롭.
            if (damageResult.Applied && damageResult.Killed && damageResult.MonsterState != null)
                combatDamage.SettleSwarmMonsterKill(matchingId, damageResult, hit.AttackerId, hit.Damage, sessions);
        }
    }

    /// <summary>
    ///     기준점 잠금 완료 (#232 1단계): 비행 중 표적 몹이 죽은 사건을 잠근 원점·기준 위치에서
    ///     마무리한다. 지금은 계측만 남긴다 — 2단계 교차사격은 이 자리에서 잠긴 방향·크기의
    ///     모양을 그대로 판정해야 한다("예고 시작 뒤 기준점·방향·크기는 바꾸지 않는다").
    /// </summary>
    private void ResolveSwarmAttackAtLockedAnchor(long matchingId, PendingSwarmMonsterHit hit, DateTime nowUtc)
    {
        _ = hit;
        matchRuntimes.GetRequired(matchingId).Swarm.Pacing.AnchorOrphanCount[matchingId] =
            (matchRuntimes.GetRequired(matchingId).Swarm.Pacing.AnchorOrphanCount.TryGetValue(matchingId, out int count) ? count : 0) + 1;

        if (matchRuntimes.GetRequired(matchingId).Swarm.Pacing.AnchorProbeAtUtc.TryGetValue(matchingId, out var probeAt) && nowUtc < probeAt)
            return;
        matchRuntimes.GetRequired(matchingId).Swarm.Pacing.AnchorProbeAtUtc[matchingId] = nowUtc.AddSeconds(10);
        eventLogs.LogSystem(
            matchingId,
            $"anchor_probe orphanResolved={matchRuntimes.GetRequired(matchingId).Swarm.Pacing.AnchorOrphanCount[matchingId]}");
    }

    // 티어별 오브 HP는 Common(OrbData.GetSquadOrbMaxHp)이 단일 출처 — 클라 체력바와 공유.
    private static int GetSquadOrbMaxHp(int tier) => OrbData.GetSquadOrbMaxHp(tier);

    /// <summary>매치 잠금 밖에서도 읽을 수 있는 터미널 게이트 — 색인에 없는 매치도 터미널로 본다.</summary>
    private bool IsMatchTerminal(long matchingId) =>
        matchRuntimes.Get(matchingId) is not { IsTerminal: false };

    /// <summary>
    ///     #217 8인 맵 역할 검증(M1). 매치 수명(탈락·최후 1인·타이머)은 기존 서바이버 로얄
    ///     흐름이 소유하고, 여기서는 스웜 디렉터 틱·접촉 피해·전투 액터·PvP만 돌린다.
    ///     호출자(50ms 전투 틱·5초 정산 틱)가 매치 잠금을 쥔 채 부른다.
    /// </summary>
    public void ProcessSwarmArenaForMatching(
        long matchingId,
        List<GameClientSession> activeSessions)
    {
        // 종료되었거나 이미 정리된 매치는 틱 처리를 건너뛴다.
        if (IsMatchTerminal(matchingId))
            return;

        // 탐사 모드(SOLO_MAP_VALIDATION=1): 맵 검증용 1인 매치 — 캠프 몹·접촉 피해·
        // 전투·오브 스트림을 전부 끈다. 이동·문·탐색만 남는다.
        // SOLO_MONSTERS=1을 얹으면 캠프 몹만 되살린다 (봇 없이 몹 상대 검증).
        if (MatchStartGate.IsSoloMapValidation(matchingId) && !devOptions.SoloMonsters)
            return;

        var sessions = activeSessions
            .Where(session => session.PlayerId.HasValue &&
                              session.MatchingId == matchingId &&
                              !session.IsGameEnded)
            .ToList();
        var bots = matchRuntimes.GetRequired(matchingId).Bots.GetBots(matchingId).ToList();
        // 봇 전용 매치(어드민 검증)에서도 스웜을 돌린다 — 생존·완주 계측의 기반.
        if (sessions.Count == 0 && bots.Count == 0)
            return;

        if (!matchRuntimes.GetRequired(matchingId).Monsters.HasMatching(matchingId))
        {
            long humanPlayerId = sessions.Count > 0 ? sessions[0].PlayerId!.Value : bots[0].PlayerId;
            if (!matchRuntimes.GetRequired(matchingId).Monsters.InitializeMatching(matchingId, humanPlayerId, DateTime.UtcNow))
                return;

            // 자기장 경계 스폰 규칙을 이 매치의 몬스터 처리기에 연결한다.
            matchRuntimes.GetRequired(matchingId).Monsters.FieldSpawnCellResolver ??= fieldService.ResolveSpawn;
            LogSwarmPairZoneDistances(matchingId);
            // #272 자기장: 수축 시계는 폐쇄 시계와 같은 앵커(AreaClosureManager.GameStartTime)를 쓴다 —
            // 무장은 폐쇄 틱(PrepareSwarmScheduledClosureTick)의 최초 InitializeMatching이 담당한다.
            logger.LogInformation(
                "Swarm pressure field armed: MatchingId={MatchingId}, MaxDistance={MaxDistance}, " +
                "HoldSeconds={Hold}, ShrinkSeconds={Shrink}",
                matchingId, SwarmPressureField.MaxDistance, MatchPressureFieldPolicy.HoldSeconds, MatchPressureFieldPolicy.ShrinkSeconds);
            logger.LogInformation(
                "Swarm arena initialized: MatchingId={MatchingId}, Humans={HumanCount}, Bots={BotCount}",
                matchingId, sessions.Count, bots.Count);
        }

        // 시작 지급 (#232 4단계): 무작위 T1 공격 오브 3개 + 소환석 5 — 6칸 빌드에서 첫 판단은 무엇을 살지가
        // 아니라 들고 시작한 셋을 유지·합성·교체할지다. 샌드박스 사람은 태양·바람·파도 한 개씩 고정.
        int startingStones = Config.SWARM_STARTING_STONE_GRANT;
        foreach (var session in sessions)
        {
            if (!session.PlayerId.HasValue ||
                !matchRuntimes.GetRequired(matchingId).Swarm.Pacing.StartingOrbGrantedPlayers.Add((matchingId, session.PlayerId.Value)))
                continue;

            orbUpgrades.GrantStartingOrbs(matchingId, session.PlayerId.Value, session);
            matchRuntimes.GetRequired(matchingId).SummonStones.AddStones(session.PlayerId.Value, startingStones);
            session.SendSummonStoneState();
        }

        foreach (var bot in bots)
        {
            if (!matchRuntimes.GetRequired(matchingId).Swarm.Pacing.StartingOrbGrantedPlayers.Add((matchingId, bot.PlayerId)))
                continue;

            orbUpgrades.GrantStartingOrbs(matchingId, bot.PlayerId, session: null);
            matchRuntimes.GetRequired(matchingId).SummonStones.AddStones(bot.PlayerId, startingStones);
        }

        DateTime nowUtc = DateTime.UtcNow;

        var aliveSessions = sessions.Where(session => !session.IsEliminated).ToList();
        var aliveBots = bots.Where(bot => !bot.IsEliminated).ToList();
        var participants = aliveSessions
            .Where(session => session.LastValidatedPosition != null)
            .Select(session => new SwarmParticipantSpatial(
                session.PlayerId!.Value, session.CurrentArea, session.LastValidatedPosition!))
            .Concat(aliveBots.Select(bot =>
                new SwarmParticipantSpatial(bot.PlayerId, bot.CurrentArea, bot.Position)))
            .ToList();

        // 실험장 자동 세팅 (#226): 사람이 있는 매치는 첫 틱에 실험장이 자동으로 차려진다.
        // 봇 전용 검증 매치는 제외 — 게이트 계측이 오염되지 않게.
        if (SwarmDummySandboxActive && aliveSessions.Count > 0 && aliveBots.Count > 0 &&
            matchRuntimes.GetRequired(matchingId).Swarm.Pacing.CutDummyAutoSetupDone.Add(matchingId))
        {
            // 무장 과녁은 절단 실험장(DEV_CUT_DUMMY)에만 세운다. 교차사격 샌드박스는 더미 없이
            // 사람 + 몹만 남긴다 (유저 지시 "더미 유저 없애줘") — 단독 생존 종료는
            // 정산 쪽 샌드박스 게이트가 막는다.
            if (SwarmCutDummyAutoSetup)
            {
                // 자동 세팅은 전투 틱과 같은 잠금 안에서 재진입해 나머지 아레나 패킷보다 먼저 나간다.
                SetupSwarmCutDummy(matchingId);
            }
            // 실험장 격리: 더미 외 봇은 조용히 퇴장 — 순위·드롭 이벤트 없이 화면에서 사라진다.
            foreach (var other in matchRuntimes.GetRequired(matchingId).Bots.GetBots(matchingId))
            {
                if (other.IsSwarmCutDummy || other.IsEliminated)
                    continue;
                other.IsEliminated = true;
                using var leavePacket = PacketMaker.G_TO_C_AREA_PLAYER_LEAVE(other.PlayerId);
                foreach (var session in sessions)
                    session.TrySend(leavePacket);
            }
        }

        // 더미(#226 실험 과녁)는 웨이브 디렉터에서 제외 — 몹이 몰려들지 않아 실험장이 조용하다.
        var dummyIds = aliveBots.Where(bot => bot.IsSwarmCutDummy)
            .Select(bot => bot.PlayerId).ToHashSet();
        var directorParticipants = dummyIds.Count == 0
            ? participants
            : participants.Where(participant => !dummyIds.Contains(participant.PlayerId)).ToList();
        // 실험장 (#226): 몹은 나오되(색 무기 과녁) 공격 피해만 아래 게이트에서 꺼진다.
        var tick = matchRuntimes.GetRequired(matchingId).Monsters.Tick(matchingId, directorParticipants, nowUtc);

        // 정지 감시: 8초 이상 제자리인 몹을 매치 로그로 남긴다 — 회귀 감지선.
        foreach (string report in tick.StuckReports)
            eventLogs.LogSystem(matchingId, report);

        // 공급 스폰 계측 (#229 4단계): 공급지·페이즈·마릿수·석 보상 + 스폰 직후 전역 생존 수.
        // alive는 상한 48 준수와 구역 목표 유지를 한 줄로 읽기 위한 값이다.
        if (tick.SupplyPackSpawns.Count > 0)
        {
            int aliveAfter = matchRuntimes.GetRequired(matchingId).Monsters.GetVisualStates(matchingId).Count(state => state.IsAlive);
            foreach (var supplySpawn in tick.SupplyPackSpawns)
                eventLogs.LogSystem(
                    matchingId,
                    $"supply_pack area={supplySpawn.Area} phase={supplySpawn.PackIndex} " +
                    $"monsters={supplySpawn.MonsterCount} stones={supplySpawn.StoneTotal} " +
                    $"alive={aliveAfter}");
        }

        // 인트로 예열은 여기서 끝난다: 공급 디렉터와 몹 이동만 돌리고
        // 전투·절단·포위·물폭탄·접촉 피해는 매치가 시작된 뒤에 붙는다. 카운트다운 동안
        // 운동장에서 각 방으로 걸어 나가는 그림만 만들면 되고, 그 사이 누가 맞아서는 안 된다.
        if (!MatchStartGate.IsGameplayActive(matchingId))
        {
            MonsterSnapshotPublisher.Broadcast(
                matchingId, sessions, matchRuntimes.GetRequired(matchingId).Monsters.GetVisualStates(matchingId));
            return;
        }

        // 절단 실험 더미 (#226): 불사 + 오브 리필 — 절단·포위 타격감 튜닝용 과녁.
        // 리필 기준은 피격 시각이 아니라 오브 수다 (#227 수리): 피격 스탬프는 PvP 미사일이
        // 매 발 갱신해 3초 유예가 영영 지나지 않았다 — 끊어도 다시 안 차던 원인.
        foreach (var dummyBot in aliveBots)
        {
            if (!dummyBot.IsSwarmCutDummy)
                continue;
            dummyBot.Health = Config.MAX_HEALTH;
            ProcessSwarmCutDummyRefill(matchingId, dummyBot, nowUtc);
        }

        // 오브열 (#226 α/C/B): 경로 기록 → 이동 선분의 상대 열 절단.
        // 경로 기록은 #232에서도 산다 — 꼬리 오브의 월드 좌표가 곧 각 공격의 발사 원점이다.
        UpdateSwarmOrbTrails(matchingId, participants);
        if (SwarmTrailCutEnabled)
        {
            ProcessSwarmTrailCuts(matchingId, nowUtc, participants, aliveSessions, aliveBots, sessions);
            // 반격 보호 창 결산 (#227 7단계) — 만료된 쌍만 CUT_RETALIATION_WINDOW로 남긴다.
            ProcessSwarmRetaliationWindows(matchingId, nowUtc, participants);
        }

        // 실험장 (#227): 절단 더미 매치에서는 물폭탄도 끈다 — 파도 오브가 계속 터지면
        // 절단 궤적 실험이 폭발 연출·피해에 묻힌다. 미사일 비무장(AddSwarmParticipantCombatActors)과
        // 같은 조건을 쓴다 — 옵트인 환경변수 자체가 실험장 스위치다.
        // 교차사격 샌드박스(#232)는 켠다 — 파도·바람이 실험 대상이다 (저녁 유저 지시).
        if (!SwarmCutDummyAutoSetup && (dummyIds.Count == 0 || SwarmCrossfireSandbox))
        {
            ProcessSwarmWaveBombs(matchingId, nowUtc, participants, aliveSessions, aliveBots, sessions);
            if (IsMatchTerminal(matchingId) || sessions.Any(session => session.IsGameEnded))
                return;

            windBlades.Process(matchingId, nowUtc, participants, aliveSessions, aliveBots, sessions);
            if (IsMatchTerminal(matchingId) || sessions.Any(session => session.IsGameEnded))
                return;
        }

        // 화상 틱 (#268): 교차사격 충격이 남긴 도트 — 발생원이 위 블록과 무관하게 항상 정산한다.
        crossfires.ProcessSwarmSunBurns(matchingId, nowUtc, aliveSessions, aliveBots, sessions);
        if (IsMatchTerminal(matchingId) || sessions.Any(session => session.IsGameEnded))
            return;

        // 접촉 계측: 접촉이 성립하는지 층별로 남긴다. 이 줄들이 "봇은 접촉 피해를
        // 안 받는다"는 오독을 두 번 걷어냈다 — 실제로는 로깅이 없었고, 그다음엔 배율이 깎고 있었다.
        if (tick.PlayerDamage.Count > 0 && matchRuntimes.GetRequired(matchingId).Swarm.Pacing.ContactProbeAtUtc.TryGetValue(matchingId, out var probeAt)
                ? nowUtc >= probeAt
                : true)
        {
            matchRuntimes.GetRequired(matchingId).Swarm.Pacing.ContactProbeAtUtc[matchingId] = nowUtc.AddSeconds(10);
            int toBots = tick.PlayerDamage.Count(entry => entry.TargetPlayerId < 0);
            int toHumans = tick.PlayerDamage.Count - toBots;
            eventLogs.LogSystem(
                matchingId,
                $"contact_probe damage={tick.PlayerDamage.Count} toBots={toBots} toHumans={toHumans} " +
                $"dummies={dummyIds.Count} aliveBots={aliveBots.Count} aliveSessions={aliveSessions.Count} " +
                $"participants={participants.Count}");
        }

        // 실험장 (#226): 절단 더미 매치는 몹 공격도 끈다 — 절단 튜닝 중 방해 금지.
        // 교차사격 샌드박스(#232)는 켠다 — 몹이 달려들어 부딪히는 것까지가 실험 대상이다
        // (저녁 유저 제보 "몹이 데미지를 안 입힌다": 이 게이트가 막고 있었다).
        if (dummyIds.Count == 0 || SwarmCrossfireSandbox)
        {
            foreach (var damage in tick.PlayerDamage)
            {
                ApplySwarmParticipantDamage(matchingId, damage, aliveSessions, aliveBots, sessions);
                if (sessions.Any(session => session.IsGameEnded))
                    return;
            }
        }
        botDecisions.ProcessSwarmBotRecovery(matchingId, aliveBots, nowUtc);
        ProcessPeriodicBuffs(matchingId, aliveSessions, nowUtc);
        if (IsMatchTerminal(matchingId) || sessions.Any(session => session.IsGameEnded))
            return;
        ProcessSwarmSleepRecovery(aliveSessions, nowUtc);

        // 봇도 사람과 같은 규칙으로 성장한다: 소환석 5개 + 스팟 소진. 공짜 버튼 소환 없음.
        botDecisions.ProcessSwarmBotExplores(matchingId, aliveBots, sessions);
        botDecisions.ProcessSwarmBotDoorUnlocks(matchingId, aliveBots, sessions, nowUtc);

        if (MonsterSnapshotPublisher.TryConsumeBroadcastSlot(matchRuntimes.GetRequired(matchingId), nowUtc))
            MonsterSnapshotPublisher.Broadcast(matchingId, sessions, matchRuntimes.GetRequired(matchingId).Monsters.GetVisualStates(matchingId));

        var actors = BuildSwarmArenaCombatActors(matchingId, aliveSessions, aliveBots, nowUtc);
        orbRecovery.Process(matchingId, actors, aliveSessions, aliveBots, nowUtc);
        orbVisuals.Publish(matchingId, actors, sessions);
        BroadcastSwarmOrbRankings(matchingId, sessions, bots);
        // 성장 카드 (#226 단계 C): 소환석이 비용에 닿는 즉시 3택 오퍼 — 상자 트리거 퇴역.
        growth.ProcessOffers(matchingId, aliveSessions, aliveBots);
        if (ProcessSwarmScoreTimeout(matchingId, nowUtc, sessions, aliveSessions, aliveBots))
            return;
        // 지난 틱에 예약된 착탄들을 먼저 정산한다 — 체력바가 폭발 시점에 맞춰 닳는다.
        ProcessPendingSwarmMonsterHits(matchingId, nowUtc, sessions);
        // 교차사격 판정 (#232 2단계): 예고가 끝난 모양을 이번 틱 위치로 판정한다.
        crossfires.ProcessSwarmCrossfires(matchingId, nowUtc, participants, aliveSessions, aliveBots, sessions);
        if (IsMatchTerminal(matchingId) || sessions.Any(session => session.IsGameEnded))
            return;

        // 비행 중인 PvP 탄은 여기서 착탄 처리한다 — 매 틱 지우면 안 된다. "리졸버는 PvE 전용"이라는 전제의 청소가
        // 리졸버가 사람 표적도 내보내게 바뀐 뒤 방금 발사한 탄을 다음 틱에 통째로 삭제해 PvP가 한 발도 착탄하지 못했다.
        for (int index = matchRuntimes.GetRequired(matchingId).Swarm.Pacing.PendingPvpHits.Count - 1; index >= 0; index--)
        {
            var pending = matchRuntimes.GetRequired(matchingId).Swarm.Pacing.PendingPvpHits[index];
            if (pending.MatchingId != matchingId || nowUtc < pending.DueAtUtc)
                continue;
            matchRuntimes.GetRequired(matchingId).Swarm.Pacing.PendingPvpHits.RemoveAt(index);
            ApplySwarmPvpAttack(matchingId, pending.Attack, aliveSessions, aliveBots, sessions,
                broadcastVfx: false);
            if (IsMatchTerminal(matchingId) || sessions.Any(session => session.IsGameEnded))
                return;
        }

        // 본체 위치 사전 — PvP 사거리를 본체 기준으로 재기 위한 조회표.
        // 본체 액터는 무기 없는 순수 표적이다(WeaponItemId == 0).
        var swarmBodyPositions = new Dictionary<long, Vector3f>();
        foreach (var actor in actors)
        {
            if (actor.WeaponItemId == 0 && !actor.IsMonsterTarget)
                swarmBodyPositions[actor.PlayerId] = actor.Position;
        }

        // 교차사격 예고 상한 (#232, 명세 "동시 예고 최대 2개"): 상한에 닿은 소유자의 태양 오브는
        // 이번 틱에 표적을 잡지 않는다 — 리졸버가 조준을 유예하고, 자리가 나면 곧 쏜다.
        // 발을 버리지 않으면서 예고 수를 묶는 유일한 자리 (발사 뒤엔 이미 쿨다운이 소모돼 있다).
        var crossfireCappedOwners = crossfires.CollectSwarmCrossfireCappedOwners(matchingId, nowUtc);
        // 표적 분산 (#232): 내 살아 있는 모양이 이미 겨눈 몹은 내 다른 태양 오브의 후보에서 뺀다 —
        // 오브마다 제 자리에서 "아직 아무도 안 겨눈" 가장 가까운 몹을 고른다.
        var crossfireAnchoredTargets = crossfires.CollectSwarmCrossfireAnchoredTargets(matchingId);

        var attacks = matchRuntimes.GetRequired(matchingId).Combat.Resolve(
            matchingId,
            actors,
            nowUtc,
            // 유저간 공격도 같은 리졸버가 담당한다(몹이랑 똑같이 유도탄으로 결정). 아래 루프의 태양·바람 분기가
            // 몹 사격과 같은 발사 연출(BroadcastSwarmAttackVfxToTargetAndObservers)과 비행시간 착탄을 쓴다.
            // PvP 제한: 자동 공격과 절단이 서로 다른 것을 건드려야 한다. 자동 공격은 본체를 서서히 압박하고, 오브
            // 손실은 충돌 절단으로만 난다. 전체 오브가 사람을 쏘면 20개 꼬리가 3개 꼬리를 그대로 녹인다 — 앞열
            // 3개로 끊어 오브 수는 PvE 성장과 절단 위험만 키우고, 사거리도 PvE(7)보다 짧은 5로 둔다 — 붙어야 싸운다.
            // 아래 PvP 사거리·앞열 규칙은 SwarmOrbTargetsPlayersEnabled를 켤 때만 산다.
            (attacker, target) =>
            {
                if (attacker.IsMonsterTarget || attacker.Area != target.Area)
                    return false;
                if (CrossfireService.IsSwarmCrossfireWeapon(attacker.WeaponItemId))
                {
                    if (crossfireCappedOwners.Contains(attacker.PlayerId))
                        return false;
                    if (crossfireAnchoredTargets.Contains((attacker.PlayerId, target.PlayerId)))
                        return false;
                    // 사거리(티어, 바닥면 타원) 안에 표적이 있으면 쏜다 — 축 정렬 조건은 퇴역
                    // (유저 결정 개정: 축이 맞는 표적을 기다리면 태양이 아예 공격을 안
                    // 하는 구간이 생긴다). 발사 방향은 표적이 아니라 이동 방향의 수직 타일 축이
                    // 정하므로(TryScheduleSwarmCrossfire), 이 표적은 "쏠 이유"일 뿐 "조준점"이 아니다 —
                    // 선이 이 표적을 못 맞혀도 발사한다. 논타게팅.
                    if (!IsWithinSwarmOrbRange(attacker, target))
                        return false;
                }
                if (target.IsMonsterTarget)
                    return true;
                if (!SwarmOrbTargetsPlayersEnabled)
                    return false;
                // 플레이어 최우선 (유저 지시 "반경 내에 다른 플레이어가 있으면 최우선"): 태양은
                // 앞열 3개 제한·본체 기준 5u 사거리를 걷고, 티어 사거리(위 IsWithinSwarmOrbRange) 안이면
                // 어느 오브든 사람을 후보에 올린다. 우선순위 0(사람) < 2(몹)이라 후보에 오르면 곧 최우선이다.
                if (CrossfireService.IsSwarmCrossfireWeapon(attacker.WeaponItemId))
                    return true;
                if (attacker.TrailOrdinal >= Config.SWARM_PVP_ORB_COUNT)
                    return false;

                // 사거리는 오브가 아니라 본체 기준이다 (유저 요청: 사거리 표시).
                // 오브별 원점으로 재면 꼬리가 길수록 사정권이 늘어 "오브 수는 PvP 화력을
                // 키우지 않는다"는 규칙과 어긋나고, 링 하나로 표시할 수도 없다.
                if (!swarmBodyPositions.TryGetValue(attacker.PlayerId, out var attackerBody))
                    attackerBody = attacker.Position;
                float dx = target.Position.X - attackerBody.X;
                float dy = target.Position.Y - attackerBody.Y;
                if (dx * dx + dy * dy >
                    Config.SWARM_PVP_ATTACK_RANGE * Config.SWARM_PVP_ATTACK_RANGE)
                    return false;

                // 시야 판정은 걷어냈다 (범위 안이면 사람 먼저 무조건). 오브는 궤도를 돌며 벽·소품 위를 자주 지나고
                // HasClearPath는 출발 셀이 불투명하면 즉시 false라 몸이 붙어 있어도 PvP가 0건이 됐다 —
                // 사거리 안에 붙어 있는데 조준이 안 서는 쪽이 훨씬 나쁘다.
                return true;
            });
        Dictionary<long, ProximityCombatActor>? actorById = null;
        foreach (var attack in attacks)
        {
            int monsterId = matchRuntimes.GetRequired(matchingId).Monsters.GetMonsterIdForCombatTarget(matchingId, attack.TargetPlayerId);
            // 유령 발사 가드 (#226 진단): 같은 틱에 죽은 몬스터의 CombatTargetId(-4e18대)가 몬스터 분기를
            // 통과해 PvP 분기로 새던 문제 — 음수 대역 차단. 태양 분기보다 먼저 건다.
            if (monsterId <= 0 && attack.TargetPlayerId < -1_000_000_000_000L)
                continue;
            // 태양은 표적이 몬스터든 플레이어든 교차사격 투사체다 (유저 지시: 오브가 사람도 조준) —
            // 첫 표적에서 폭발. 바람은 조준하지 않는다(회전 칼날), 파도는 물폭탄.
            if (CrossfireService.IsSwarmCrossfireWeapon(attack.WeaponItemId))
            {
                // 태양 = 교차사격 직선 (#232 2단계): 유도탄이 아니라 예고 뒤 쓸고 지나가는
                // 큰 공격이다. 미사일 연출·비행시간 착탄·예약을 타지 않고 모양 하나를 잠근다.
                // 모양을 못 잠그면(같은 틱에 여러 오브가 함께 준비돼 예고 상한을 넘김) 그 발은 환불한다 —
                // 쿨다운을 되돌려 다음 틱에 다시 시도한다. 모양 없이 때리던 옛 폴백은 "안 맞은 몹이 죽는"
                // 보이지 않는 피해였다 (유저 제보). 표시 = 판정: 화면에 없는 공격은 없다.
                actorById ??= actors
                    .GroupBy(actor => actor.PlayerId)
                    .ToDictionary(group => group.Key, group => group.First());
                var sunOrigin = attack.Origin ??
                                (actorById.TryGetValue(attack.AttackerPlayerId, out var sunAttacker)
                                    ? sunAttacker.Position
                                    : null);
                var sunAnchor = attack.AnchorPosition ??
                                (actorById.TryGetValue(attack.TargetPlayerId, out var sunTarget)
                                    ? sunTarget.Position
                                    : null);
                // 표적 분산의 같은 틱 구멍: 리졸버 필터는 틱 시작의 모양만 봤으니, 같은 틱에 준비된
                // 두 오브가 같은 몹을 고를 수 있다 — 뒤 오브는 환불하고 다음 틱에 다른 몹을 고르게 한다.
                bool anchoredThisTick = !crossfireAnchoredTargets.Add((attack.AttackerPlayerId, attack.TargetPlayerId));
                if (anchoredThisTick ||
                    !crossfires.TryScheduleSwarmCrossfire(
                        matchingId, attack, sunOrigin, sunAnchor, monsterId, attack.Damage, nowUtc, sessions))
                {
                    // 로그는 남기지 않는다 — 상한이 찬 동안 매 틱 되풀이되는 정상 대기라 이벤트 흐름만 메운다.
                    matchRuntimes.GetRequired(matchingId).Combat.RefundAttack(
                        matchingId, attack.AttackerPlayerId, attack.AttackerItemUid, nowUtc);
                }
                continue;
            }

            if (monsterId > 0)
            {
                int monsterDamage = combatDamage.RollSwarmCriticalDamage(
                    matchingId, attack.Damage, out bool critical);
                // 발사 연출은 즉시, 피해는 투사체 비행시간 뒤에 — 체력바와 폭발이 일치한다.
                var attackerSession = sessions.FirstOrDefault(
                    session => session.PlayerId == attack.AttackerPlayerId);
                // 자동 공격은 교전 잠금을 찍지 않는다 (#229 6단계 수정): 오브는 사거리 안 잔상을
                // 쉬지 않고 쏘므로, 이걸 "가해"로 세면 잔상이 한 마리라도 살아 있는 한 영영 눕지
                // 못한다 — "수면은 잔상이 없는 상태를 요구하지 않는다"는 규칙과 정면으로 충돌하고,
                // 전멸 뒤 4초 휴지 창도 3초를 잠금에 뺏겨 무의미해진다.
                // 잠금은 내가 몸으로 지르는 절단과 피격에만 건다.
                combatDamage.SendMonsterHitNotification(attackerSession,
                    monsterId, attack.Area, attack.WeaponItemId, monsterDamage, critical);

                // 관전자에게도 발사 연출 (#219): 공격자 피드백만으로는 봇의 사냥이 완전 무음이었다.
                // 클라 관전 분기(TargetPlayerId < 0 → 몬스터)가 받는 음수 id로 실어 보낸다.
                BroadcastSwarmAttackVfxToTargetAndObservers(
                    attack with { TargetPlayerId = -monsterId }, sessions);

                actorById ??= actors
                    .GroupBy(actor => actor.PlayerId)
                    .ToDictionary(group => group.Key, group => group.First());
                // 비행시간은 실제 발사 원점(오브)에서 잠근 기준 위치까지다 (#232 1단계). 리졸버가
                // 원점을 안 실어 주는 레거시 경로만 본체 대표 액터로 폴백한다.
                var origin = attack.Origin ??
                             (actorById.TryGetValue(attack.AttackerPlayerId, out var attackerActor)
                                 ? attackerActor.Position
                                 : null);
                var anchor = attack.AnchorPosition ??
                             (actorById.TryGetValue(attack.TargetPlayerId, out var targetActor)
                                 ? targetActor.Position
                                 : null);
                float distance = origin != null && anchor != null
                    ? Vector3f.Distance(origin, anchor)
                    : Config.SWARM_ORB_ATTACK_RANGE;
                double delaySeconds =
                    OrbData.GetPvpProjectileImpactDelaySeconds(attack.WeaponItemId, distance);
                // 발사 즉시 예약 (#229): 착탄까지 기다리면 그 사이 다른 오브가 같은 몹을 또
                // 고른다. 예약분으로 이미 죽는 몹은 표적 후보에서 빠지므로 사격이 흩어진다.
                matchRuntimes.GetRequired(matchingId).Monsters.ReserveMonsterDamage(
                    matchingId, attack.TargetPlayerId, monsterDamage);
                // 기준점 계측 + 잠금 (#232 1단계): 발사 순간 몹의 공격 사건 수를 올리고,
                // 원점·기준 위치·무기를 박제해 착탄 정산까지 들고 간다.
                matchRuntimes.GetRequired(matchingId).Monsters.RecordMonsterAttackEvent(matchingId, attack.TargetPlayerId);
                matchRuntimes.GetRequired(matchingId).Swarm.Pacing.PendingMonsterHits.Add(new PendingSwarmMonsterHit(
                    matchingId, attack.TargetPlayerId, attack.AttackerPlayerId,
                    monsterDamage, nowUtc.AddSeconds(delaySeconds),
                    attack.WeaponItemId, attack.AttackerItemUid, origin, anchor));
                continue;
            }

            // 유령 발사 가드 (#226 진단): 같은 틱에 죽은 몬스터의 CombatTargetId(-4e18대)가
            // 몬스터 분기(monsterId=0 조회)를 통과해 PvP 분기로 새던 문제 — 음수 대역 차단.
            if (attack.TargetPlayerId < -1_000_000_000_000L)
                continue;

            // 태양·바람 유도탄 (복귀): 발사 연출 즉시 + 비행시간 뒤 착탄 확정 —
            // 직선탄 회피 실험은 상시 이동에서 유령 사격이 됐다. 클라 투사체는 표적을 추적한다.
            if (OrbData.TryGetColorAndTier(attack.WeaponItemId, out var pvpColor, out _) &&
                pvpColor is OrbColor.Red or OrbColor.Green)
            {
                BroadcastSwarmAttackVfxToTargetAndObservers(attack, sessions);
                actorById ??= actors
                    .GroupBy(actor => actor.PlayerId)
                    .ToDictionary(group => group.Key, group => group.First());
                float pvpDistance = actorById.TryGetValue(attack.AttackerPlayerId, out var pvpAttacker) &&
                                    actorById.TryGetValue(attack.TargetPlayerId, out var pvpTarget)
                    ? Vector3f.Distance(pvpAttacker.Position, pvpTarget.Position)
                    : Config.SWARM_ORB_ATTACK_RANGE;
                double pvpDelaySeconds =
                    OrbData.GetPvpProjectileImpactDelaySeconds(attack.WeaponItemId, pvpDistance);
                matchRuntimes.GetRequired(matchingId).Swarm.Pacing.PendingPvpHits.Add((matchingId, attack, nowUtc.AddSeconds(pvpDelaySeconds)));
                continue;
            }

            // PvP는 저데미지 보조다. 킬의 주 경로는 스웜이어야 한다 (#217 결합 원칙).
            ApplySwarmPvpAttack(matchingId, attack, aliveSessions, aliveBots, sessions);
            if (IsMatchTerminal(matchingId) || sessions.Any(session => session.IsGameEnded))
                return;
        }

        // 봇 탈락 확정은 기존 근접전투 파이프라인과 동일한 경로를 쓴다.
        foreach (var bot in aliveBots)
        {
            if (!matchRuntimes.GetRequired(matchingId).Bots.TryFinalizeProximityAutoCombatElimination(bot, matchingId))
                continue;

            botEliminations.Process(matchRuntimes.GetRequired(matchingId), bot.PlayerId, EliminationReason.HEALTH_ZERO,
                attackerPlayerId: bot.LastProximityAttackerPlayerId);
            if (IsMatchTerminal(matchingId) ||
                activeSessions.Any(session => session.IsGameEnded))
                return;
        }
    }

    // 스팟 예산 선소진(#217 성장곡선 v3, 21개)은 퇴역 — SB에는 인위적 봉인이 없고,
    // 희소성은 리젠(60초)과 크기 비례 비용이 담당한다. 배치된 스팟은 전부 살아 있다.

    // 쌍 깔때기: 시작방 → 만남 구역. 거리 편차의 보정값(잔상 스폰 시점)은 이 로그를 계측한 뒤 정한다.
    // #272 School2: 복도 연결 정의와 1:1 — 시작방 2곳이 합류 1곳을 공유한다.
    private static readonly (AreaType StartRoom, AreaType PairZone)[] SwarmPairZones =
    [
        (AreaType.S2Classroom1, AreaType.S2Library1),
        (AreaType.S2Classroom2, AreaType.S2Library1),
        (AreaType.S2ExamRoom, AreaType.S2Gym1),
        (AreaType.S2BroadcastRoom, AreaType.S2Gym1),
        (AreaType.S2Storage, AreaType.S2Library2),
        (AreaType.S2AdminOffice2, AreaType.S2Library2),
        (AreaType.S2AdminOffice1, AreaType.S2Gym2),
        (AreaType.S2NurseOffice, AreaType.S2Gym2)
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
                Config.SWARM_MATCH_MAP,
                startRoom, GameMapData.GetAreaSpawnCell(Config.SWARM_MATCH_MAP, startRoom),
                pairZone, GameMapData.GetAreaSpawnCell(Config.SWARM_MATCH_MAP, pairZone));
            logger.LogInformation(
                "Swarm pair distance: MatchingId={MatchingId}, StartRoom={StartRoom}, PairZone={PairZone}, Steps={Steps}",
                matchingId, startRoom, pairZone, path?.Count ?? -1);
        }
    }


    /// <summary>매치 잠금 안에서 플레이어별 버프 실행 시각을 확인하고 체력 변경을 적용한다.</summary>
    private void ProcessPeriodicBuffs(long matchingId, List<GameClientSession> sessions, DateTime nowUtc)
    {
        foreach (var session in sessions)
        {
            if (IsMatchTerminal(matchingId)) return;
            if (session.IsEliminated || session.IsGameEnded || !session.IsAcceptingMessages)
            {
                session.Condition.ClearPeriodicBuffs();
                continue;
            }

            try
            {
                session.Condition.UpdatePeriodicBuffs(nowUtc, Config.MAX_HEALTH, health =>
                {
                    session.HealthChanges.Handle(session.Condition.ChangeHealth(health, Config.MAX_HEALTH));
                    if (session.IsEliminated || session.IsGameEnded || IsMatchTerminal(matchingId))
                        session.Condition.ClearPeriodicBuffs();
                });
            }
            catch (Exception ex)
            {
                session.Condition.ClearPeriodicBuffs();
                logger.LogWarning(ex, "Periodic buff processing failed: MatchingId={MatchingId}, PlayerId={PlayerId}",
                    matchingId, session.PlayerId);
            }
        }
    }
    /// <summary>생존 플레이어의 수면 회복을 갱신한다.</summary>
    internal static void ProcessSwarmSleepRecovery(
        List<GameClientSession> aliveSessions, DateTime nowUtc)
    {
        foreach (var session in aliveSessions)
        {
            int recovered = session.Condition.GetSleepRecovery(nowUtc, session.IsEliminated, Config.MAX_HEALTH);
            if (recovered <= 0) continue;
            session.HealthChanges.Handle(session.Condition.Recover(recovered));
            if (!session.PlayerId.HasValue) continue;

            using var packet = PacketMaker.G_TO_C_HEALTH_RECOVERY(new()
            {
                PlayerId = session.PlayerId.Value,
                AreaType = session.CurrentArea,
                Amount = recovered,
                Source = HealthRecoveryKind.Sleep
            });
            session.TrySend(packet);
        }
    }

    /// <summary>상자 시간 등급 (#222 M3): 개전 앵커(게이트, 봇 전용은 스웜 첫 틱) 경과로 티어 결정.</summary>
    private int GetSwarmDraftTier(long matchingId)
    {
        var startedAtUtc = MatchStartGate.GetGameplayStartedAtUtc(matchingId);
        if (startedAtUtc == null &&
            matchRuntimes.GetRequired(matchingId).Swarm.Pacing.MatchFallbackAnchorUtc.TryGetValue(matchingId, out var fallbackAnchor))
            startedAtUtc = fallbackAnchor;
        return OrbData.GetDraftTierByElapsed((DateTime.UtcNow - startedAtUtc)?.TotalSeconds);
    }

    // ===== 오브열 (#226 실험 α/β): 서버 경로 추적 — 오브별 공격 원점·본체 접촉 판정의 좌표 =====

    private const float SwarmTrailSampleMinDistance = 0.08f;
    private const float SwarmTrailTeleportResetDistance = 5f;

    // 열 절단: 대상 오브(ItemUid)별 래치 — 같은 오브를 다시 때리려면 판정 타원 밖으로 완전히 나갔다 와야 하고
    // (이탈 재무장), 0.8초 안의 재타는 같은 통과로 본다. 움직이는 열이 절단자를 스치면 매 틱 재무장되므로
    // 디바운스가 없으면 한 통과가 여러 번으로 잡힌다. 서로 다른 오브 연속 타격은 자유 — 꼬리를 따라 달리면 순차로 금이 간다.
    // 절단 비용·창의 원천은 swarm_config.csv (#335) — 미등재 시 코드 기본값.
    private static double SwarmTrailCutSameOrbDebounceSeconds =>
        SwarmConfigData.GetDouble("SWARM_TRAIL_CUT_SAME_ORB_DEBOUNCE_SECONDS", 0.8d);
    private const float SwarmTrailCutMaxSegmentLength = 2f;
    // 고위험 단일 절단 (#232): 성공한 공격자는 체력 35를 내고 8초 동안 수면 회복을 잃는다.
    // 비용을 감당할 수 없으면(만충으로 탈락) 절단도 비용도 발생하지 않는다.
    private static int SwarmSingleCutHealthCost =>
        SwarmConfigData.GetInt("SWARM_SINGLE_CUT_HEALTH_COST", 35);
    private static double SwarmSingleCutHealLockSeconds =>
        SwarmConfigData.GetDouble("SWARM_SINGLE_CUT_HEAL_LOCK_SECONDS", 8d);
    // 절단자 한정 반격 보호 (#227 7단계): 실제 꼬리 상실 순간부터 1.2초.
    // 전역 무적이 아니라 '방금 내 꼬리를 자른 그 사람에게 되갚을 시간'이다 —
    // 제3자·잔상·폐쇄 피해는 그대로 들어오고, 피해자는 이동·사격·역절단을 다 할 수 있다.
    private static double SwarmCutRetaliationWindowSeconds =>
        SwarmConfigData.GetDouble("SWARM_CUT_RETALIATION_WINDOW_SECONDS", 1.2d);
    // 오브 관통 판정 (정규화 dy×2 공간): 링크 선을 스치는 게 아니라 오브를 밟아야 끊긴다. 판정 중심은 스프라이트가
    // 떠 있어 위로 오프셋한다. 반경 0.42는 오브 간격 0.9의 절반(0.45) 아래 — 넘기면 판정 원이 겹쳐 의도보다 앞선
    // 순번이 잡히고, 절단은 그 순번부터 뒤를 전부 날리므로 손실이 과해진다.
    private const float SwarmTrailCutOrbHitRadiusX = 0.42f;
    private const float SwarmTrailCutOrbHitRadiusY = 0.42f;
    private const float SwarmTrailCutOrbHitYOffset = 0.15f;
    // 절단 파열 플래시 반경 — 포위 링과 같은 원형을 작게 띄운다.
    private const float SwarmTrailCutFlashRadius = OrbTrailService.CutFlashRadius;

    // 오브 트레일·절단 래치·반격 창 상태는 matchRuntimes.GetRequired(matchingId).Swarm.TrailCombat (#294 상태 홀더).

    /// <summary>이 절단자가 이 피해자에게 손댈 수 없는 상태인가 — 절단·본체 피해 공통 관문.</summary>
    private bool IsSwarmRetaliationGuarded(long matchingId, long cutterId, long victimId, DateTime nowUtc)
    {
        return matchRuntimes.GetRequired(matchingId).Swarm.TrailCombat.CutRetaliationWindows.TryGetValue((matchingId, cutterId, victimId), out var window) &&
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
        if (matchRuntimes.GetRequired(matchingId).Swarm.TrailCombat.CutRetaliationWindows.TryGetValue((matchingId, victimId, cutterId), out var opposite) &&
            nowUtc < opposite.ExpiresAtUtc)
            opposite.Retaliated = true;

        var key = (matchingId, cutterId, victimId);
        if (!matchRuntimes.GetRequired(matchingId).Swarm.TrailCombat.CutRetaliationWindows.TryGetValue(key, out var window))
        {
            window = new SwarmRetaliationWindow { OpenedAtUtc = nowUtc, OpenedArea = area };
            matchRuntimes.GetRequired(matchingId).Swarm.TrailCombat.CutRetaliationWindows[key] = window;
        }

        window.ExpiresAtUtc = nowUtc.AddSeconds(SwarmCutRetaliationWindowSeconds);

        // 잔광은 피해자 본인과 그 절단자에게만 — 제3자는 정상 공격할 수 있으니 보지 않는다.
        SendSwarmRetaliationVfx(
            cutterId, victimId, area, SwarmRingVfxKindRetaliationGuard,
            (float)SwarmCutRetaliationWindowSeconds, allSessions);
    }

    /// <summary>만료된 창을 결산해 CUT_RETALIATION_WINDOW로 남긴다 — 매 틱 호출.</summary>
    private void ProcessSwarmRetaliationWindows(
        long matchingId, DateTime nowUtc, List<SwarmParticipantSpatial> participants)
    {
        var expired = matchRuntimes.GetRequired(matchingId).Swarm.TrailCombat.CutRetaliationWindows
            .Where(pair => pair.Key.MatchingId == matchingId && nowUtc >= pair.Value.ExpiresAtUtc)
            .ToList();
        foreach (var (key, window) in expired)
        {
            matchRuntimes.GetRequired(matchingId).Swarm.TrailCombat.CutRetaliationWindows.Remove(key);

            // 양측 이탈: 창이 닫히는 시점에 둘이 같은 구역에 없다 = 싸움을 접고 갈라섰다.
            SwarmParticipantSpatial? cutter = participants
                .Where(participant => participant.PlayerId == key.CutterId)
                .Select(static participant => (SwarmParticipantSpatial?)participant)
                .FirstOrDefault();
            SwarmParticipantSpatial? victim = participants
                .Where(participant => participant.PlayerId == key.VictimId)
                .Select(static participant => (SwarmParticipantSpatial?)participant)
                .FirstOrDefault();
            bool bothDisengaged =
                !cutter.HasValue ||
                !victim.HasValue ||
                cutter.Value.Area != victim.Value.Area;

            eventLogs.LogSwarmRetaliationWindow(
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
                session.TrySend(packet);
        }
    }

    /// <summary>클라 PlayerTool.UpdateOrbTrail과 같은 규칙 — 정지하면 경로가 얼어 열이 남는다.</summary>
    private void UpdateSwarmOrbTrails(long matchingId, List<SwarmParticipantSpatial> participants)
    {
        foreach (var participant in participants)
        {
            var key = (matchingId, participant.PlayerId);
            if (!matchRuntimes.GetRequired(matchingId).Swarm.TrailCombat.OrbTrails.TryGetValue(key, out var points))
            {
                points = new List<Vector3f>();
                matchRuntimes.GetRequired(matchingId).Swarm.TrailCombat.OrbTrails[key] = points;
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
            int trailOrbCount = Math.Max(orbTrails.CountSwarmSquadOrbs(matchingId, participant.PlayerId) + 2, 4);
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

    /// <summary>
    ///     열 절단: 본체 이동 선분이 상대 오브 링크(오브i-오브i+1)를 가로지르면 밟힌 순번부터 꼬리 끝까지 파괴한다.
    ///     이동이 곧 공격 동사이고, 비용은 "상대 성장물이 끊김"과 절단자의 체력 35다. 본체-첫 오브 링크는
    ///     절단 불가. 한 이동 선분당 가장 먼저 교차한 링크 하나만 처리한다.
    /// </summary>
    private void ProcessSwarmTrailCuts(
        long matchingId,
        DateTime nowUtc,
        List<SwarmParticipantSpatial> participants,
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
            var orbs = matchRuntimes.GetRequired(matchingId).Inventory.GetPlayerInventory(owner.PlayerId)
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
                points.Add(orbTrails.GetSwarmOrbTrailPosition(
                    matchingId, owner.PlayerId, ordinal, owner.Position, chainTiers));
                uids.Add(orbs[ordinal].ItemUid);
                itemIds.Add(orbs[ordinal].ItemId);
            }

            chains[owner.PlayerId] = (owner.Area, owner.Position, points, uids, itemIds);
        }

        foreach (var cutter in participants)
        {
            var positionKey = (matchingId, cutter.PlayerId);
            bool hasPrevious = matchRuntimes.GetRequired(matchingId).Swarm.TrailCombat.TrailLastTickPositions.TryGetValue(positionKey, out var previous);
            matchRuntimes.GetRequired(matchingId).Swarm.TrailCombat.TrailLastTickPositions[positionKey] =
                new Vector3f(cutter.Position.X, cutter.Position.Y, 0f);
            if (!hasPrevious)
                continue;
            // 무오브는 절단할 수 없다 (유저 명세). 자동 공격도 절단도 못 하는
            // 상태라야 "살아 있지만 전투력을 잃어 재건에 집중하는 패배 직전"이 성립한다 —
            // 지금은 빈손으로 남의 꼬리만 끊고 다니는 무적 훼방꾼이 될 수 있다.
            // 위치 기록은 위에서 이미 갱신했다 — 재건 직후 첫 틱부터 다시 자를 수 있다.
            // chains는 오브가 있는 사람만 담으므로 이 검사가 곧 보유 검사다.
            if (!chains.ContainsKey(cutter.PlayerId))
                continue;
            TryPerformSwarmTrailCut(matchingId, cutter.PlayerId, cutter.PlayerId, cutter.Area,
                previous!, cutter.Position, chains, nowUtc, aliveSessions, aliveBots, allSessions);
        }
    }

    /// <summary>공격에 기여하는 오브 수 — 회복 오브는 사격에 참여하지 않아 뺀다 (#227 5단계).</summary>
    private static int CountSwarmAttackOrbs(IReadOnlyList<int> orderedItemIds)
    {
        int count = 0;
        foreach (int itemId in orderedItemIds)
            if (OrbData.TryGetColorAndTier(itemId, out _, out _))
                count++;
        return count;
    }

    /// <summary>열 순서대로의 아이템 ID — 절단 후 재조회용.</summary>
    private List<int> GetSwarmOrbItemIdsInOrder(long matchingId, long playerId)
    {
        return matchRuntimes.GetRequired(matchingId).Inventory.GetPlayerInventory(playerId)
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
        Vector3f? bestOrbPosition = null;
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
                if (matchRuntimes.GetRequired(matchingId).Swarm.TrailCombat.OrbCutLatches.TryGetValue(
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
                    if (matchRuntimes.GetRequired(matchingId).Swarm.TrailCombat.CutRetaliationWindows.TryGetValue(
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

        if (bestOwnerId == 0 || bestOrbPosition == null)
            return;

        // 비용 선결 (#232 단일 절단): +35를 감당할 수 없으면(만충 = 탈락) 절단도 비용도 없다.
        // 래치도 안 찍는다 — 다음 틱에 사정이 달라져 있으면(회복) 그때 다시 판정한다.
        var cutterSession = aliveSessions.FirstOrDefault(session => session.PlayerId == cutterId);
        var cutterBot = cutterSession == null
            ? aliveBots.FirstOrDefault(candidate => candidate.PlayerId == cutterId)
            : null;
        int cutterHealthBefore = cutterSession?.CurrentHealth ?? cutterBot?.Health ?? 0;
        if (cutterHealthBefore - SwarmSingleCutHealthCost <= 0)
        {
            eventLogs.LogSystem(
                matchingId,
                $"ORB_SINGLE_CUT_REFUSED attacker={creditPlayerId} victim={bestOwnerId} targetOrbUid={bestOrbUid} " +
                $"targetIndex={bestTailOrdinal} reason=cost attackerHealth={cutterHealthBefore}");
            return;
        }

        // 봇 절단 자제: 봇은 체력이 절반 이상일 때, 봇 1인당 6초에 한 번만 자른다. 봇끼리 몇 초 간격으로 서로 자르며
        // 자해로 죽어 나가면 사람 카메라에 남는 긴 꼬리가 없다 — 봇의 절단은 '한 번 지르는 사건'으로 읽혀야 한다.
        // 거절된 통과는 래치를 찍어 같은 오브를 이번 통과에서 다시 판정하지 않는다 — 사람의 절단은 이 규칙과 무관하다.
        if (cutterBot != null && !botDecisions.IsSwarmBotCutAllowed(matchingId, cutterId, cutterHealthBefore, nowUtc, SwarmSingleCutHealthCost))
        {
            matchRuntimes.GetRequired(matchingId).Swarm.TrailCombat.OrbCutLatches[(matchingId, cutterId, bestOrbUid)] = nowUtc;
            return;
        }

        matchRuntimes.GetRequired(matchingId).Swarm.TrailCombat.OrbCutLatches[(matchingId, cutterId, bestOrbUid)] = nowUtc;

        // #229 6단계: 절단은 내가 몸으로 지르는 가해다 — 교전 잠금을 찍어 절단하고 바로 눕는
        // 도주 회복을 막는다. 수면 해제는 안 건다 — 절단하러 움직인 순간 이동이 이미 깨웠다.
        cutterSession?.Condition.MarkSwarmCombat(nowUtc);

        // 절단 진입 계측 (#227 3·6단계): 공격자·피해자·후보 ordinal·그 자리를 덮던 적 오브
        // 사거리 수(국소 화망). 내구 1·즉시 파괴, 손실 = 후보 순번부터 꼬리 끝까지.
        eventLogs.LogSwarmCutAttempt(
            matchingId, creditPlayerId, bestOwnerId, bestTailOrdinal,
            CountSwarmOrbGunsCovering(chains, cutterId, cutterArea, current),
            CountSwarmOrbGunsCovering(chains, cutterId, cutterArea, current, bestOwnerId),
            durabilityBeforeHit: 1,
            expectedOrbLoss: Math.Max(0, chains[bestOwnerId].Points.Count - bestTailOrdinal),
            breaksNow: true,
            area: bestArea.ToString());

        // 접미 절단 (스네이크 문법, "오브 절단면 다 깨지게" 결정): 밟힌 오브(몸체) 또는
        // 링크 뒤쪽 첫 오브부터 꼬리 끝까지 전부 사라진다. 절단 지점이 머리에 가까울수록 손실이 크다.
        var destroyedItems = orbTrails.DestroySwarmOrbsFromOrdinal(matchingId, bestOwnerId, bestTailOrdinal);
        if (destroyedItems.Count == 0)
            return;
        var destroyedItem = destroyedItems[0];
        // 반격 보호 개시 (#227 7단계): 방금 자른 그 사람은 1.2초 동안 이 피해자를 다시 못 자른다.
        OpenSwarmRetaliationWindow(matchingId, creditPlayerId, bestOwnerId, bestArea, nowUtc, allSessions);
        var ownerSession = aliveSessions.FirstOrDefault(session => session.PlayerId == bestOwnerId);
        var ownerChain = chains[bestOwnerId];
        foreach (var lost in destroyedItems)
        {
            matchRuntimes.GetRequired(matchingId).Swarm.TrailCombat.OrbDurabilityBonus.Remove((matchingId, bestOwnerId, lost.ItemUid));
            ownerSession?.SendOrbUpdate(lost);
        }
        // 절단 낙수 없음 (#232): 소환석·드롭·점수·웨이브 기여를 지급하지 않는다. 잃은 것은 그냥 사라진다.

        // 절단 파열 플래시: 링 + 잘린 꼬리 오브 섬광 — "어디부터 끊겼다"가 화면에서 읽히게.
        SendSwarmRingVfx(
            bestArea, creditPlayerId, bestOrbPosition.X, bestOrbPosition.Y,
            SwarmTrailCutFlashRadius, allSessions, SwarmRingVfxKindCut,
            victimId: bestOwnerId, fromOrdinal: bestTailOrdinal);

        // 공격자 치명상 (#232): 같은 사건으로 +35. 사람은 사격 피격 경로(체력 감소·피격 숫자)를 타고
        // 8초 수면 회복 차단이 걸린다. 봇은 체력만 감소한다.
        DateTime healLockUntil = nowUtc.AddSeconds(SwarmSingleCutHealLockSeconds);
        int cutterHealthAfter;
        if (cutterSession != null)
        {
            combatDamage.ApplyProximityAutoCombatHit(cutterSession,
                cutterId, cutterArea, destroyedItem.ItemId, SwarmSingleCutHealthCost);
            cutterSession.Condition.BlockHealingUntil(healLockUntil);
            cutterHealthAfter = cutterSession.CurrentHealth;
        }
        else if (cutterBot != null)
        {
            cutterBot.Health = Math.Max(
                0, cutterBot.Health - SwarmSingleCutHealthCost);
            cutterBot.LastDamagedAtUtc = nowUtc;
            matchRuntimes.GetRequired(matchingId).Swarm.BotTactics.LastDamagedAtUtc[(matchingId, cutterBot.PlayerId)] = nowUtc;
            // 봇 절단 시각 — 절단 자제 쿨다운(IsSwarmBotCutAllowed)과 절단 후 회수 창이 읽는다.
            matchRuntimes.GetRequired(matchingId).Swarm.BotTactics.LastTrailCutAtUtc[(matchingId, cutterBot.PlayerId)] = nowUtc;
            cutterHealthAfter = cutterBot.Health;
        }
        else
        {
            cutterHealthAfter = cutterHealthBefore;
        }

        var ownerBot = aliveBots.FirstOrDefault(candidate => candidate.PlayerId == bestOwnerId);
        if (ownerBot != null)
        {
            // 절단당한 봇은 피격 반응(도주 판단)으로 즉시 넘어간다.
            ownerBot.LastProximityAttackerPlayerId = creditPlayerId;
            ownerBot.LastDamagedAtUtc = nowUtc;
            matchRuntimes.GetRequired(matchingId).Swarm.BotTactics.LastDamagedAtUtc[(matchingId, ownerBot.PlayerId)] = nowUtc;
            ownerBot.CancelChannelHold();
        }

        // 절단 전후 대차대조 (#227 5단계): 오브 수(=점수)·순위·공격 기여 수를 한 줄에 묶는다.
        // "몇 개 잃음 → 순위가 바뀜 → 다음 화력이 줄었다"가 한 이벤트에서 확인돼야
        // 전략이 먹혔는지 로그만으로 판정할 수 있다. 전은 파괴 직전 체인, 후는 재조회다.
        int attackOrbsBefore = CountSwarmAttackOrbs(ownerChain.ItemIds);
        int orbsBefore = ownerChain.ItemIds.Count;
        int orbsAfter = orbTrails.CountSwarmSquadOrbs(matchingId, bestOwnerId);
        int attackOrbsAfter = CountSwarmAttackOrbs(GetSwarmOrbItemIdsInOrder(matchingId, bestOwnerId));
        int rankAfter = GetSwarmPlayerRank(matchingId, bestOwnerId, aliveSessions, aliveBots);

        // 잃은 만큼 소환 비용을 되돌린다 (#229): 오브 수가 곧 소환 카운터라, 잘려 나간 몫이
        // 값에 남으면 절단당한 쪽이 재건 비용까지 떠안아 격차가 한 방향으로만 벌어진다.
        matchRuntimes.GetRequired(matchingId).SummonStones.RefundGrowthSuccess(
            bestOwnerId, SwarmGrowthOfferState.CardMultiply, destroyedItems.Count);

        eventLogs.LogSwarmTrailCut(
            matchingId, creditPlayerId, bestOwnerId, bestTailOrdinal, destroyedItems.Count,
            orbsBefore, orbsAfter, attackOrbsBefore, attackOrbsAfter, rankAfter,
            bestArea.ToString());
        // 필수 로그 (#232 §11): 절단 한 건 = 공격자·피해자·절단 순번·잃은 수·공격자 오염 전후·회복 차단 만료.
        eventLogs.LogSystem(
            matchingId,
            $"ORB_TAIL_CUT attacker={creditPlayerId} victim={bestOwnerId} cutIndex={bestTailOrdinal} " +
            $"lostOrbs={destroyedItems.Count} firstOrbUid={destroyedItem.ItemUid} " +
            $"attackerHealthBefore={cutterHealthBefore} attackerHealthAfter={cutterHealthAfter} " +
            $"healLockUntil={healLockUntil:O} victimOrbsBefore={orbsBefore} victimOrbsAfter={orbsAfter} area={bestArea}");
        logger.LogInformation(
            "Swarm tail cut: MatchingId={MatchingId}, CutterId={CutterId}, OwnerId={OwnerId}, TailOrdinal={TailOrdinal}, Lost={Lost}, AttackerHealth={Before}->{After}",
            matchingId, cutterId, bestOwnerId, bestTailOrdinal, destroyedItems.Count,
            cutterHealthBefore, cutterHealthAfter);
    }

    // 링 연출 종류: 클라가 색·효과음을 분기한다. 크랙(3)은 링 없이 슬롯 크랙 + 크랙음만 —
    // Radius 필드에 단계(1~4)를 실어 보낸다.
    private const int SwarmRingVfxKindEncircle = 0;
    private const int SwarmRingVfxKindCut = OrbTrailService.CutVfxKind;
    private const int SwarmRingVfxKindWaveBomb = 2;
    // 반격 보호 (#227 7단계): 5 = 피해자 남은 꼬리의 유리 잔광 개시(Radius에 지속 초),
    // 6 = 그 절단자의 투사체가 잔광 앞에서 깨짐(피해 숫자 없음).
    private const int SwarmRingVfxKindRetaliationGuard = 5;
    private const int SwarmRingVfxKindRetaliationBlocked = 6;

    // 방어 카드와 샌드박스 초기 외피가 같은 보너스를 사용한다.
    private const int SwarmArmorDurabilityBonus = MatchGrowthService.ArmorDurabilityBonus;

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
                session.TrySend(packet);
        }
    }

    // ===== 파도 = 소용돌이 (#268): 파도 오브 각각이 주기(2초)마다 자기 열 위치에 소용돌이를 깐다 — 오브가 곧
    // 무기 위치라는 점에서 바람 회전 칼날과 같은 문법. 예고(0.65초 림 링) 후 반경 안 전원을 잠깐 늦춘다(침수) —
    // 피해는 타격 피드백 수준(1/4). 예고 원점은 스폰 순간 고정. 주기·예고의 원천은 swarm_config.csv (#335). =====
    private static double SwarmWaveBombIntervalSeconds =>
        SwarmConfigData.GetDouble("SWARM_WAVE_VORTEX_INTERVAL_SECONDS", 2d);
    private static double SwarmWaveBombFuseSeconds =>
        SwarmConfigData.GetDouble("SWARM_WAVE_VORTEX_FUSE_SECONDS", 0.65d);

    // 오브별 독립 시계("다같이 터지는 게 어색"). 파도 폭탄 상태(위상·대기열)는 matchRuntimes.GetRequired(matchingId).Swarm.TrailCombat.

    private void ProcessSwarmWaveBombs(
        long matchingId,
        DateTime nowUtc,
        List<SwarmParticipantSpatial> participants,
        List<GameClientSession> aliveSessions,
        List<BotPlayerState> aliveBots,
        List<GameClientSession> allSessions)
    {
        // 1) 기폭: 예약된 소용돌이 정산.
        for (int index = matchRuntimes.GetRequired(matchingId).Swarm.TrailCombat.PendingWaveBombs.Count - 1; index >= 0; index--)
        {
            var vortex = matchRuntimes.GetRequired(matchingId).Swarm.TrailCombat.PendingWaveBombs[index];
            if (vortex.MatchingId != matchingId || nowUtc < vortex.ExplodeAtUtc)
                continue;
            matchRuntimes.GetRequired(matchingId).Swarm.TrailCombat.PendingWaveBombs.RemoveAt(index);
            DetonateSwarmWaveVortex(matchingId, vortex.OwnerId, vortex.Area, vortex.Position,
                vortex.Damage, vortex.Radius, vortex.SourceItemId, nowUtc,
                participants, aliveSessions, aliveBots, allSessions);
        }

        // 2) 생성: 파도 오브 각각이 자기 시계(2초)로 자기 열 위치에 소용돌이를 깐다.
        // 오브 uid 기반 위상으로 첫 발동이 흩어져 일제사가 되지 않는다. 비무장은 쉰다.
        IReadOnlyList<SwarmArenaCombatTarget>? vortexTargets = null;
        foreach (var owner in participants)
        {
            var trailOrbs = GetSwarmTrailOrbs(matchingId, owner.PlayerId);
            if (trailOrbs.Count == 0)
                continue;

            float sunMultiplier = -1f;
            List<int>? tiers = null;
            for (int ordinal = 0; ordinal < trailOrbs.Count; ordinal++)
            {
                var item = trailOrbs[ordinal];
                if (!OrbData.TryGetColorAndTier(item.ItemId, out var color, out _) ||
                    color != OrbColor.Blue)
                    continue;

                var orbKey = (matchingId, owner.PlayerId, item.ItemUid);
                if (!matchRuntimes.GetRequired(matchingId).Swarm.TrailCombat.WaveBombNextDropAtUtc.TryGetValue(orbKey, out var nextDropAtUtc))
                {
                    // 고유 위상: 첫 발동을 0.5~1.5주기 사이에 흩뿌린다 — uid라 재접속에도 안정.
                    double phase = 0.5d + item.ItemUid % 977 / 977d;
                    matchRuntimes.GetRequired(matchingId).Swarm.TrailCombat.WaveBombNextDropAtUtc[orbKey] =
                        nowUtc.AddSeconds(SwarmWaveBombIntervalSeconds * phase);
                    continue;
                }

                if (nowUtc < nextDropAtUtc)
                    continue;

                float radius = OrbData.GetSwarmWaveBombRadius(item.ItemId);
                int baseDamage = OrbData.GetSwarmPveAttackDamage(item.ItemId);
                if (radius <= 0f || baseDamage <= 0)
                    continue;

                // 사거리 게이트 (유저 결정, 태양·바람과 동일): 소용돌이 반경 안에
                // 표적(몹 또는 소유자 아닌 플레이어)이 있어야 깐다. 없으면 시계를 소모하지 않고
                // 대기 — 표적이 들어오는 순간 바로 발동한다.
                tiers ??= orbTrails.GetSwarmOrbTiersInOrder(matchingId, owner.PlayerId);
                var orbPosition = orbTrails.GetSwarmOrbTrailPosition(
                    matchingId, owner.PlayerId, ordinal, owner.Position, tiers);
                vortexTargets ??= matchRuntimes.GetRequired(matchingId).Monsters.GetCombatTargets(matchingId);
                bool hasTarget = false;
                foreach (var target in vortexTargets)
                {
                    if (target.Area != owner.Area ||
                        !SwarmCombatGeometry.IsWithinGroundRadius(
                            orbPosition, target.Position, radius + SwarmCombatGeometry.MonsterRadius))
                        continue;
                    hasTarget = true;
                    break;
                }

                if (!hasTarget)
                {
                    foreach (var participant in participants)
                    {
                        if (participant.PlayerId == owner.PlayerId || participant.Area != owner.Area ||
                            !SwarmCombatGeometry.IsWithinGroundRadius(
                                orbPosition, participant.Position, radius + SwarmBotDodgePolicy.SwarmCrossfirePlayerRadius))
                            continue;
                        hasTarget = true;
                        break;
                    }
                }

                if (!hasTarget)
                    continue;

                // 비무장(소환·채집 중)이어도 시계는 돈다 — 칼날·미사일과 같은 규칙.
                matchRuntimes.GetRequired(matchingId).Swarm.TrailCombat.WaveBombNextDropAtUtc[orbKey] = nowUtc.AddSeconds(SwarmWaveBombIntervalSeconds);

                if (sunMultiplier < 0f)
                    sunMultiplier = OrbData.GetSunPveAttackMultiplier(trailOrbs);
                int damage = Math.Max(1, (int)MathF.Round(
                    baseDamage * sunMultiplier * Config.SWARM_WAVE_VORTEX_DAMAGE_MULTIPLIER));

                // 스폰 = 그 오브의 현재 열 좌표(사거리 게이트가 계산한 그 지점) — 스폰 순간 고정.
                matchRuntimes.GetRequired(matchingId).Swarm.TrailCombat.PendingWaveBombs.Add((
                    matchingId,
                    owner.PlayerId,
                    owner.Area,
                    orbPosition,
                    damage,
                    radius,
                    item.ItemId,
                    nowUtc.AddSeconds(SwarmWaveBombFuseSeconds)));
                SendSwarmRingVfx(owner.Area, owner.PlayerId, orbPosition.X, orbPosition.Y,
                    radius, allSessions, SwarmRingVfxKindWaveBomb,
                    victimId: 0, fromOrdinal: ordinal);
                eventLogs.LogSystem(
                    matchingId,
                    $"wave_vortex_spawn owner={owner.PlayerId} ordinal={ordinal} " +
                    $"at=({orbPosition.X:F2},{orbPosition.Y:F2}) radius={radius:F2} damage={damage}");
            }
        }
    }

    /// <summary>
    ///     소용돌이 기폭 (#268): 반경 안 전원(몹·플레이어 동일)에게 타격 피드백 수준의 피해와
    ///     "침수"(5초 25% 감속) 디버프를 준다. 변위(당김·밀침·원 밖 축출) 실험은 전부
    ///     기각(유저 판정). 플레이어는 충격 면역 창(0.9초)이 연쇄 피격을 막는다 —
    ///     면역이면 감속·피해 전부 없음.
    /// </summary>
    private void DetonateSwarmWaveVortex(
        long matchingId,
        long ownerId,
        AreaType area,
        Vector3f position,
        int damage,
        float radius,
        int sourceItemId,
        DateTime nowUtc,
        List<SwarmParticipantSpatial> participants,
        List<GameClientSession> aliveSessions,
        List<BotPlayerState> aliveBots,
        List<GameClientSession> allSessions)
    {
        var ownerSession = allSessions.FirstOrDefault(session => session.PlayerId == ownerId);
        float radiusSquared = radius * radius;
        int hitCount = 0;
        int notifiedCount = 0;
        // 몹: 착탄 지연 정산 파이프라인 재사용 — 킬 보상·상태 브로드캐스트가 따라온다.
        // 당김은 서버 위치를 즉시 옮긴다 — 클라 표시가 SmoothDamp로 따라가며 당김으로 읽힌다.
        foreach (var target in matchRuntimes.GetRequired(matchingId).Monsters.GetCombatTargets(matchingId))
        {
            if (target.Area != area)
                continue;
            float dx = target.Position.X - position.X;
            float dy = (target.Position.Y - position.Y) * 2f;
            if (dx * dx + dy * dy > radiusSquared)
                continue;
            int monsterDamage = combatDamage.RollSwarmCriticalDamage(matchingId, damage, out bool critical);
            matchRuntimes.GetRequired(matchingId).Monsters.ReserveMonsterDamage(matchingId, target.CombatTargetId, monsterDamage);
            matchRuntimes.GetRequired(matchingId).Monsters.RecordMonsterAttackEvent(matchingId, target.CombatTargetId);
            matchRuntimes.GetRequired(matchingId).Swarm.Pacing.PendingMonsterHits.Add(new PendingSwarmMonsterHit(
                matchingId, target.CombatTargetId, ownerId, monsterDamage, nowUtc));
            matchRuntimes.GetRequired(matchingId).Monsters.TrySlowMonster(
                matchingId, target.CombatTargetId, OrbData.WaveSlowSeconds, nowUtc);
            hitCount++;

            int monsterId = matchRuntimes.GetRequired(matchingId).Monsters.GetMonsterIdForCombatTarget(matchingId, target.CombatTargetId);
            if (monsterId <= 0)
                continue;

            notifiedCount++;
            combatDamage.SendMonsterHitNotification(ownerSession,
                monsterId, area, sourceItemId, monsterDamage, critical, showDamageOnly: true);
        }

        // 플레이어: 같은 반경(바닥면 타원) + 몸통 여유. 소유자 제외 — 침수 디버프 + 피해.
        int soaked = 0;
        foreach (var participant in participants)
        {
            if (participant.PlayerId == ownerId || participant.Area != area || participant.Position == null)
                continue;
            if (!SwarmCombatGeometry.IsWithinGroundRadius(position, participant.Position, radius + SwarmBotDodgePolicy.SwarmCrossfirePlayerRadius))
                continue;

            // 충격 면역 없음: 겹친 링에 다 맞는다 — 침수는 지속 갱신이라 중첩 무해.
            soaked++;
            combatDamage.ApplySwarmShock(matchingId, ownerId, sourceItemId, area, participant.PlayerId,
                "WAVE_VORTEX_HIT", aliveSessions, aliveBots, allSessions,
                Config.SWARM_WAVE_VORTEX_DAMAGE_MULTIPLIER);

            var victimSession = aliveSessions.FirstOrDefault(
                session => session.PlayerId == participant.PlayerId);
            if (victimSession != null)
            {
                // 사람: 클라가 감속을 적용하고 디버프 창에 침수를 띄운다.
                if (ownerId != 0)
                {
                    using var packet = PacketMaker.G_TO_C_STATUS_EFFECT(new()
                    {
                        SourcePlayerId = ownerId,
                        TargetPlayerId = participant.PlayerId,
                        AreaType = area,
                        Effect = CombatStatusEffectKind.WaveSlow,
                        DurationMs = (int)(OrbData.WaveSlowSeconds * 1000f)
                    });
                    victimSession.TrySend(packet);
                }
                continue;
            }

            var bot = aliveBots.FirstOrDefault(candidate => candidate.PlayerId == participant.PlayerId);
            if (bot != null)
                bot.WaveSlowUntilUtc = nowUtc.AddSeconds(OrbData.WaveSlowSeconds);
        }

        if (hitCount > 0 || soaked > 0)
        {
            eventLogs.LogSystem(
                matchingId,
                $"wave_vortex_hit owner={ownerId} area={area} monsters={hitCount} " +
                $"notified={notifiedCount} playersSoaked={soaked} radius={radius:F2} " +
                $"damage={damage} item={sourceItemId}");
        }
    }

    // ===== 절단 실험 더미 (#226): 매치의 봇 하나를 운동장 과녁으로 바꾼다 —
    // 정지·불사·오브 10개 일자 꼬리(자동 리필)·비무장·몹 절단 면제. 웨이브 디렉터 제외.
    // 명시적 분리 (단계 0): DEV_CUT_DUMMY=1 환경변수 옵트인 — 일반 매치는 순정으로 돈다. =====
    private bool SwarmCutDummyAutoSetup => devOptions.CutDummy;

    // 교차사격 샌드박스 (#232 2단계): DEV_CROSSFIRE_SANDBOX=1 — 절단 실험장과 같은 격리
    // (운동장 더미 하나 + 나머지 봇 퇴장)를 쓰되(몹 접촉 피해는 켜 둔다), 더미는 태양 T1 3개·철갑
    // 없음·무장(몹을 쏜다)이다. 사람 오브도 무장 — 실험 대상이 절단 궤적이 아니라
    // 몹을 향한 사격이 만드는 직선이기 때문이다. 같은 플래그로 MatchSpawnPlanner가 전원을 운동장에 스폰한다.
    private bool SwarmCrossfireSandbox => devOptions.CrossfireSandbox;
    private bool SwarmDummySandboxActive => SwarmCutDummyAutoSetup || SwarmCrossfireSandbox;
    private const int SwarmCutDummyOrbCount = 10;
    private const int SwarmCrossfireDummyOrbCount = 3;
    private int SwarmDummyOrbCount => SwarmCrossfireSandbox ? SwarmCrossfireDummyOrbCount : SwarmCutDummyOrbCount;
    // 과녁 열은 단색 태양 T1 — 시작 지급의 무작위 색이 섞이면 파도(물폭탄)가 딸려 온다.
    private const int SwarmCutDummyOrbItemId = 107000010;
    // 절단 직후 3초는 비워 둔다 — 즉시 채우면 "끊어도 안 줄어드는" 것처럼 보인다.
    private const double SwarmCutDummyRefillDelaySeconds = 3d;

    /// <summary>
    ///     더미 오브 리필 (#227 수리): 판단 기준은 오브 수 — 열이 줄어든 걸 본 시점부터
    ///     3초를 세고 채운다. 피격 시각 기준이던 시절엔 PvP 미사일이 스탬프를 매 발 갱신해
    ///     유예가 끝나지 않았다(리필 정지).
    /// </summary>
    private void ProcessSwarmCutDummyRefill(long matchingId, BotPlayerState dummy, DateTime nowUtc)
    {
        var key = (matchingId, dummy.PlayerId);
        if (orbTrails.CountSwarmSquadOrbs(matchingId, dummy.PlayerId) >= SwarmDummyOrbCount)
        {
            matchRuntimes.GetRequired(matchingId).Swarm.Pacing.CutDummyRefillAtUtc.Remove(key);
            return;
        }

        if (!matchRuntimes.GetRequired(matchingId).Swarm.Pacing.CutDummyRefillAtUtc.TryGetValue(key, out var refillAtUtc))
        {
            matchRuntimes.GetRequired(matchingId).Swarm.Pacing.CutDummyRefillAtUtc[key] = nowUtc.AddSeconds(SwarmCutDummyRefillDelaySeconds);
            return;
        }

        if (nowUtc < refillAtUtc)
            return;

        matchRuntimes.GetRequired(matchingId).Swarm.Pacing.CutDummyRefillAtUtc.Remove(key);
        RefillSwarmCutDummyOrbs(matchingId, dummy);
    }

    /// <summary>봇 플래그 조회 — 참가자 id가 더미인지. 사람(양수)은 항상 false.</summary>
    private bool IsSwarmCutDummyPlayer(long matchingId, long playerId)
    {
        if (playerId >= 0)
            return false;
        foreach (var bot in matchRuntimes.GetRequired(matchingId).Bots.GetBots(matchingId))
        {
            if (bot.PlayerId == playerId)
                return bot.IsSwarmCutDummy;
        }

        return false;
    }

    private void RefillSwarmCutDummyOrbs(long matchingId, BotPlayerState dummy)
    {
        for (int index = orbTrails.CountSwarmSquadOrbs(matchingId, dummy.PlayerId);
             index < SwarmDummyOrbCount;
             index++)
            matchRuntimes.GetRequired(matchingId).Inventory.TryAddItemWithCapacity(
                dummy.PlayerId, SwarmCutDummyOrbItemId, Config.SWARM_ORB_CAPACITY, out _);

        // 교차사격 샌드박스는 철갑을 안 씌운다 — 절단이 꺼져 있어 내구는 의미가 없다.
        if (SwarmCrossfireSandbox)
            return;

        // 실험 과녁 (#227): 머리쪽 절반은 방어 강화(5/5), 나머지 절반은 맨 오브(1/5) —
        // 같은 열에서 두 내구를 나란히 밟아 비교할 수 있다.
        var trailOrbs = GetSwarmTrailOrbs(matchingId, dummy.PlayerId);
        int armoredCount = (trailOrbs.Count + 1) / 2;
        for (int ordinal = 0; ordinal < trailOrbs.Count; ordinal++)
        {
            var key = (matchingId, dummy.PlayerId, trailOrbs[ordinal].ItemUid);
            if (ordinal < armoredCount)
                matchRuntimes.GetRequired(matchingId).Swarm.TrailCombat.OrbDurabilityBonus[key] = SwarmArmorDurabilityBonus;
            else
                matchRuntimes.GetRequired(matchingId).Swarm.TrailCombat.OrbDurabilityBonus.Remove(key);
        }
    }

    /// <summary>
    ///     지정한 매치의 절단 실험 더미를 설정한다. 봇 하나를 운동장 중앙 동쪽에 고정하고
    ///     서쪽으로 일자 꼬리를 심는다. 같은 봇에 재호출하면 위치·꼬리를 재정렬한다.
    /// </summary>
    public object SetupSwarmCutDummy(long matchingId)
    {

        if (matchingId <= 0)
            return new { error = "no active match" };

        if (!matchRuntimes.Enter(matchingId, out MatchScope scope))
            return new { error = "match is no longer active " + matchingId };

        using (scope)
        {
            if (scope.Runtime.IsTerminal)
                return new { error = "match is no longer active " + matchingId };

            object result = SetupSwarmCutDummyCore(matchingId, out BotMovementEvent? movement);
            if (movement != null)
                botMovement.DispatchExternalMovement(scope.Runtime, movement);
            return result;
        }
    }

    private object SetupSwarmCutDummyCore(long matchingId, out BotMovementEvent? movement)
    {
        movement = null;
        var bots = matchRuntimes.GetRequired(matchingId).Bots.GetBots(matchingId)
            .Where(bot => !bot.IsEliminated).ToList();
        var dummy = bots.FirstOrDefault(bot => bot.IsSwarmCutDummy) ?? bots.FirstOrDefault();
        if (dummy == null)
            return new { error = "no alive bot in match " + matchingId };

        var groundCell = GameMapData.GetAreaSpawnCell(Config.SWARM_MATCH_MAP, Config.SWARM_MATCH_GROUND_AREA);
        var center = BotPlayerManager.CellToWorldPosition(Config.SWARM_MATCH_MAP, groundCell);
        var fromArea = dummy.CurrentArea;
        var fromCell = dummy.Cell;
        dummy.IsSwarmCutDummy = true;
        dummy.CurrentArea = Config.SWARM_MATCH_GROUND_AREA;
        dummy.Position = new Vector3f(center.X + 4f, center.Y + 3f, 0f);
        dummy.Cell = MapCoordinateConverter.WorldToCell(Config.SWARM_MATCH_MAP, dummy.Position);
        dummy.Path.Clear();
        dummy.PathIndex = 0;
        dummy.Health = Config.MAX_HEALTH;

        // 꼬리: 동→서 일자 경로를 미리 심는다 — points[0] = 현재 위치(최신).
        var trailPoints = new List<Vector3f>();
        for (float distance = 0f; distance <= 12f; distance += 0.3f)
            trailPoints.Add(new Vector3f(dummy.Position.X - distance, dummy.Position.Y, 0f));
        matchRuntimes.GetRequired(matchingId).Swarm.TrailCombat.OrbTrails[(matchingId, dummy.PlayerId)] = trailPoints;
        matchRuntimes.GetRequired(matchingId).Swarm.TrailCombat.TrailLastTickPositions[(matchingId, dummy.PlayerId)] =
            new Vector3f(dummy.Position.X, dummy.Position.Y, 0f);
        // 시작 지급의 무작위 색(파도 포함)을 비우고 단색 태양 열로 재구성한다 (#227).
        matchRuntimes.GetRequired(matchingId).Inventory.TakeAllItems(dummy.PlayerId);
        matchRuntimes.GetRequired(matchingId).Swarm.Pacing.CutDummyRefillAtUtc.Remove((matchingId, dummy.PlayerId));
        RefillSwarmCutDummyOrbs(matchingId, dummy);

        movement = new BotMovementEvent
        {
            BotPlayerId = dummy.PlayerId,
            FromArea = fromArea,
            ToArea = Config.SWARM_MATCH_GROUND_AREA,
            FromCell = fromCell,
            ToCell = dummy.Cell,
            Position = dummy.Position,
            Velocity = new Vector3f(0f, 0f, 0f),
            Rotation = 0f,
            IsAreaTransition = fromArea != Config.SWARM_MATCH_GROUND_AREA
        };
        logger.LogInformation(
            "Swarm cut dummy ready: MatchingId={MatchingId}, DummyId={DummyId}, Position=({X},{Y})",
            matchingId, dummy.PlayerId, dummy.Position.X, dummy.Position.Y);
        return new
        {
            matchingId,
            dummyId = dummy.PlayerId,
            x = dummy.Position.X,
            y = dummy.Position.Y,
            orbs = SwarmDummyOrbCount
        };
    }





    /// <summary>
    ///     열 순번부터 꼬리 끝까지 인벤토리에서 즉시 파괴한다 (스네이크 문법). 순번 매핑은
    ///     전투 액터·클라 슬롯과 같은 인벤토리 순서(스쿼드 오브 필터).
    /// </summary>

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
    ///     반경 내 라이벌 탐색 — 티어 가중 전력 기준. 동수 이상인 최근접(강자)과 확실히 약한
    ///     (×1.25 우위) 최근접(약자)을 함께 찾는다. 빈손이면 살아있는 몹도 강자로 취급한다.
    /// </summary>

    private void ApplySwarmParticipantDamage(
        long matchingId,
        SwarmPlayerDamage damage,
        List<GameClientSession> aliveSessions,
        List<BotPlayerState> aliveBots,
        List<GameClientSession> allSessions)
    {
        // 받는 피해 배율(봇·플레이어 전부 1/3만 받게 결정): 사람·봇·로그가 전부 같은 값을 보게 진입점에서 한 번
        // 줄인다. 페이즈 곡선은 그대로 두고 배율만 곱한다 — 곡선을 고치면 "위협이 세지는 리듬"까지 다시 잡아야 한다.
        damage = damage with { Damage = Config.ScaleSwarmDamageTaken(damage.Damage) };

        // 파도 문양 몹 공격 연출: 접촉 강타가 주변까지 튀므로 같은 구역 전원에게 공격 VFX를 쏴
        // 몸 기울임이 출처를 말하게 한다 (보스 투사체 분기는 #335에서 삭제 — 보스 스폰 경로 없음).
        if (matchRuntimes.GetRequired(matchingId).Monsters.IsWavePatternMonster(matchingId, damage.MonsterId))
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
                    vfxSession.TrySend(vfxPacket);
            }
        }

        var session = aliveSessions.FirstOrDefault(candidate =>
            candidate.PlayerId == damage.TargetPlayerId);
        if (session != null)
        {
            // 피격은 수면을 깨지 않는다 — 자면서 맞는 건 본인의 선택이다.
            // 3초 진입 잠금만 찍어 맞자마자 새로 눕는 것은 계속 막는다.
            session.Condition.MarkSwarmCombat(DateTime.UtcNow);
            // #229: 문 게이지도 같이 끊는다 — 문 앞을 비우지 못하면 방을 못 연다.
            session.BreakDoorUnlockGauge();
            // 오염 경로 — 체력 감소·피격 피드백·일반 탈락 흐름까지 담당한다.
            combatDamage.ApplySwarmAfterimageMonsterHit(session, damage.MonsterId, damage.Damage);
            return;
        }

        var bot = aliveBots.FirstOrDefault(candidate => candidate.PlayerId == damage.TargetPlayerId);
        if (bot == null)
            return;

        // 반올림으로 맞춘다 (#229 4단계-보정): 잘라내기라 raw 6(배율 통과 3)이 1로, raw 8(4)이
        // 2로 뭉개져 페이즈별 접촉 곡선이 봇에게는 통째로 평평했다. 사람 경로는 Round를 쓴다.
        int botDamage = Math.Max(1, (int)MathF.Round(damage.Damage * SwarmBotContactDamageMultiplier));
        int legacyBefore = bot.Health;
        bot.Health = Math.Max(0, bot.Health - botDamage);
        matchRuntimes.GetRequired(matchingId).Swarm.BotTactics.LastDamagedAtUtc[(matchingId, bot.PlayerId)] = DateTime.UtcNow;
        // 세 번째 봇 경로도 남긴다 — 앞의 두 경로만 로그를 붙여 놓으면 여기로 빠진 피해가
        // 그대로 안 보인다.
        eventLogs.LogSwarmAfterimageHit(
            matchingId, damage.MonsterId, bot.PlayerId, damage.Area.ToString(),
            botDamage, legacyBefore, bot.Health,
            bot.Health <= 0, isBot: true, DateTimeOffset.UtcNow);
    }

    /// <summary>
    ///     5분 점수 만료 판정 (#226 단계 B): 개전 후 5분이 지나면 생존자 중 오브 최다
    ///     보유자가 승리한다. 동점은 총 티어 합 → (철갑, 단계 C 예정) → 본체 게이지(오염
    ///     낮은 쪽) → PlayerId 낮은 쪽. 단독 생존 조기 종료와 같은 MatchEliminationService.EndMatch
    ///     경로라 결과 화면도 같다. 잼 승점(#222 M3-2)은 퇴역.
    /// </summary>
    private bool ProcessSwarmScoreTimeout(
        long matchingId,
        DateTime nowUtc,
        List<GameClientSession> sessions,
        List<GameClientSession> aliveSessions,
        List<BotPlayerState> aliveBots)
    {
        if (devOptions.DisableGameEnd || matchRuntimes.GetRequired(matchingId).Swarm.Pacing.TimeoutEndedMatchings.Contains(matchingId))
            return false;

        var startedAtUtc = MatchStartGate.GetGameplayStartedAtUtc(matchingId);
        if (startedAtUtc == null)
        {
            // 봇 전용 매치(어드민 검증)는 게이트가 없다 — 스웜 첫 틱을 앵커로 대신 쓴다.
            if (!matchRuntimes.GetRequired(matchingId).Swarm.Pacing.MatchFallbackAnchorUtc.TryGetValue(matchingId, out var fallbackAnchor))
            {
                matchRuntimes.GetRequired(matchingId).Swarm.Pacing.MatchFallbackAnchorUtc[matchingId] = nowUtc;
                return false;
            }
            startedAtUtc = fallbackAnchor;
        }

        if ((nowUtc - startedAtUtc.Value).TotalSeconds < Config.SWARM_MATCH_DURATION_SECONDS)
            return false;

        var candidates = aliveSessions
            .Where(session => session.PlayerId.HasValue)
            .Select(session => (
                PlayerId: session.PlayerId!.Value,
                Health: session.CurrentHealth))
            .Concat(aliveBots.Select(bot => (bot.PlayerId, bot.Health)))
            .Select(candidate =>
            {
                var (orbCount, tierSum) = GetSwarmOrbScore(matchingId, candidate.PlayerId);
                return (candidate.PlayerId, OrbCount: orbCount, TierSum: tierSum,
                    candidate.Health);
            })
            .OrderByDescending(candidate => candidate.OrbCount)
            .ThenByDescending(candidate => candidate.TierSum)
            // 철갑(내구 보너스 합) 3차 키 (#226): 같은 열이면 방어 투자한 쪽이 앞선다.
            .ThenByDescending(candidate => matchRuntimes.GetRequired(matchingId).Swarm.TrailCombat.OrbDurabilityBonus
                .Where(pair => pair.Key.MatchingId == matchingId &&
                               pair.Key.PlayerId == candidate.PlayerId)
                .Sum(pair => pair.Value))
            .ThenByDescending(candidate => candidate.Health)
            .ThenBy(candidate => candidate.PlayerId)
            .ToList();
        long winnerId = candidates.Count > 0 ? candidates[0].PlayerId : 0;
        matchRuntimes.GetRequired(matchingId).Swarm.Pacing.TimeoutEndedMatchings.Add(matchingId);
        // 최종 점수표 (#226 F 계측): 순위 순 pid:오브:티어합 — 300초 목표(1위 11~15) 검증 근거.
        eventLogs.LogSystem(
            matchingId,
            "match_score_result " + string.Join(",", candidates.Select(candidate =>
                $"{candidate.PlayerId}:{candidate.OrbCount}:{candidate.TierSum}")));
        logger.LogInformation(
            "Swarm score timeout: MatchingId={MatchingId}, WinnerId={WinnerId}, WinnerOrbs={WinnerOrbs}, WinnerTierSum={WinnerTierSum}, Alive={AliveCount}",
            matchingId, winnerId,
            candidates.Count > 0 ? candidates[0].OrbCount : 0,
            candidates.Count > 0 ? candidates[0].TierSum : 0,
            candidates.Count);

        if (sessions.Any(session => !session.IsGameEnded))
        {
            matchEliminations.EndMatch(matchingId, winnerId, "orb_score_timeout");
            matchRuntimes.Get(matchingId)?.Combat.Clear();
            return true;
        }

        matchCleanup.EndBotOnlyMatchIfSettled(matchingId, winnerId);
        return true;
    }

    /// <summary>
    ///     오브 점수 헬퍼 (#226 단계 B): 궤도 열의 오브 수와 총 티어 합. 사람·봇 공통
    ///     (봇도 같은 인게임 인벤토리를 쓴다).
    /// </summary>
    private (int OrbCount, int TierSum) GetSwarmOrbScore(long matchingId, long playerId) =>
        matchRuntimes.GetRequired(matchingId).GetOrbScore(playerId);

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
        bool isFirstBroadcast = !matchRuntimes.GetRequired(matchingId).Swarm.Pacing.JamRankingsSignature.TryGetValue(matchingId, out var previous);
        if (!isFirstBroadcast && previous == signature)
            return;

        matchRuntimes.GetRequired(matchingId).Swarm.Pacing.JamRankingsSignature[matchingId] = signature;
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
            session.TrySend(packet);
    }

    /// <summary>열 순서의 오브 목록 — 강화·철갑의 "가장 앞" 판정과 트레일 순번의 단일 출처.</summary>
    private List<InGameItemInfo> GetSwarmTrailOrbs(long matchingId, long playerId) =>
        matchRuntimes.GetRequired(matchingId).Inventory.GetPlayerInventory(playerId).GetOrderedOrbs();

    private static int GetSquadOrbTier(int itemId)
    {
        if (OrbData.TryGetColorAndTier(itemId, out _, out int tier))
            return tier;
        return OrbData.TryGetRecoveryTier(itemId, out int recoveryTier) ? recoveryTier : 0;
    }

    /// <summary>
    ///     PvP 미사일 적용 (#226 재개편): 오브 HP·본체 보호 퇴역 — 모든 발은 본체 체력으로
    ///     환산(이월 누산)되어 직행한다. 오브 파괴는 열 절단 전용. 옛 PvpDamageScale(0.65)은
    ///     오염 환산 상수가 대체해 퇴역했다 (#325에서 상수 삭제).
    /// </summary>
    private int ApplySwarmPvpAttack(
        long matchingId,
        ProximityCombatAttack attack,
        List<GameClientSession> aliveSessions,
        List<BotPlayerState> aliveBots,
        List<GameClientSession> allSessions,
        bool broadcastVfx = true,
        bool sendAttackerFeedback = true)
    {
        // 반격 보호 (#227 7단계): 방금 이 표적의 꼬리를 자른 공격자의 본체 피해는 통과하지 못한다.
        // 착탄 시점에 보므로 창이 열리기 '전에' 발사된 대기 투사체도 함께 걸린다.
        // 제3자·잔상·폐쇄는 이 경로를 타지 않아 종전대로 들어간다.
        var nowUtc = DateTime.UtcNow;
        if (matchRuntimes.GetRequired(matchingId).Swarm.TrailCombat.CutRetaliationWindows.TryGetValue(
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

        int healthDamage = ConsumeSwarmPvpDamage(matchingId, attack.TargetPlayerId, attack.Damage);
        var attackerSession = allSessions.FirstOrDefault(session => session.PlayerId == attack.AttackerPlayerId);
        int attackerHealth = attackerSession?.CurrentHealth ?? aliveBots.FirstOrDefault(bot => bot.PlayerId == attack.AttackerPlayerId)?.Health ?? -1;
        int targetHealth;
        var targetSession = aliveSessions.FirstOrDefault(session =>
            session.PlayerId == attack.TargetPlayerId);
        if (targetSession != null)
        {
            if (healthDamage > 0)
            {
                combatDamage.ApplyProximityAutoCombatHit(targetSession,
                    attack.AttackerPlayerId, attack.Area, attack.WeaponItemId, healthDamage, sourceHealth: attackerHealth);
            }
            targetHealth = targetSession.CurrentHealth;
        }
        else
        {
            var bot = aliveBots.FirstOrDefault(candidate => candidate.PlayerId == attack.TargetPlayerId);
            if (bot == null)
                return 0;

            // 오염이 0으로 이월돼도 "피격 중" 스탬프는 매 발 — 피격 반응 판단의 입력.
            bot.LastProximityAttackerPlayerId = attack.AttackerPlayerId;
            matchRuntimes.GetRequired(matchingId).Swarm.BotTactics.LastDamagedAtUtc[(matchingId, bot.PlayerId)] = DateTime.UtcNow;
            bot.LastDamagedAtUtc = DateTime.UtcNow;
            if (healthDamage > 0)
            {
                // 킬 크레딧 (#226 F 계측): 봇 표적도 사람 표적과 같은 피격 로그를 남긴다 —
                // 이게 빠지면 사람이 봇을 잡아도 killCount·totalDamageDealt가 0으로 남는다.
                eventLogs.LogHit(
                    matchingId, attack.AttackerPlayerId, bot.PlayerId, attack.WeaponItemId,
                    healthDamage,
                    bot.Health > 0 &&
                    bot.Health - healthDamage <= 0,
                    BotPlayerManager.IsBotPlayerId(attack.AttackerPlayerId), DateTimeOffset.UtcNow);
                bot.Health = Math.Max(0, bot.Health - healthDamage);
            }
            targetHealth = bot.Health;
        }

        if (healthDamage > 0 && sendAttackerFeedback)
        {
            combatDamage.SendPlayerHitNotification(attackerSession,
                attack.TargetPlayerId, attack.Area, attack.WeaponItemId, healthDamage, targetHealth);
        }
        // 태양 착탄(#226)은 발사 시점에 이미 연출을 쐈다 — 이중 투사체 방지.
        if (broadcastVfx)
            BroadcastSwarmAttackVfxToTargetAndObservers(attack, allSessions);
        return healthDamage;
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
                CombatActorFactory.TryCreateSpatialActor(
                    session.PlayerId.Value,
                    session.CurrentMapId,
                    session.CurrentArea,
                    session.LastValidatedPosition,
                    out var spatial))
            {
                AddSwarmParticipantCombatActors(actors, matchingId, spatial, nowUtc);
            }
        }

        MapId botMapId = matchRuntimes.GetRequired(matchingId).Bots.GetMatchingMapId(matchingId);
        foreach (var bot in aliveBots)
        {
            if (CombatActorFactory.TryCreateSpatialActor(bot.PlayerId, botMapId, bot.CurrentArea, bot.Position, out var botSpatial))
                AddSwarmParticipantCombatActors(actors, matchingId, botSpatial, nowUtc);
        }

        foreach (var target in matchRuntimes.GetRequired(matchingId).Monsters.GetCombatTargets(matchingId))
        {
            actors.Add(new ProximityCombatActor(
                target.CombatTargetId,
                target.Area,
                target.Position,
                0,
                0f,
                0,
                0f,
                MapId: Config.SWARM_MATCH_MAP,
                Cell: ProximityCombatLineOfSight.WorldPositionToCell(Config.SWARM_MATCH_MAP, target.Position),
                IsMonsterTarget: true,
                // SB 타겟 규칙 (#219): 적 오브(0) > 적 본체(1) > 몬스터(2)
                TargetPriority: 2));
        }

        return actors;
    }

    /// <summary>
    ///     보드의 오브가 곧 화력이다. 오브가 있으면 오브별 공격 문법(기존 인벤토리 액터)을
    ///     스웜 배율로 얹고, 없을 때만 기본 공격 하나로 싸운다 — 드래프트가 성장 체감이 되게.
    ///     비무장(실험 더미)이면 모든 공격 액터의 데미지를 0으로 눕힌다 — 리졸버가 공격자에서
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
        // 교차사격 샌드박스(#232)는 반대다 — 더미도 몹을 쏴야 그 직선이 나를 지나는 장면이 나온다.
        bool armed = !SwarmCutDummyAutoSetup &&
                     (SwarmCrossfireSandbox || !IsSwarmCutDummyPlayer(matchingId, spatial.PlayerId));
        // 본체 우선(1). 동급(2)이면 최근접이 이기는데 구역당 몹이 8~28마리라 항상 몹이 더 가깝고, 표적 고정이
        // 걸려 죽으면 또 다음 몹을 문다 — 사람은 표적이 될 기회조차 없다. PvP 사거리가 구역 전체를 덮던 시절의
        // "적 플레이어가 있는 한 몹이 영영 표적이 안 된다"는 사거리가 짧아진 지금은 성립하지 않는다 — 적이 코앞에
        // 붙었을 때만 우선권을 가져간다.
        // 오브 액터는 발사 원점일 뿐 표적이 아니다(Untargetable).
        var fallback = CreateSwarmParticipantActor(spatial, armed) with
        {
            // 0 = 최상위 (유저 판정: 범위 안이면 사람 먼저 무조건).
            // 사거리 판정을 이미 필터가 하므로, 후보에 올라온 사람은 곧 사정권 안이다.
            TargetPriority = 0
        };
        var inventory = matchRuntimes.GetRequired(matchingId).Inventory.GetPlayerInventory(spatial.PlayerId);
        var inventoryItems = inventory.GetAllItems().Where(item => item.Count > 0).ToList();
        if (inventoryItems.Count == 0)
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
        CombatActorFactory.AddInventoryCombatActors(
            actors,
            fallback with
            {
                AttackRange = 0f,
                Damage = 0,
                AttackIntervalSeconds = 0f
            },
            inventory);
        // #229: 태양·바람은 티어별 원시 피해·주기·탄속이 같은 유도탄이다. 차이는 보드
        // 패시브뿐이며, 태양 보너스는 모든 PvE 공격에 적용된다. 파도는 별도 물폭탄 시스템.
        float sunAttackMultiplier = OrbData.GetSunPveAttackMultiplier(inventoryItems);
        var actorTiers = orbTrails.GetSwarmOrbTiersInOrder(matchingId, spatial.PlayerId);
        int orbCount = actors.Count - before;
        long nowUnixMs = (long)(nowUtc - DateTime.UnixEpoch).TotalMilliseconds;
        for (int index = before; index < actors.Count; index++)
        {
            var actor = actors[index];
            // 오브열 (#226 α+): 공격 원점·피격 위치 = 각 오브의 열 좌표 — 표시가 곧 판정.
            // 오브마다 제 자리에서 가장 가까운 몹을 고르고, 예고선은 그 오브에서 나간다.
            var trailPosition = orbTrails.GetSwarmOrbTrailPosition(
                matchingId, spatial.PlayerId, index - before, spatial.Position, actorTiers);
            actor = actor with
            {
                Position = trailPosition,
                Cell = ProximityCombatLineOfSight.WorldPositionToCell(Config.SWARM_MATCH_MAP, trailPosition),
                // 열 순번을 실어 보낸다 — PvP 참여 오브를 앞열 N개로 끊는 근거.
                TrailOrdinal = index - before
            };
            // 오브는 표적이 아니다 (#226 재개편): 발사 원점으로만 존재 — 파괴는 절단 전용.
            actor = actor with { Untargetable = true };
            OrbData.TryGetColorAndTier(actor.WeaponItemId, out var orbColor, out _);
            if (orbColor is OrbColor.Blue or OrbColor.Green)
            {
                // 파도: 미사일을 쏘지 않는다 — 물폭탄(별도 주기)이 화력이다.
                // 바람: 조준 투사체가 없다 — 회전 칼날(반경 주기 틱, ProcessSwarmWindBlades)이 화력이다.
                actors[index] = actor with { Damage = 0 };
                continue;
            }

            // 태양 = 큰 공격 한 번 (#232 2단계): 유도탄 두 발 몫을 한 번에 — 주기 ×2, 피해 ×2.
            // 총 화력은 같고, 예고 → 쓸기 한 사이클이 유도탄 연사보다 읽히는 무게를 갖는다.
            bool crossfireSun = CrossfireService.IsSwarmCrossfireSun(actor.WeaponItemId);
            float crossfireDamageMultiplier = crossfireSun ? Config.SWARM_CROSSFIRE_SUN_DAMAGE_MULTIPLIER : 1f;
            float crossfireCadenceMultiplier = crossfireSun ? Config.SWARM_CROSSFIRE_SUN_CADENCE_MULTIPLIER : 1f;
            OrbData.TryGetColorAndTier(actor.WeaponItemId, out _, out int actorTier);
            // 태양 티어 사거리는 표적 획득에만 쓴다. 발사된 직선은 구역 경계(벽)까지 진행한다.
            float actorAttackRange = crossfireSun
                ? Config.SWARM_CROSSFIRE_SUN_RANGE_BY_TIER[Math.Clamp(actorTier, 1, 3) - 1]
                : SwarmPveSameAreaAttackRange;
            actors[index] = actor with
            {
                Damage = armed
                    ? Math.Max(1, (int)MathF.Round(
                        OrbData.GetSwarmPveAttackDamage(actor.WeaponItemId) *
                        sunAttackMultiplier * crossfireDamageMultiplier))
                    : 0,
                // 오브마다 제 박자를 준다 (유저 판정: 일제사가 어색하다).
                // 전 오브가 같은 주기를 쓰면 한 번에 쏘고 한 번에 쉬는 호흡이 되어, 서로 다른
                // 시기에 붙은 오브들이 한 몸처럼 읽힌다. 슬롯마다 주기를 ±12% 흔들어
                // 몇 발 만에 위상이 벌어지게 한다 — 평균 주기는 그대로라 화력 총량은 불변이고,
                // 표적이 죽어 재조준이 겹쳐도 다시 흩어진다.
                // 초기 지연으로 어긋내지 않는 이유: 재조준마다 그 지연을 다시 물어 DPS가 깎인다.
                AttackIntervalSeconds = OrbData.GetSwarmPveAttackIntervalSeconds(
                    actor.WeaponItemId) * ResolveSwarmOrbCadenceJitter(index - before) *
                    crossfireCadenceMultiplier,
                InitialAttackDelaySeconds = 0f,
                // 이 공용 actor는 잔상 PvE에만 쓰인다. PvP 국소 사거리는 별도 공격 사건에서
                // 오브별 원점을 기준으로 판정하므로, PvE의 같은 구역 사냥 범위는 유지한다.
                AttackRange = actorAttackRange
            };
        }

        // 발사 순서 공평화 (#232 교차사격 예고 상한): 리졸버는 목록 순서로 공격자를 돌아 자리를 배정하므로, 상한이
        // 찬 동안 앞 순번 오브만 계속 쏘고 뒤 순번은 굶는다. 순번·자리는 이미 박혔으니 목록 순서만 틱마다 돌린다.
        if (orbCount > 1)
        {
            int rotation = (int)(nowUnixMs / 50 % orbCount);
            if (rotation > 0)
            {
                var rotated = new ProximityCombatActor[orbCount];
                for (int offset = 0; offset < orbCount; offset++)
                    rotated[offset] = actors[before + (offset + rotation) % orbCount];
                for (int offset = 0; offset < orbCount; offset++)
                    actors[before + offset] = rotated[offset];
            }
        }
    }

    /// <summary>
    ///     슬롯별 주기 배율 (0.88~1.12). 황금비 계단으로 흩어 몇 개가 붙든 값이 뭉치지 않게 한다.
    ///     슬롯 인덱스만 보므로 같은 자리의 오브는 판 내내 같은 박자를 유지한다.
    /// </summary>
    private static float ResolveSwarmOrbCadenceJitter(int slotIndex)
    {
        float phase = slotIndex * 0.6180339f;
        phase -= MathF.Floor(phase);
        return 0.88f + phase * 0.24f;
    }

    // 국소 화망 (#227): 사거리가 구역 전체를 덮으면 후미 절단과 머리 절단의 위험이 같다 — 어디를 자르든 상대의 모든
    // 오브가 사정권이기 때문. 6.0이면 각 오브가 자기 열 좌표 주변만 덮으므로, 깊게 자를수록 앞열 여러 오브의
    // 사거리가 겹치는 자리로 들어가야 한다. 위험을 수치가 아니라 공간이 만든다.
    private const float SwarmSunAttackRange = 6f;
    // 바람 사거리 = 태양과 동일: 색 차이는 거리표가 아니라 리듬(연사 vs 한 방)과 탄속이 만든다.
    private const float SwarmWindAttackRange = SwarmSunAttackRange;
    // PvE 사거리 = 등장 거리(SupplyOffscreenDistance 7) — 뱀서라이크의 무기가 그렇듯 화면에 들어온 것만 친다.
    // 구역 전체를 덮으면 화면 밖에 선 몹이 등장하는 순간부터 계속 맞으며 걸어와 도착 전에 죽으니 접촉이 성립하지
    // 않고 몹은 위협이 아니라 자원 자판기가 된다. 걸어오는 1.6초가 화망을 통과하는 시간이고, 페이즈가 올라
    // HP가 두꺼워질수록 실제로 도달하는 몹이 늘어난다.
    private const float SwarmPveSameAreaAttackRange = 7f;

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
        if (!OrbData.TryGetColorAndTier(itemId, out var color, out _))
            return 0f;
        return color switch
        {
            OrbColor.Red => SwarmSunAttackRange,
            OrbColor.Green => SwarmWindAttackRange,
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

    // #238: 스팟 아레나 파셜 퇴역 시 현행 스웜이 실사용하던 헬퍼 2종을 이관.
    private static void BroadcastSwarmAttackVfxToTargetAndObservers(
        ProximityCombatAttack attack,
        IReadOnlyCollection<GameClientSession> sessions)
    {
        foreach (var observer in sessions)
        {
            // 탈락자도 받는다 (#219): 관전 중에도 봇 전투 연출이 계속 보여야 한다.
            if (!observer.PlayerId.HasValue ||
                observer.PlayerId.Value == attack.AttackerPlayerId ||
                observer.CurrentArea != attack.Area)
            {
                continue;
            }

            using var packet = Packet.Create((int)Protocol.G_TO_C_PROXIMITY_ATTACK_VFX);
            packet.SetBody(MessagePackSerializer.Serialize(new G_TO_C_PROXIMITY_ATTACK_VFX
            {
                AttackerPlayerId = attack.AttackerPlayerId,
                TargetPlayerId = attack.TargetPlayerId,
                AreaType = attack.Area,
                WeaponItemId = attack.WeaponItemId
            }));
            observer.TrySend(packet);
        }
    }

}
