using System.Collections.Immutable;
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
    private static float SwarmArenaBasicRange => Config.SWARM_ORB_ATTACK_RANGE;
    private const float SwarmArenaBasicAttackIntervalSeconds = 1f;
    private const int SwarmArenaWeaponItemId = 107000010;
    // 세 색(태양·파도·바람) 소환·시작·재건 풀 (#232).
    private static readonly int[] SwarmStartingOrbPool = BuildSwarmSupplyOrbPool();

    /// <summary>공급 차단 토글을 반영한 색 풀 — 소환·시작 지급·샌드박스 세트가 공유한다.</summary>
    private static int[] BuildSwarmSupplyOrbPool()
    {
        var pool = new List<int>();
        if (Config.SWARM_SUN_ORB_ENABLED) pool.Add(107000010);
        if (Config.SWARM_WIND_ORB_ENABLED) pool.Add(107000020);
        if (Config.SWARM_WAVE_ORB_ENABLED) pool.Add(107000030);
        return pool.ToArray();
    }

    // 플레이어 단위 지급: 매칭 단위 1회 지급은 지급 틱에 아직 접속 전인 사람을 영영 빈손으로 만든다 —
    // 늦게 합류해도 첫 등장 틱에 각자 1회 받는다 (상태는 Pacing.StartingOrbGrantedPlayers).

    // #272 자기장 재무장: 원형 수축 필드가 폐쇄 시간표의 단일 원천 — 구역 웨이브는 필드에서
    // 파생한다(GetSwarmFieldWaves). 토글은 클라 경계 렌더와 공유하므로 Config가 단일 출처.
    private static readonly bool SwarmFieldEnabled = Config.SWARM_PRESSURE_FIELD_ENABLED;

    // 고위험 절단 (#232 무한 꼬리): 몸으로 상대 꼬리를 유효하게 가로지르면 밟은 지점부터 꼬리 끝까지(접미 전체)
    // 깨지고 나는 정신오염 +35를 낸다. 크랙·방어 장갑·절단 낙수는 쓰지 않는다 (TryPerformSwarmTrailCut).
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
    private static float SwarmPvpCorruptionPerDamage =>
        SwarmConfigData.GetFloat("SWARM_PVP_CORRUPTION_PER_DAMAGE", 0.12f);

    /// <summary>PvP 피해 → 본체 오염 이월 누산. 반환 = 이번 타에 실제 적용할 오염(0 가능).</summary>
    private int ConsumeSwarmPvpCorruption(long matchingId, long victimId, int rawDamage)
    {
        var key = (matchingId, victimId);
        float total = (GetSwarmMatchRuntime(matchingId).Pacing.PvpCorruptionCarry.TryGetValue(key, out float carry) ? carry : 0f) +
                      rawDamage * SwarmPvpCorruptionPerDamage;
        int whole = (int)total;
        GetSwarmMatchRuntime(matchingId).Pacing.PvpCorruptionCarry[key] = total - whole;
        return whole;
    }

    // 치명타 (#229 임시): PvE 전용. 성장 축이 오브 수·티어뿐이라 같은 몹을 같은 속도로 지우는
    // 감각이 계속된다 — 가끔 크게 터지는 순간을 넣어 파밍에 리듬을 준다. 확률·배율은 임시값이고,
    // 정식 축(뒤치기·처형 사거리 등 조건부)이 생기면 이 굴림을 그 조건으로 대체한다.
    // 원천은 swarm_config.csv (#325) — 미등재 시 코드 기본값.
    private static double SwarmCriticalChance => SwarmConfigData.GetDouble("SWARM_CRITICAL_CHANCE", 0.15d);
    private static float SwarmCriticalMultiplier => SwarmConfigData.GetFloat("SWARM_CRITICAL_MULTIPLIER", 2f);

    /// <summary>PvE 치명타 굴림 — 적중이면 배율을 적용한 피해를 돌려준다.</summary>
    private int RollSwarmCriticalDamage(long matchingId, int damage, out bool critical)
    {
        critical = GetSwarmMatchRuntime(matchingId).Pacing.RollCritical(SwarmCriticalChance);
        return critical
            ? Math.Max(damage + 1, (int)MathF.Round(damage * SwarmCriticalMultiplier))
            : damage;
    }

    // SB 유닛 개별 체력·착탄 지연 대기열·계측 서명 등 매치 상태는 #294에서
    // 상태 홀더(GetSwarmMatchRuntime(matchingId).Pacing 등, Services/SwarmArenaStates.cs)로 이동했다.

    private void ProcessPendingSwarmMonsterHits(
        long matchingId, DateTime nowUtc, List<GameClientSession> sessions)
    {
        for (int index = GetSwarmMatchRuntime(matchingId).Pacing.PendingMonsterHits.Count - 1; index >= 0; index--)
        {
            var hit = GetSwarmMatchRuntime(matchingId).Pacing.PendingMonsterHits[index];
            if (hit.MatchingId != matchingId || nowUtc < hit.ApplyAtUtc)
                continue;

            GetSwarmMatchRuntime(matchingId).Pacing.PendingMonsterHits.RemoveAt(index);
            var damageResult = _swarmMonsterDirector.ApplyMonsterDamage(
                matchingId, hit.CombatTargetId, hit.AttackerId, hit.Damage);

            // 결과 집계 (#229): 스웜 전투는 전부 여기를 지난다. 여기서 안 세면
            // 결과 화면이 수백 킬을 "처치 0회"로 표시한다.
            if (damageResult.Applied)
            {
                _gameEventLogManager.RecordMonsterHit(
                    matchingId, hit.AttackerId, hit.Damage, damageResult.Killed);
            }

            // 기준점 잠금 (#232 1단계): 비행 중 몬스터가 이미 죽었어도 사건은 소멸하지 않는다 —
            // 발사 순간 잠근 위치에서 끝까지 처리한다. 피해는 없지만(이중 정산 없음) 2단계
            // 교차사격 모양은 여기서 그대로 터져야 "예고 뒤 몹이 죽어도 모양은 남는다"가 참이 된다.
            if (!damageResult.Applied && hit.AnchorPosition != null)
                ResolveSwarmAttackAtLockedAnchor(matchingId, hit, nowUtc);

            // 처치 정산은 교차사격 즉시 타격과 같은 경로 — 계측·처치 로그·소환석 드롭.
            if (damageResult.Applied && damageResult.Killed && damageResult.MonsterState != null)
                SettleSwarmMonsterKill(matchingId, damageResult, hit.AttackerId, hit.Damage, sessions);
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
        GetSwarmMatchRuntime(matchingId).Pacing.AnchorOrphanCount[matchingId] =
            (GetSwarmMatchRuntime(matchingId).Pacing.AnchorOrphanCount.TryGetValue(matchingId, out int count) ? count : 0) + 1;

        if (GetSwarmMatchRuntime(matchingId).Pacing.AnchorProbeAtUtc.TryGetValue(matchingId, out var probeAt) && nowUtc < probeAt)
            return;
        GetSwarmMatchRuntime(matchingId).Pacing.AnchorProbeAtUtc[matchingId] = nowUtc.AddSeconds(10);
        _gameEventLogManager.LogSystem(
            matchingId,
            $"anchor_probe orphanResolved={GetSwarmMatchRuntime(matchingId).Pacing.AnchorOrphanCount[matchingId]}");
    }

    // 티어별 오브 HP는 Common(OrbData.GetSquadOrbMaxHp)이 단일 출처 — 클라 체력바와 공유.
    private static int GetSquadOrbMaxHp(int tier) => OrbData.GetSquadOrbMaxHp(tier);

    /// <summary>매치 잠금 밖에서도 읽을 수 있는 터미널 게이트 — 색인에 없는 매치도 터미널로 본다.</summary>
    private bool IsMatchTerminal(long matchingId) =>
        MatchRuntimes.Get(matchingId) is not { IsTerminal: false };

    /// <summary>
    ///     #217 8인 맵 역할 검증(M1). 매치 수명(탈락·최후 1인·타이머)은 기존 서바이버 로얄
    ///     흐름이 소유하고, 여기서는 스웜 디렉터 틱·접촉 피해·전투 액터·PvP만 돌린다.
    ///     호출자(50ms 전투 틱·5초 정산 틱)가 매치 잠금을 쥔 채 부른다.
    /// </summary>
    private void ProcessSwarmArenaForMatching(
        long matchingId,
        List<GameClientSession> activeSessions)
    {
        // 정리된 매치의 상태를 되살리지 않는다 — 아래 GetSwarmMatchRuntime은 GetOrCreate다.
        if (IsMatchTerminal(matchingId))
            return;

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

        if (!_swarmMonsterDirector.HasMatching(matchingId))
        {
            long humanPlayerId = sessions.Count > 0 ? sessions[0].PlayerId!.Value : bots[0].PlayerId;
            if (!_swarmMonsterDirector.InitializeMatching(matchingId, humanPlayerId, DateTime.UtcNow))
                return;

            GameClientSession.SwarmDummyMoveCallback ??=
                (dummyMatchingId, dirX, dirY) =>
                    MoveSwarmCutDummy(dummyMatchingId, dirX, dirY);
            // #272 경계 토출 스폰: 자기장 경계가 관통 중인 구역의 캠프는 빨간 지대에서 태어난다.
            SwarmMonsterDirector.FieldSpawnCellResolver ??= ResolveSwarmFieldSpawn;
            LogSwarmPairZoneDistances(matchingId);
            // #272 자기장: 수축 시계는 폐쇄 시계와 같은 앵커(AreaClosureManager.GameStartTime)를 쓴다 —
            // 무장은 폐쇄 틱(PrepareSwarmScheduledClosureTick)의 최초 InitializeMatching이 담당한다.
            logger.LogInformation(
                "Swarm pressure field armed: MatchingId={MatchingId}, MaxDistance={MaxDistance}, " +
                "HoldSeconds={Hold}, ShrinkSeconds={Shrink}",
                matchingId, SwarmPressureField.MaxDistance, SwarmFieldHoldSeconds, SwarmFieldShrinkSeconds);
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
                !GetSwarmMatchRuntime(matchingId).Pacing.StartingOrbGrantedPlayers.Add((matchingId, session.PlayerId.Value)))
                continue;

            // 잼 지갑 리셋 (#222 M3) — 세션이 매치를 넘어 살아있으므로 시작 지급 시점에 초기화.
            session.ResetJam();
            session.FreeSummonCharges = 0;
            GrantSwarmStartingOrbs(matchingId, session.PlayerId.Value, session);
            _summonStoneManager.AddStones(matchingId, session.PlayerId.Value, startingStones);
            session.SendSummonStoneState();
        }

        foreach (var bot in bots)
        {
            if (!GetSwarmMatchRuntime(matchingId).Pacing.StartingOrbGrantedPlayers.Add((matchingId, bot.PlayerId)))
                continue;

            GrantSwarmStartingOrbs(matchingId, bot.PlayerId, session: null);
            _summonStoneManager.AddStones(matchingId, bot.PlayerId, startingStones);
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
            GetSwarmMatchRuntime(matchingId).Pacing.CutDummyAutoSetupDone.Add(matchingId))
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
        var tick = _swarmMonsterDirector.Tick(matchingId, directorParticipants, nowUtc);

        // 정지 감시: 8초 이상 제자리인 몹을 매치 로그로 남긴다 — 회귀 감지선.
        foreach (string report in tick.StuckReports)
            _gameEventLogManager.LogSystem(matchingId, report);

        // 공급 스폰 계측 (#229 4단계): 공급지·페이즈·마릿수·석 보상 + 스폰 직후 전역 생존 수.
        // alive는 상한 48 준수와 구역 목표 유지를 한 줄로 읽기 위한 값이다.
        if (tick.SupplyPackSpawns.Count > 0)
        {
            int aliveAfter = _swarmMonsterDirector.GetVisualStates(matchingId).Count(state => state.IsAlive);
            foreach (var supplySpawn in tick.SupplyPackSpawns)
                _gameEventLogManager.LogSystem(
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
            BroadcastMonsterMinimapSnapshot(
                matchingId, sessions, _swarmMonsterDirector.GetVisualStates(matchingId));
            return;
        }

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

            ProcessSwarmWindBlades(matchingId, nowUtc, participants, aliveSessions, aliveBots, sessions);
            if (IsMatchTerminal(matchingId) || sessions.Any(session => session.IsGameEnded))
                return;
        }

        // 화상 틱 (#268): 교차사격 충격이 남긴 도트 — 발생원이 위 블록과 무관하게 항상 정산한다.
        ProcessSwarmSunBurns(matchingId, nowUtc, aliveSessions, aliveBots, sessions);
        if (IsMatchTerminal(matchingId) || sessions.Any(session => session.IsGameEnded))
            return;

        // 접촉 계측: 접촉이 성립하는지 층별로 남긴다. 이 줄들이 "봇은 접촉 피해를
        // 안 받는다"는 오독을 두 번 걷어냈다 — 실제로는 로깅이 없었고, 그다음엔 배율이 깎고 있었다.
        if (tick.PlayerDamage.Count > 0 && GetSwarmMatchRuntime(matchingId).Pacing.ContactProbeAtUtc.TryGetValue(matchingId, out var probeAt)
                ? nowUtc >= probeAt
                : true)
        {
            GetSwarmMatchRuntime(matchingId).Pacing.ContactProbeAtUtc[matchingId] = nowUtc.AddSeconds(10);
            int toBots = tick.PlayerDamage.Count(entry => entry.TargetPlayerId < 0);
            int toHumans = tick.PlayerDamage.Count - toBots;
            _gameEventLogManager.LogSystem(
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
        ProcessSwarmBotRecovery(matchingId, aliveBots, nowUtc);
        ProcessSwarmSleepRecovery(aliveSessions, nowUtc);

        // 봇도 사람과 같은 규칙으로 성장한다: 소환석 5개 + 스팟 소진. 공짜 버튼 소환 없음.
        ProcessSwarmBotExplores(matchingId, aliveBots, sessions);
        ProcessSwarmBotDoorUnlocks(matchingId, aliveBots, sessions, nowUtc);

        if (TryConsumeMonsterPositionBroadcastSlot(matchingId, nowUtc))
            BroadcastMonsterMinimapSnapshot(matchingId, sessions, _swarmMonsterDirector.GetVisualStates(matchingId));

        var actors = BuildSwarmArenaCombatActors(matchingId, aliveSessions, aliveBots, nowUtc);
        ProcessOrbRecovery(matchingId, actors, aliveSessions, aliveBots, nowUtc);
        DispatchOrbVisualStatePublications(
            PrepareOrbVisualStatePublications(matchingId, actors, sessions));
        BroadcastSwarmOrbRankings(matchingId, sessions, bots);
        // 성장 카드 (#226 단계 C): 소환석이 비용에 닿는 즉시 3택 오퍼 — 상자 트리거 퇴역.
        ProcessSwarmGrowthOffers(matchingId, nowUtc, aliveSessions, aliveBots);
        if (ProcessSwarmScoreTimeout(matchingId, nowUtc, sessions, aliveSessions, aliveBots))
            return;
        // 지난 틱에 예약된 착탄들을 먼저 정산한다 — 체력바가 폭발 시점에 맞춰 닳는다.
        ProcessPendingSwarmMonsterHits(matchingId, nowUtc, sessions);
        // 교차사격 판정 (#232 2단계): 예고가 끝난 모양을 이번 틱 위치로 판정한다.
        ProcessSwarmCrossfires(matchingId, nowUtc, participants, aliveSessions, aliveBots, sessions);
        if (IsMatchTerminal(matchingId) || sessions.Any(session => session.IsGameEnded))
            return;

        // 비행 중인 PvP 탄은 여기서 착탄 처리한다 — 매 틱 지우면 안 된다. "리졸버는 PvE 전용"이라는 전제의 청소가
        // 리졸버가 사람 표적도 내보내게 바뀐 뒤 방금 발사한 탄을 다음 틱에 통째로 삭제해 PvP가 한 발도 착탄하지 못했다.
        for (int index = GetSwarmMatchRuntime(matchingId).Pacing.PendingPvpHits.Count - 1; index >= 0; index--)
        {
            var pending = GetSwarmMatchRuntime(matchingId).Pacing.PendingPvpHits[index];
            if (pending.MatchingId != matchingId || nowUtc < pending.DueAtUtc)
                continue;
            GetSwarmMatchRuntime(matchingId).Pacing.PendingPvpHits.RemoveAt(index);
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
        var crossfireCappedOwners = CollectSwarmCrossfireCappedOwners(matchingId, nowUtc);
        // 표적 분산 (#232): 내 살아 있는 모양이 이미 겨눈 몹은 내 다른 태양 오브의 후보에서 뺀다 —
        // 오브마다 제 자리에서 "아직 아무도 안 겨눈" 가장 가까운 몹을 고른다.
        var crossfireAnchoredTargets = CollectSwarmCrossfireAnchoredTargets(matchingId);

        var attacks = _proximityAutoCombatResolver.Resolve(
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
                if (IsSwarmCrossfireWeapon(attacker.WeaponItemId))
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
                if (IsSwarmCrossfireWeapon(attacker.WeaponItemId))
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
            int monsterId = _swarmMonsterDirector.GetMonsterIdForCombatTarget(matchingId, attack.TargetPlayerId);
            // 유령 발사 가드 (#226 진단): 같은 틱에 죽은 몬스터의 CombatTargetId(-4e18대)가 몬스터 분기를
            // 통과해 PvP 분기로 새던 문제 — 음수 대역 차단. 태양 분기보다 먼저 건다.
            if (monsterId <= 0 && attack.TargetPlayerId < -1_000_000_000_000L)
                continue;
            // 태양은 표적이 몬스터든 플레이어든 교차사격 투사체다 (유저 지시: 오브가 사람도 조준) —
            // 첫 표적에서 폭발. 바람은 조준하지 않는다(회전 칼날), 파도는 물폭탄.
            if (IsSwarmCrossfireWeapon(attack.WeaponItemId))
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
                    !TryScheduleSwarmCrossfire(
                        matchingId, attack, sunOrigin, sunAnchor, monsterId, attack.Damage, nowUtc, sessions))
                {
                    // 로그는 남기지 않는다 — 상한이 찬 동안 매 틱 되풀이되는 정상 대기라 이벤트 흐름만 메운다.
                    _proximityAutoCombatResolver.RefundAttack(
                        matchingId, attack.AttackerPlayerId, attack.AttackerItemUid, nowUtc);
                }
                continue;
            }

            if (monsterId > 0)
            {
                int monsterDamage = RollSwarmCriticalDamage(
                    matchingId, attack.Damage, out bool critical);
                // 발사 연출은 즉시, 피해는 투사체 비행시간 뒤에 — 체력바와 폭발이 일치한다.
                var attackerSession = sessions.FirstOrDefault(
                    session => session.PlayerId == attack.AttackerPlayerId);
                // 자동 공격은 교전 잠금을 찍지 않는다 (#229 6단계 수정): 오브는 사거리 안 잔상을
                // 쉬지 않고 쏘므로, 이걸 "가해"로 세면 잔상이 한 마리라도 살아 있는 한 영영 눕지
                // 못한다 — "수면은 잔상이 없는 상태를 요구하지 않는다"는 규칙과 정면으로 충돌하고,
                // 전멸 뒤 4초 휴지 창도 3초를 잠금에 뺏겨 무의미해진다.
                // 잠금은 내가 몸으로 지르는 절단과 피격에만 건다.
                attackerSession?.SendSwarmAfterimageMonsterAttackFeedback(
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
                _swarmMonsterDirector.ReserveMonsterDamage(
                    matchingId, attack.TargetPlayerId, monsterDamage);
                // 기준점 계측 + 잠금 (#232 1단계): 발사 순간 몹의 공격 사건 수를 올리고,
                // 원점·기준 위치·무기를 박제해 착탄 정산까지 들고 간다.
                _swarmMonsterDirector.RecordMonsterAttackEvent(matchingId, attack.TargetPlayerId);
                GetSwarmMatchRuntime(matchingId).Pacing.PendingMonsterHits.Add(new PendingSwarmMonsterHit(
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
                GetSwarmMatchRuntime(matchingId).Pacing.PendingPvpHits.Add((matchingId, attack, nowUtc.AddSeconds(pvpDelaySeconds)));
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
            if (!_botPlayerManager.TryFinalizeProximityAutoCombatElimination(bot, matchingId))
                continue;

            ProcessBotElimination(matchingId, bot.PlayerId, EliminationReason.MENTAL_ZERO, activeSessions,
                attackerPlayerId: bot.LastProximityAttackerPlayerId);
            if (IsMatchTerminal(matchingId) ||
                activeSessions.Any(session => session.IsGameEnded))
                return;
        }
    }


    // 스팟 예산 선소진(#217 성장곡선 v3, 21개)은 퇴역 — SB에는 인위적 봉인이 없고,
    // 희소성은 리젠(60초)과 크기 비례 비용이 담당한다. 배치된 스팟은 전부 살아 있다.

    // 자기장 스케줄 (#272 재무장, 원형): 유예 후 안전 반경이 최대치에서 0까지 선형 수축한다 —
    // 매치 종료(SWARM_MATCH_DURATION_SECONDS)에 운동장 중심만 안전, 최종 폐쇄 = 타이머 만료 = 오버타임 개시.
    // 깔때기 순서(외곽 방 → 복도 밴드 → 운동장)는 중심 거리가 먼 순서로 자연 재현된다.
    private static double SwarmFieldHoldSeconds => Config.SWARM_FIELD_HOLD_SECONDS;
    private static double SwarmFieldShrinkSeconds =>
        Config.SWARM_MATCH_DURATION_SECONDS - Config.SWARM_FIELD_HOLD_SECONDS;

    // 경계 밖 오염 (리소스 틱 5초당): 기본 + 초과 셀당 가산. 문턱에서 즉사가 아니라 "슬슬 따가움에서 깊을수록
    // 아픔"의 경사 — 외곽 마지막 개봉 도박이 성립해야 한다. 구 웨이브 폐쇄 오염 대신 이 경사가 압박을 전담한다
    // (#272). 기본 12: 유예가 없어 상시 노출 시간이 길므로 경계 스침은 오래 살고 깊이 20셀 방치는 20초 안에 죽는다.
    // 원천은 swarm_config.csv (#335) — 미등재 시 코드 기본값.
    private static int SwarmFieldBaseCorruptionPerTick =>
        SwarmConfigData.GetInt("SWARM_FIELD_BASE_CORRUPTION_PER_TICK", 12);
    private static int SwarmFieldCorruptionPerExtraCell =>
        SwarmConfigData.GetInt("SWARM_FIELD_CORRUPTION_PER_EXTRA_CELL", 5);

    /// <summary>현재 안전 반경. 수축 전에는 double.MaxValue(전 맵 안전). 폐쇄 시계(GameStartTime)와
    ///     같은 앵커를 쓴다 — 파생 웨이브의 구역 완전-밖 시각과 필드 오염이 어긋나지 않는다.
    ///     양자화 없는 연속식(유저 결정: 주기 단위가 아니라 계속 줄어드는 원) —
    ///     클라 경계 렌더(ClosureFieldOverlay.ComputeSafeDistance)와 같은 식이다.</summary>
    private double GetSwarmSafeDistance(long matchingId, DateTime nowUtc)
    {
        if (!SwarmFieldEnabled)
            return double.MaxValue;
        var closureState = _areaClosureManager.GetMatchingState(matchingId);
        if (closureState == null)
            return double.MaxValue;

        double shrinkElapsed = (nowUtc - closureState.GameStartTime).TotalSeconds - SwarmFieldHoldSeconds;
        if (shrinkElapsed <= 0) return double.MaxValue;

        double progress = Math.Min(1d, shrinkElapsed / SwarmFieldShrinkSeconds);
        return SwarmPressureField.GetSafeDistanceAtProgress(progress);
    }

    /// <summary>구역 전체가 현재 경계 밖(폐쇄·자기장)인가 — 봇 대피·스팟 필터의 기준.</summary>
    private bool IsSwarmAreaOutside(long matchingId, AreaType area) =>
        _areaClosureManager.IsAreaClosed(matchingId, area) ||
        SwarmPressureField.GetAreaMinDistance(area) > GetSwarmSafeDistance(matchingId, DateTime.UtcNow);

    /// <summary>자기장 오염 (리소스 틱당). 경계 안이면 0, 밖이면 기본 + 초과 거리 비례.</summary>
    private int GetSwarmFieldCorruptionPerTick(long matchingId, Vector3f worldPosition)
    {
        if (worldPosition == null)
            return 0;

        double safeDistance = GetSwarmSafeDistance(matchingId, DateTime.UtcNow);
        if (safeDistance >= double.MaxValue)
            return 0;

        var cell = ProximityCombatLineOfSight.WorldPositionToCell(Config.SWARM_MATCH_MAP, worldPosition);
        double over = SwarmPressureField.GetDistance(cell) - safeDistance;
        if (over <= 0) return 0;
        return SwarmFieldBaseCorruptionPerTick + (int)(over * SwarmFieldCorruptionPerExtraCell);
    }

    // #272 경계 토출 스폰: 구역별 walkable 셀을 중심 거리 오름차순으로 캐시 — 리졸버가 띠를 자른다.
    // Config 초기화 뒤 첫 접근까지 계산을 미루되, 서로 다른 매치의 동시 최초 접근은 한 번만 게시한다.
    private static readonly Lazy<IReadOnlyDictionary<AreaType, IReadOnlyList<(Cell Cell, int Distance)>>>
        _swarmAreaCellsByDistance = new(
            BuildSwarmAreaCellsByDistance,
            LazyThreadSafetyMode.ExecutionAndPublication);

    private static IReadOnlyDictionary<AreaType, IReadOnlyList<(Cell Cell, int Distance)>>
        BuildSwarmAreaCellsByDistance()
    {
        var byArea = new Dictionary<AreaType, List<(Cell Cell, int Distance)>>();
        foreach (var pair in SwarmPressureField.DistancesByCell)
        {
            var cell = new Cell(pair.Key.X, pair.Key.Y);
            var cellArea = GameMapData.GetCurrentArea(Config.SWARM_MATCH_MAP, cell);
            if (cellArea == AreaType.None) continue;
            if (!byArea.TryGetValue(cellArea, out var list))
                byArea[cellArea] = list = new List<(Cell, int)>();
            list.Add((cell, pair.Value));
        }

        foreach (var list in byArea.Values)
            list.Sort((left, right) => left.Distance.CompareTo(right.Distance));

        return byArea.ToDictionary(
            pair => pair.Key,
            pair => (IReadOnlyList<(Cell Cell, int Distance)>)pair.Value.AsReadOnly());
    }

    private static IReadOnlyList<(Cell Cell, int Distance)> GetSwarmAreaCellsByDistance(AreaType area)
    {
        return _swarmAreaCellsByDistance.Value.TryGetValue(area, out var cells)
            ? cells
            : [];
    }

    // 경계 토출 띠 폭 (셀): 스폰은 경계 바로 밖, 복귀 앵커는 경계 바로 안 — 태어나서 걸어 들어온다.
    private const int SwarmFieldSpawnBandCells = 6;

    /// <summary>
    ///     #272 경계 토출 스폰 ("안전 구역 예외 제거" 결정 포함): 캠프
    ///     신규 스폰은 항상 바깥(자기장이 올 방향)에서 태어나 안쪽 앵커로 걸어 들어온다 —
    ///     경계가 구역을 관통 중이면 경계 밖 빨간 띠, 아직 온전히 안전한 구역이면 그 구역의
    ///     가장 바깥 띠. 저작 캠프 앵커는 자기장 모드에서 쓰지 않는다 (단일 문법).
    ///     온전히 밖인 구역은 폐쇄 스폰 정지 규칙이 이미 막는다.
    /// </summary>
    private (Cell Spawn, Cell Anchor)? ResolveSwarmFieldSpawn(long matchingId, AreaType area)
    {
        if (!SwarmFieldEnabled) return null;
        double safeDistance = GetSwarmSafeDistance(matchingId, DateTime.UtcNow);

        var cells = GetSwarmAreaCellsByDistance(area);
        if (cells.Count == 0) return null;

        bool boundaryCrossing = safeDistance < cells[^1].Distance;
        double spawnMin = boundaryCrossing ? safeDistance : cells[^1].Distance - SwarmFieldSpawnBandCells;
        double spawnMax = boundaryCrossing ? safeDistance + SwarmFieldSpawnBandCells : cells[^1].Distance;

        var spawnBand = cells
            .Where(entry => entry.Distance > spawnMin && entry.Distance <= spawnMax)
            .ToList();
        if (spawnBand.Count == 0)
            spawnBand = boundaryCrossing
                ? cells.Where(entry => entry.Distance > safeDistance).ToList()
                : [cells[^1]];

        // 앵커(도착지)는 구역에서 자기장 중심에 가장 가까운 띠 (#269-A):
        // 스폰 띠 바로 안쪽으로 잡으면 복도처럼 좁은 구역에서 스폰과 도착이 사실상 같은 자리라
        // "즉시 젠 후 제자리"로 읽힌다 — 구역을 최대로 가로질러 걸어 들어오게 한다.
        var anchorBand = cells
            .Where(entry => entry.Distance < cells[0].Distance + SwarmFieldSpawnBandCells)
            .ToList();
        if (anchorBand.Count == 0)
            anchorBand = [cells[0]];

        return (
            spawnBand[Random.Shared.Next(spawnBand.Count)].Cell,
            anchorBand[Random.Shared.Next(anchorBand.Count)].Cell);
    }

    // 자기장 파생 웨이브 (#272): 계산은 AreaClosureManager.BuildSwarmFieldWaves가 담당한다.
    // 거리 필드·상수가 프로세스 수명 동안 불변이라 한 번만 계산해 캐시한다.
    private static readonly Lazy<IReadOnlyList<ClosureWaveDefinition>> _swarmFieldDerivedWaves =
        new(
            () => AreaClosureManager
                .BuildSwarmFieldWaves(SwarmFieldHoldSeconds, SwarmFieldShrinkSeconds)
                .AsReadOnly(),
            LazyThreadSafetyMode.ExecutionAndPublication);

    private static IReadOnlyList<ClosureWaveDefinition> GetSwarmFieldWaves() =>
        _swarmFieldDerivedWaves.Value;

    // 자기장 상태 패킷은 매칭당 개전 1회 브로드캐스트 (재접속은 스냅샷이 복원). Timer 콜백은
    // 겹칠 수 있으므로 authoritative commit은 match monitor, outbound 순서는 field FIFO가 맡는다.

    /// <summary>
    ///     #272 자기장 폐쇄: 구역 웨이브는 자기장에서 파생한 시간표로 닫는다 (SwarmFieldEnabled=false면
    ///     폐쇄 없음 — 레거시 DefaultP0Waves 폴백은 #310에서 제거). 경고 15초 → 폐쇄 브로드캐스트. 폐쇄 구역 오염은 자기장
    ///     경사(정산 틱의 GetSwarmFieldCorruptionPerTick)가 전담하고, 신규 몹 스폰 정지는 캠프
    ///     리졸버, 봇·스팟 제외는 IsSwarmAreaOutside가 담당한다.
    /// </summary>
    /// <summary>
    ///     Commits closure, door, inventory, and event-log state under the match monitor, then
    ///     freezes its ordered best-effort packet projection. Transport failure never rolls back
    ///     these authoritative changes.
    /// </summary>
    private SwarmClosurePublicationPlan? PrepareSwarmScheduledClosureTick(
        long matchingId,
        IReadOnlyList<GameClientSession> sessions)
    {
        var outbound = ImmutableArray.CreateBuilder<SwarmClosureOutbound>();
        ImmutableArray<int> allRecipients = CaptureSwarmClosureRecipientOrdinals(
            sessions,
            static _ => true);
        var closureState = _areaClosureManager.InitializeMatching(
            matchingId,
            wavesOverride: SwarmFieldEnabled ? GetSwarmFieldWaves() : null);
        if (SwarmFieldEnabled && GetSwarmMatchRuntime(matchingId).Pacing.FieldStateAnnounced.Add(matchingId))
        {
            outbound.Add(new SwarmFieldStateOutbound(
                new DateTimeOffset(closureState.GameStartTime).ToUnixTimeMilliseconds(),
                allRecipients));
        }

        var closureTick = _areaClosureManager.CheckClosureSchedule(matchingId);
        foreach (var area in closureTick.WarningAreas)
        {
            outbound.Add(new SwarmClosureWarningOutbound(
                area,
                closureTick.WarningSeconds,
                closureTick.ClosureAtUnixMs,
                allRecipients));
        }

        foreach (var area in closureTick.ClosedAreas)
        {
            _gameEventLogManager.LogClosure(matchingId, area.ToString());
            outbound.Add(new SwarmAreaClosedOutbound(area, allRecipients));
        }

        if (closureTick.ClosedAreas.Count > 0)
        {
            // 폐쇄 = 문 잠금 + 틱 오염 (즉사 없음, #227): 닫히는 순간 안에 있어도 죽지
            // 않는다. 정산 틱(GetClosedAreaCorruptionPerTick)이 5초마다 오염을 얹고, 안에 있는 사람은 자기 구역
            // 문을 게이지로 따고 나갈 수 있다(밖에서 들어오는 문 따기는 여전히 거절). 자기 구역 문이 잠기는 것은
            // 그대로다 — "지금 나가야 하는가"의 판단은 경고 15초와 잠긴 문이 만든다.
            // 폐쇄·경고도 수면을 깨우지 않는다 — 수면 중단은 이동뿐이다.
            IReadOnlyList<int> lockedDoorIds =
                _doorStateManager.CloseDoorsForAreas(matchingId, closureTick.ClosedAreas);
            foreach (int doorId in lockedDoorIds.Distinct())
                outbound.Add(new SwarmDoorStateOutbound(doorId, allRecipients));

            // 꼬리 파괴: 본인은 밖에 있고 꼬리만 남은 경우가 무보상 파괴 대상이다. 안에 있는 사람의 꼬리는
            // 본인과 함께 남는다 — 틱 오염이 그 사람의 비용이다.
            PrepareDestroySwarmOrbsInClosedAreas(
                matchingId,
                closureTick.ClosedAreas,
                sessions,
                outbound);
        }

        return outbound.Count == 0
            ? null
            : new SwarmClosurePublicationPlan(matchingId, outbound.ToImmutable());
    }

    private static ImmutableArray<int> CaptureSwarmClosureRecipientOrdinals(
        IReadOnlyList<GameClientSession> sessions,
        Func<GameClientSession, bool> predicate)
    {
        var recipients = ImmutableArray.CreateBuilder<int>();
        for (int ordinal = 0; ordinal < sessions.Count; ordinal++)
        {
            if (predicate(sessions[ordinal]))
                recipients.Add(ordinal);
        }

        return recipients.ToImmutable();
    }

    /// <summary>
    ///     Dispatches one frozen closure plan in legacy packet order. The first transport exception
    ///     aborts the remaining projection; the surrounding FIFO/lease finally paths still advance.
    /// </summary>
    private void DispatchSwarmClosurePublicationPlan(
        SwarmClosurePublicationPlan plan,
        IReadOnlyList<GameClientSession> sessions)
    {
        foreach (SwarmClosureOutbound outbound in plan.Outbound)
        {
            switch (outbound)
            {
                case SwarmFieldStateOutbound fieldState:
                    {
                        using var packet = Packet.Create((int)Protocol.G_TO_C_SWARM_FIELD_STATE);
                        packet.SetBody(MessagePackSerializer.Serialize(new G_TO_C_SWARM_FIELD_STATE
                        {
                            StartedAtUnixMs = fieldState.StartedAtUnixMs
                        }));
                        SendToCapturedRecipients(packet, fieldState.RecipientOrdinals, sessions);
                        break;
                    }
                case SwarmClosureWarningOutbound warning:
                    {
                        using var packet = Packet.Create((int)Protocol.G_TO_C_AREA_CLOSURE_WARNING);
                        packet.SetBody(MessagePackSerializer.Serialize(new G_TO_C_AREA_CLOSURE_WARNING
                        {
                            AreaType = warning.Area,
                            SecondsRemaining = warning.SecondsRemaining,
                            ClosureAtUnixMs = warning.ClosureAtUnixMs
                        }));
                        SendToCapturedRecipients(packet, warning.RecipientOrdinals, sessions);
                        break;
                    }
                case SwarmAreaClosedOutbound closed:
                    {
                        using var packet = Packet.Create((int)Protocol.G_TO_C_AREA_CLOSED);
                        packet.SetBody(MessagePackSerializer.Serialize(new G_TO_C_AREA_CLOSED
                        {
                            AreaType = closed.Area,
                            IsClosed = true
                        }));
                        SendToCapturedRecipients(packet, closed.RecipientOrdinals, sessions);
                        break;
                    }
                case SwarmDoorStateOutbound door:
                    {
                        using var packet = PacketMaker.G_TO_C_DOOR_STATE_UPDATE(
                            door.DoorId,
                            false,
                            ErrorCode.SUCCESS,
                            0);
                        SendToCapturedRecipients(packet, door.RecipientOrdinals, sessions);
                        break;
                    }
                case SwarmInventoryUpdateOutbound inventory:
                    {
                        foreach (int ordinal in inventory.RecipientOrdinals)
                        {
                            if (TryGetCapturedValue(sessions, ordinal, out GameClientSession session))
                                session.SendInGameInventoryUpdate(inventory.Item.ToModel());
                        }

                        break;
                    }
                case SwarmRingVfxOutbound ring:
                    {
                        using var packet = Packet.Create((int)Protocol.G_TO_C_SWARM_ENCIRCLE_VFX);
                        packet.SetBody(MessagePackSerializer.Serialize(new G_TO_C_SWARM_ENCIRCLE_VFX
                        {
                            OwnerPlayerId = ring.OwnerPlayerId,
                            CenterX = ring.CenterX,
                            CenterY = ring.CenterY,
                            Radius = ring.Radius,
                            Kind = ring.Kind,
                            VictimPlayerId = ring.VictimPlayerId,
                            FromOrdinal = ring.FromOrdinal
                        }));
                        SendToCapturedRecipients(packet, ring.RecipientOrdinals, sessions);
                        break;
                    }
                default:
                    throw new InvalidOperationException(
                        $"Unknown closure outbound type {outbound.GetType().Name} for matching {plan.MatchingId}.");
            }
        }
    }

    /// <summary>
    ///     폐쇄 잔류 오브 파괴 (#226 E): 폐쇄 완료 순간, 폐쇄 구역에 남아 있는 꼬리 접미를
    ///     끝에서부터 무보상 파괴한다 — 소환석 낙수 없음. 긴 꼬리는 점수·화력이 높지만
    ///     폐쇄 전에 더 일찍 철수해야 한다는 관리 비용이 여기서 성립한다.
    /// </summary>
    private void PrepareDestroySwarmOrbsInClosedAreas(
        long matchingId,
        IReadOnlyCollection<AreaType> closedAreas,
        IReadOnlyList<GameClientSession> sessions,
        ImmutableArray<SwarmClosureOutbound>.Builder outbound)
    {
        var closed = closedAreas.ToHashSet();
        var owners = new List<(long PlayerId, Vector3f Position, int SessionOrdinal)>();
        for (int ordinal = 0; ordinal < sessions.Count; ordinal++)
        {
            GameClientSession session = sessions[ordinal];
            if (session.PlayerId.HasValue && !session.IsEliminated &&
                session.LastValidatedPosition != null)
                owners.Add((session.PlayerId.Value, session.LastValidatedPosition, ordinal));
        }

        foreach (var bot in _botPlayerManager.GetBots(matchingId))
        {
            if (!bot.IsEliminated && !bot.IsSwarmCutDummy)
                owners.Add((bot.PlayerId, bot.Position, -1));
        }

        foreach (var (playerId, ownerPosition, ownerSessionOrdinal) in owners)
        {
            // 본인이 폐쇄 구역 안이면 꼬리는 그대로 둔다: 즉사가 퇴역해 본인은 틱 오염을 받으며
            // 문을 따고 나가는 중이다 — 여기서 꼬리까지 지우면 나가도 빈손이라 살아남을 이유가 없다.
            var ownerCell = ProximityCombatLineOfSight.WorldPositionToCell(Config.SWARM_MATCH_MAP, ownerPosition);
            if (closed.Contains(GameMapData.GetCurrentArea(Config.SWARM_MATCH_MAP, ownerCell)))
                continue;

            int orbCount = CountSwarmSquadOrbs(matchingId, playerId);
            if (orbCount == 0)
                continue;
            var closureTiers = GetSwarmOrbTiersInOrder(matchingId, playerId);

            // 꼬리는 경로를 따르므로 폐쇄 구역 잔류분은 항상 접미다 — 끝에서부터 스캔한다.
            int suffixStart = orbCount;
            Vector3f? suffixPosition = null;
            for (int ordinal = orbCount - 1; ordinal >= 0; ordinal--)
            {
                var position = GetSwarmOrbTrailPosition(
                    matchingId, playerId, ordinal, ownerPosition, closureTiers);
                var cell = ProximityCombatLineOfSight.WorldPositionToCell(Config.SWARM_MATCH_MAP, position);
                if (!closed.Contains(GameMapData.GetCurrentArea(Config.SWARM_MATCH_MAP, cell)))
                    break;
                suffixStart = ordinal;
                suffixPosition = position;
            }

            if (suffixStart >= orbCount || suffixPosition == null)
                continue;

            var destroyed = DestroySwarmOrbsFromOrdinal(matchingId, playerId, suffixStart);
            foreach (var destroyedItem in destroyed)
            {
                GetSwarmMatchRuntime(matchingId).TrailCombat.OrbDurabilityBonus.Remove((matchingId, playerId, destroyedItem.ItemUid));
                if (ownerSessionOrdinal >= 0)
                {
                    outbound.Add(new SwarmInventoryUpdateOutbound(
                        SwarmInGameItemSnapshot.Capture(destroyedItem),
                        [ownerSessionOrdinal]));
                }
            }

            // 파열 연출은 절단 링 재사용 — 전리품은 흩뿌리지 않는다 (폐쇄 파괴 무보상).
            var closedArea = GameMapData.GetCurrentArea(
                Config.SWARM_MATCH_MAP,
                ProximityCombatLineOfSight.WorldPositionToCell(Config.SWARM_MATCH_MAP, suffixPosition));
            ImmutableArray<int> ringRecipients = CaptureSwarmClosureRecipientOrdinals(
                sessions,
                session => session.PlayerId.HasValue && session.CurrentArea == closedArea);
            outbound.Add(new SwarmRingVfxOutbound(
                playerId,
                suffixPosition.X,
                suffixPosition.Y,
                SwarmTrailCutFlashRadius,
                SwarmRingVfxKindCut,
                playerId,
                suffixStart,
                ringRecipients));
            _gameEventLogManager.LogSystem(
                matchingId,
                $"closure_orb_destroyed player={playerId} from={suffixStart} count={destroyed.Count}");
            logger.LogInformation(
                "Swarm closure orb destruction: MatchingId={MatchingId}, PlayerId={PlayerId}, FromOrdinal={FromOrdinal}, Count={Count}",
                matchingId, playerId, suffixStart, destroyed.Count);
        }
    }

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

    /// <summary>
    ///     수면 회복 틱 (#229 6단계). 실제 정산은 세션이 소유한다 — 오염도·최대치가 세션 내부값이라
    ///     밖에서 만지면 접근자를 열어야 하고, 그러면 다른 경로도 오염도를 직접 건드릴 수 있게 된다.
    /// </summary>
    private static void ProcessSwarmSleepRecovery(
        List<GameClientSession> aliveSessions, DateTime nowUtc)
    {
        foreach (var session in aliveSessions)
            session.TickSwarmSleepRecovery(nowUtc);
    }


    /// <summary>상자 시간 등급 (#222 M3): 개전 앵커(게이트, 봇 전용은 스웜 첫 틱) 경과로 티어 결정.</summary>
    private int GetSwarmDraftTier(long matchingId)
    {
        var startedAtUtc = MatchStartGate.GetGameplayStartedAtUtc(matchingId);
        if (startedAtUtc == null &&
            GetSwarmMatchRuntime(matchingId).Pacing.MatchFallbackAnchorUtc.TryGetValue(matchingId, out var fallbackAnchor))
            startedAtUtc = fallbackAnchor;
        if (startedAtUtc == null)
            return 1;

        return OrbData.GetDraftTierByElapsed((DateTime.UtcNow - startedAtUtc.Value).TotalSeconds);
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
                onCooldown.Contains(info.Id) ||
                // 경계 밖 구역 스팟은 후보에서 제외 — 최근접이 밖이라고 순례 전체가 멈추면 안 된다.
                IsSwarmAreaOutside(matchingId, (AreaType)info.ZoneId))
                continue;

            var world = BotPlayerManager.CellToWorldPosition(
                Config.SWARM_MATCH_MAP, new Cell(info.CellX, info.CellY));
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
            power += OrbData.GetSwarmStatTierWeight(tier) * item.Count;
        }

        return power;
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
    // 고위험 단일 절단 (#232): 성공한 공격자는 정신오염 +35를 내고 8초 동안 수면 회복을 잃는다.
    // 비용을 감당할 수 없으면(만충으로 탈락) 절단도 비용도 발생하지 않는다.
    private static int SwarmSingleCutCorruptionCost =>
        SwarmConfigData.GetInt("SWARM_SINGLE_CUT_CORRUPTION_COST", 35);
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
    private const float SwarmTrailCutFlashRadius = 0.7f;

    // 오브 트레일·절단 래치·반격 창 상태는 GetSwarmMatchRuntime(matchingId).TrailCombat (#294 상태 홀더).

    /// <summary>이 절단자가 이 피해자에게 손댈 수 없는 상태인가 — 절단·본체 피해 공통 관문.</summary>
    private bool IsSwarmRetaliationGuarded(long matchingId, long cutterId, long victimId, DateTime nowUtc)
    {
        return GetSwarmMatchRuntime(matchingId).TrailCombat.CutRetaliationWindows.TryGetValue((matchingId, cutterId, victimId), out var window) &&
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
        if (GetSwarmMatchRuntime(matchingId).TrailCombat.CutRetaliationWindows.TryGetValue((matchingId, victimId, cutterId), out var opposite) &&
            nowUtc < opposite.ExpiresAtUtc)
            opposite.Retaliated = true;

        var key = (matchingId, cutterId, victimId);
        if (!GetSwarmMatchRuntime(matchingId).TrailCombat.CutRetaliationWindows.TryGetValue(key, out var window))
        {
            window = new SwarmRetaliationWindow { OpenedAtUtc = nowUtc, OpenedArea = area };
            GetSwarmMatchRuntime(matchingId).TrailCombat.CutRetaliationWindows[key] = window;
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
        var expired = GetSwarmMatchRuntime(matchingId).TrailCombat.CutRetaliationWindows
            .Where(pair => pair.Key.MatchingId == matchingId && nowUtc >= pair.Value.ExpiresAtUtc)
            .ToList();
        foreach (var (key, window) in expired)
        {
            GetSwarmMatchRuntime(matchingId).TrailCombat.CutRetaliationWindows.Remove(key);

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
    private void UpdateSwarmOrbTrails(long matchingId, List<SwarmParticipantSpatial> participants)
    {
        foreach (var participant in participants)
        {
            var key = (matchingId, participant.PlayerId);
            if (!GetSwarmMatchRuntime(matchingId).TrailCombat.OrbTrails.TryGetValue(key, out var points))
            {
                points = new List<Vector3f>();
                GetSwarmMatchRuntime(matchingId).TrailCombat.OrbTrails[key] = points;
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
        IReadOnlyList<int>? orderedTiers = null)
    {
        // 호출부가 목록을 들고 있으면 그걸 쓴다 — 순번마다 인벤토리를 다시 훑지 않게.
        float targetDistance = OrbData.GetSwarmTrailDistance(
            orderedTiers ?? GetSwarmOrbTiersInOrder(matchingId, playerId), ordinal);
        return GetSwarmTrailPositionAtDistance(matchingId, playerId, targetDistance, anchor);
    }

    /// <summary>경로를 지정 거리만큼 거슬러 올라간 지점 — 오브 열 좌표와 소용돌이 스폰이 공용.</summary>
    private Vector3f GetSwarmTrailPositionAtDistance(
        long matchingId, long playerId, float targetDistance, Vector3f anchor)
    {
        if (!GetSwarmMatchRuntime(matchingId).TrailCombat.OrbTrails.TryGetValue((matchingId, playerId), out var points) || points.Count == 0)
            return new Vector3f(anchor.X, anchor.Y - targetDistance * 0.2f, 0f);

        Vector3f previous = anchor;
        float accumulated = 0f;
        // 인덱스로 훑는다 (#229): 폐쇄 틱은 아레나 틱과 다른 스레드에서 돈다 — foreach로 열거하는
        // 사이 아레나가 이 궤적에 점을 추가하면 "Collection was modified"로 폐쇄 정산이 통째로
        // 죽는다 — 순차 폐쇄로 폐쇄 횟수가 늘면서 실제로 터졌다.
        // 길이 변화는 이번 프레임 계산에서만 무시하면 되고, 다음 틱이 새 값을 읽는다.
        for (int index = 0; index < points.Count; index++)
        {
            var point = points[index];
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
    ///     열 절단: 본체 이동 선분이 상대 오브 링크(오브i-오브i+1)를 가로지르면 밟힌 순번부터 꼬리 끝까지 파괴한다.
    ///     이동이 곧 공격 동사이고, 비용은 "상대 성장물이 끊김"과 절단자의 정신오염 +35다. 본체-첫 오브 링크는
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
            bool hasPrevious = GetSwarmMatchRuntime(matchingId).TrailCombat.TrailLastTickPositions.TryGetValue(positionKey, out var previous);
            GetSwarmMatchRuntime(matchingId).TrailCombat.TrailLastTickPositions[positionKey] =
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
                if (GetSwarmMatchRuntime(matchingId).TrailCombat.OrbCutLatches.TryGetValue(
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
                    if (GetSwarmMatchRuntime(matchingId).TrailCombat.CutRetaliationWindows.TryGetValue(
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
        int cutterCorruptionBefore = cutterSession?.CurrentCorruption ?? cutterBot?.Corruption ?? int.MaxValue;
        if (cutterCorruptionBefore + SwarmSingleCutCorruptionCost >= Config.MAX_CORRUPTION)
        {
            _gameEventLogManager.LogSystem(
                matchingId,
                $"ORB_SINGLE_CUT_REFUSED attacker={creditPlayerId} victim={bestOwnerId} targetOrbUid={bestOrbUid} " +
                $"targetIndex={bestTailOrdinal} reason=cost attackerCorruption={cutterCorruptionBefore}");
            return;
        }

        // 봇 절단 자제: 봇은 오염이 절반 아래일 때, 봇 1인당 6초에 한 번만 자른다. 봇끼리 몇 초 간격으로 서로 자르며
        // 자해로 죽어 나가면 사람 카메라에 남는 긴 꼬리가 없다 — 봇의 절단은 '한 번 지르는 사건'으로 읽혀야 한다.
        // 거절된 통과는 래치를 찍어 같은 오브를 이번 통과에서 다시 판정하지 않는다 — 사람의 절단은 이 규칙과 무관하다.
        if (cutterBot != null && !IsSwarmBotCutAllowed(matchingId, cutterId, cutterCorruptionBefore, nowUtc))
        {
            GetSwarmMatchRuntime(matchingId).TrailCombat.OrbCutLatches[(matchingId, cutterId, bestOrbUid)] = nowUtc;
            return;
        }

        GetSwarmMatchRuntime(matchingId).TrailCombat.OrbCutLatches[(matchingId, cutterId, bestOrbUid)] = nowUtc;

        // #229 6단계: 절단은 내가 몸으로 지르는 가해다 — 교전 잠금을 찍어 절단하고 바로 눕는
        // 도주 회복을 막는다. 수면 해제는 안 건다 — 절단하러 움직인 순간 이동이 이미 깨웠다.
        cutterSession?.MarkSwarmCombat(nowUtc);

        // 절단 진입 계측 (#227 3·6단계): 공격자·피해자·후보 ordinal·그 자리를 덮던 적 오브
        // 사거리 수(국소 화망). 내구 1·즉시 파괴, 손실 = 후보 순번부터 꼬리 끝까지.
        _gameEventLogManager.LogSwarmCutAttempt(
            matchingId, creditPlayerId, bestOwnerId, bestTailOrdinal,
            CountSwarmOrbGunsCovering(chains, cutterId, cutterArea, current),
            CountSwarmOrbGunsCovering(chains, cutterId, cutterArea, current, bestOwnerId),
            durabilityBeforeHit: 1,
            expectedOrbLoss: Math.Max(0, chains[bestOwnerId].Points.Count - bestTailOrdinal),
            breaksNow: true,
            area: bestArea.ToString());

        // 접미 절단 (스네이크 문법, "오브 절단면 다 깨지게" 결정): 밟힌 오브(몸체) 또는
        // 링크 뒤쪽 첫 오브부터 꼬리 끝까지 전부 사라진다. 절단 지점이 머리에 가까울수록 손실이 크다.
        var destroyedItems = DestroySwarmOrbsFromOrdinal(matchingId, bestOwnerId, bestTailOrdinal);
        if (destroyedItems.Count == 0)
            return;
        var destroyedItem = destroyedItems[0];
        // 반격 보호 개시 (#227 7단계): 방금 자른 그 사람은 1.2초 동안 이 피해자를 다시 못 자른다.
        OpenSwarmRetaliationWindow(matchingId, creditPlayerId, bestOwnerId, bestArea, nowUtc, allSessions);
        var ownerSession = aliveSessions.FirstOrDefault(session => session.PlayerId == bestOwnerId);
        var ownerChain = chains[bestOwnerId];
        foreach (var lost in destroyedItems)
        {
            GetSwarmMatchRuntime(matchingId).TrailCombat.OrbDurabilityBonus.Remove((matchingId, bestOwnerId, lost.ItemUid));
            ownerSession?.SendInGameInventoryUpdate(lost);
        }
        // 절단 낙수 없음 (#232): 소환석·드롭·점수·웨이브 기여를 지급하지 않는다. 잃은 것은 그냥 사라진다.

        // 절단 파열 플래시: 링 + 잘린 꼬리 오브 섬광 — "어디부터 끊겼다"가 화면에서 읽히게.
        SendSwarmRingVfx(
            bestArea, creditPlayerId, bestOrbPosition.X, bestOrbPosition.Y,
            SwarmTrailCutFlashRadius, allSessions, SwarmRingVfxKindCut,
            victimId: bestOwnerId, fromOrdinal: bestTailOrdinal);

        // 공격자 치명상 (#232): 같은 사건으로 +35. 사람은 사격 피격 경로(오염 증가·피격 숫자)를 타고
        // 8초 수면 회복 차단이 걸린다. 봇은 오염만 오른다.
        DateTime healLockUntil = nowUtc.AddSeconds(SwarmSingleCutHealLockSeconds);
        int cutterCorruptionAfter;
        if (cutterSession != null)
        {
            cutterSession.ApplyProximityAutoCombatHit(
                cutterId, cutterArea, destroyedItem.ItemId, SwarmSingleCutCorruptionCost);
            cutterSession.SwarmHealLockUntilUtc = healLockUntil;
            cutterCorruptionAfter = cutterSession.CurrentCorruption;
        }
        else if (cutterBot != null)
        {
            cutterBot.Corruption = Math.Min(
                Config.MAX_CORRUPTION, cutterBot.Corruption + SwarmSingleCutCorruptionCost);
            cutterBot.LastDamagedAtUtc = nowUtc;
            GetSwarmMatchRuntime(matchingId).BotTactics.LastDamagedAtUtc[(matchingId, cutterBot.PlayerId)] = nowUtc;
            // 봇 절단 시각 — 절단 자제 쿨다운(IsSwarmBotCutAllowed)과 절단 후 회수 창이 읽는다.
            GetSwarmMatchRuntime(matchingId).BotTactics.LastTrailCutAtUtc[(matchingId, cutterBot.PlayerId)] = nowUtc;
            cutterCorruptionAfter = cutterBot.Corruption;
        }
        else
        {
            cutterCorruptionAfter = cutterCorruptionBefore;
        }

        var ownerBot = aliveBots.FirstOrDefault(candidate => candidate.PlayerId == bestOwnerId);
        if (ownerBot != null)
        {
            // 절단당한 봇은 피격 반응(도주 판단)으로 즉시 넘어간다.
            ownerBot.LastProximityAttackerPlayerId = creditPlayerId;
            ownerBot.LastDamagedAtUtc = nowUtc;
            GetSwarmMatchRuntime(matchingId).BotTactics.LastDamagedAtUtc[(matchingId, ownerBot.PlayerId)] = nowUtc;
            ownerBot.CancelChannelHold();
        }

        // 절단 전후 대차대조 (#227 5단계): 오브 수(=점수)·순위·공격 기여 수를 한 줄에 묶는다.
        // "몇 개 잃음 → 순위가 바뀜 → 다음 화력이 줄었다"가 한 이벤트에서 확인돼야
        // 전략이 먹혔는지 로그만으로 판정할 수 있다. 전은 파괴 직전 체인, 후는 재조회다.
        int attackOrbsBefore = CountSwarmAttackOrbs(ownerChain.ItemIds);
        int orbsBefore = ownerChain.ItemIds.Count;
        int orbsAfter = CountSwarmSquadOrbs(matchingId, bestOwnerId);
        int attackOrbsAfter = CountSwarmAttackOrbs(GetSwarmOrbItemIdsInOrder(matchingId, bestOwnerId));
        int rankAfter = GetSwarmPlayerRank(matchingId, bestOwnerId, aliveSessions, aliveBots);

        // 잃은 만큼 소환 비용을 되돌린다 (#229): 오브 수가 곧 소환 카운터라, 잘려 나간 몫이
        // 값에 남으면 절단당한 쪽이 재건 비용까지 떠안아 격차가 한 방향으로만 벌어진다.
        _summonStoneManager.RefundGrowthSuccess(
            matchingId, bestOwnerId, SwarmGrowthCardMultiply, destroyedItems.Count);

        _gameEventLogManager.LogSwarmTrailCut(
            matchingId, creditPlayerId, bestOwnerId, bestTailOrdinal, destroyedItems.Count,
            orbsBefore, orbsAfter, attackOrbsBefore, attackOrbsAfter, rankAfter,
            bestArea.ToString());
        // 필수 로그 (#232 §11): 절단 한 건 = 공격자·피해자·절단 순번·잃은 수·공격자 오염 전후·회복 차단 만료.
        _gameEventLogManager.LogSystem(
            matchingId,
            $"ORB_TAIL_CUT attacker={creditPlayerId} victim={bestOwnerId} cutIndex={bestTailOrdinal} " +
            $"lostOrbs={destroyedItems.Count} firstOrbUid={destroyedItem.ItemUid} " +
            $"attackerCorruptionBefore={cutterCorruptionBefore} attackerCorruptionAfter={cutterCorruptionAfter} " +
            $"healLockUntil={healLockUntil:O} victimOrbsBefore={orbsBefore} victimOrbsAfter={orbsAfter} area={bestArea}");
        logger.LogInformation(
            "Swarm tail cut: MatchingId={MatchingId}, CutterId={CutterId}, OwnerId={OwnerId}, TailOrdinal={TailOrdinal}, Lost={Lost}, AttackerCorruption={Before}->{After}",
            matchingId, cutterId, bestOwnerId, bestTailOrdinal, destroyedItems.Count,
            cutterCorruptionBefore, cutterCorruptionAfter);
    }

    // 링 연출 종류: 클라가 색·효과음을 분기한다. 크랙(3)은 링 없이 슬롯 크랙 + 크랙음만 —
    // Radius 필드에 단계(1~4)를 실어 보낸다.
    private const int SwarmRingVfxKindEncircle = 0;
    private const int SwarmRingVfxKindCut = 1;
    private const int SwarmRingVfxKindWaveBomb = 2;
    // 반격 보호 (#227 7단계): 5 = 피해자 남은 꼬리의 유리 잔광 개시(Radius에 지속 초),
    // 6 = 그 절단자의 투사체가 잔광 앞에서 깨짐(피해 숫자 없음).
    private const int SwarmRingVfxKindRetaliationGuard = 5;
    private const int SwarmRingVfxKindRetaliationBlocked = 6;

    // 즉시 절단: 밟으면 바로 그 지점부터 꼬리가 끊긴다.
    // 크랙 단계 시스템(1~4 금 + 5타 파괴)은 값만 되돌리면 복원된다.
    private const int SwarmTrailCutBreakHits = 1;
    // 오브 체력 모델 (#227): 모든 오브의 최대 내구는 5칸이고, 소환 직후는 1/5로 시작한다.
    // 방어 강화는 5/5로 채우는 카드다 — 크랙 5단계가 곧 남은 칸이라 표시가 곧 판정.
    private const int SwarmOrbMaxDurability = 5;
    private const int SwarmArmorDurabilityBonus = SwarmOrbMaxDurability - SwarmTrailCutBreakHits;

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

    // ===== 파도 = 소용돌이 (#268): 파도 오브 각각이 주기(2초)마다 자기 열 위치에 소용돌이를 깐다 — 오브가 곧
    // 무기 위치라는 점에서 바람 회전 칼날과 같은 문법. 예고(0.65초 림 링) 후 반경 안 전원을 잠깐 늦춘다(침수) —
    // 피해는 타격 피드백 수준(1/4). 예고 원점은 스폰 순간 고정. 주기·예고의 원천은 swarm_config.csv (#335). =====
    private static double SwarmWaveBombIntervalSeconds =>
        SwarmConfigData.GetDouble("SWARM_WAVE_VORTEX_INTERVAL_SECONDS", 2d);
    private static double SwarmWaveBombFuseSeconds =>
        SwarmConfigData.GetDouble("SWARM_WAVE_VORTEX_FUSE_SECONDS", 0.65d);

    // 오브별 독립 시계("다같이 터지는 게 어색"). 파도 폭탄 상태(위상·대기열)는 GetSwarmMatchRuntime(matchingId).TrailCombat.

    private void ProcessSwarmWaveBombs(
        long matchingId,
        DateTime nowUtc,
        List<SwarmParticipantSpatial> participants,
        List<GameClientSession> aliveSessions,
        List<BotPlayerState> aliveBots,
        List<GameClientSession> allSessions)
    {
        // 1) 기폭: 예약된 소용돌이 정산.
        for (int index = GetSwarmMatchRuntime(matchingId).TrailCombat.PendingWaveBombs.Count - 1; index >= 0; index--)
        {
            var vortex = GetSwarmMatchRuntime(matchingId).TrailCombat.PendingWaveBombs[index];
            if (vortex.MatchingId != matchingId || nowUtc < vortex.ExplodeAtUtc)
                continue;
            GetSwarmMatchRuntime(matchingId).TrailCombat.PendingWaveBombs.RemoveAt(index);
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
                if (!GetSwarmMatchRuntime(matchingId).TrailCombat.WaveBombNextDropAtUtc.TryGetValue(orbKey, out var nextDropAtUtc))
                {
                    // 고유 위상: 첫 발동을 0.5~1.5주기 사이에 흩뿌린다 — uid라 재접속에도 안정.
                    double phase = 0.5d + item.ItemUid % 977 / 977d;
                    GetSwarmMatchRuntime(matchingId).TrailCombat.WaveBombNextDropAtUtc[orbKey] =
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
                tiers ??= GetSwarmOrbTiersInOrder(matchingId, owner.PlayerId);
                var orbPosition = GetSwarmOrbTrailPosition(
                    matchingId, owner.PlayerId, ordinal, owner.Position, tiers);
                vortexTargets ??= _swarmMonsterDirector.GetCombatTargets(matchingId);
                bool hasTarget = false;
                foreach (var target in vortexTargets)
                {
                    if (target.Area != owner.Area ||
                        !IsWithinSwarmGroundRadius(
                            orbPosition, target.Position, radius + SwarmWindBladeMonsterRadius))
                        continue;
                    hasTarget = true;
                    break;
                }

                if (!hasTarget)
                {
                    foreach (var participant in participants)
                    {
                        if (participant.PlayerId == owner.PlayerId || participant.Area != owner.Area ||
                            !IsWithinSwarmGroundRadius(
                                orbPosition, participant.Position, radius + SwarmCrossfirePlayerRadius))
                            continue;
                        hasTarget = true;
                        break;
                    }
                }

                if (!hasTarget)
                    continue;

                // 비무장(소환·채집 중)이어도 시계는 돈다 — 칼날·미사일과 같은 규칙.
                GetSwarmMatchRuntime(matchingId).TrailCombat.WaveBombNextDropAtUtc[orbKey] = nowUtc.AddSeconds(SwarmWaveBombIntervalSeconds);

                if (sunMultiplier < 0f)
                    sunMultiplier = OrbData.GetSunPveAttackMultiplier(trailOrbs);
                int damage = Math.Max(1, (int)MathF.Round(
                    baseDamage * sunMultiplier * Config.SWARM_WAVE_VORTEX_DAMAGE_MULTIPLIER));

                // 스폰 = 그 오브의 현재 열 좌표(사거리 게이트가 계산한 그 지점) — 스폰 순간 고정.
                GetSwarmMatchRuntime(matchingId).TrailCombat.PendingWaveBombs.Add((
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
                _gameEventLogManager.LogSystem(
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
        foreach (var target in _swarmMonsterDirector.GetCombatTargets(matchingId))
        {
            if (target.Area != area)
                continue;
            float dx = target.Position.X - position.X;
            float dy = (target.Position.Y - position.Y) * 2f;
            if (dx * dx + dy * dy > radiusSquared)
                continue;
            int monsterDamage = RollSwarmCriticalDamage(matchingId, damage, out bool critical);
            _swarmMonsterDirector.ReserveMonsterDamage(matchingId, target.CombatTargetId, monsterDamage);
            _swarmMonsterDirector.RecordMonsterAttackEvent(matchingId, target.CombatTargetId);
            GetSwarmMatchRuntime(matchingId).Pacing.PendingMonsterHits.Add(new PendingSwarmMonsterHit(
                matchingId, target.CombatTargetId, ownerId, monsterDamage, nowUtc));
            _swarmMonsterDirector.TrySlowMonster(
                matchingId, target.CombatTargetId, OrbData.WaveSlowSeconds, nowUtc);
            hitCount++;

            int monsterId = _swarmMonsterDirector.GetMonsterIdForCombatTarget(matchingId, target.CombatTargetId);
            if (monsterId <= 0)
                continue;

            notifiedCount++;
            ownerSession?.SendSwarmAfterimageMonsterAttackFeedback(
                monsterId, area, sourceItemId, monsterDamage, critical, noProjectile: true);
        }

        // 플레이어: 같은 반경(바닥면 타원) + 몸통 여유. 소유자 제외 — 침수 디버프 + 피해.
        int soaked = 0;
        foreach (var participant in participants)
        {
            if (participant.PlayerId == ownerId || participant.Area != area || participant.Position == null)
                continue;
            if (!IsWithinSwarmGroundRadius(position, participant.Position, radius + SwarmCrossfirePlayerRadius))
                continue;

            // 충격 면역 없음: 겹친 링에 다 맞는다 — 침수는 지속 갱신이라 중첩 무해.
            soaked++;
            ApplySwarmShock(matchingId, ownerId, sourceItemId, area, participant.PlayerId,
                "WAVE_VORTEX_HIT", aliveSessions, aliveBots, allSessions,
                Config.SWARM_WAVE_VORTEX_DAMAGE_MULTIPLIER);

            var victimSession = aliveSessions.FirstOrDefault(
                session => session.PlayerId == participant.PlayerId);
            if (victimSession != null)
            {
                // 사람: 클라가 감속을 적용하고 디버프 창에 침수를 띄운다.
                victimSession.SendSwarmWaveSlow(
                    ownerId, area, (int)(OrbData.WaveSlowSeconds * 1000f));
                continue;
            }

            var bot = aliveBots.FirstOrDefault(candidate => candidate.PlayerId == participant.PlayerId);
            if (bot != null)
                bot.WaveSlowUntilUtc = nowUtc.AddSeconds(OrbData.WaveSlowSeconds);
        }

        if (hitCount > 0 || soaked > 0)
        {
            _gameEventLogManager.LogSystem(
                matchingId,
                $"wave_vortex_hit owner={ownerId} area={area} monsters={hitCount} " +
                $"notified={notifiedCount} playersSoaked={soaked} radius={radius:F2} " +
                $"damage={damage} item={sourceItemId}");
        }
    }

    // ===== 절단 실험 더미 (#226): 매치의 봇 하나를 운동장 과녁으로 바꾼다 —
    // 정지·불사·오브 10개 일자 꼬리(자동 리필)·비무장·몹 절단 면제. 웨이브 디렉터 제외.
    // 명시적 분리 (단계 0): DEV_CUT_DUMMY=1 환경변수 옵트인 — 일반 매치는 순정으로 돈다. =====
    private static readonly bool SwarmCutDummyAutoSetup =
        Environment.GetEnvironmentVariable("DEV_CUT_DUMMY") == "1";

    // 교차사격 샌드박스 (#232 2단계): DEV_CROSSFIRE_SANDBOX=1 — 절단 실험장과 같은 격리
    // (운동장 더미 하나 + 나머지 봇 퇴장)를 쓰되(몹 접촉 피해는 켜 둔다), 더미는 태양 T1 3개·철갑
    // 없음·무장(몹을 쏜다)이다. 사람 오브도 무장 — 실험 대상이 절단 궤적이 아니라
    // 몹을 향한 사격이 만드는 직선이기 때문이다. user_server 같은 env가 전원을 운동장에 스폰한다.
    private static readonly bool SwarmCrossfireSandbox =
        Environment.GetEnvironmentVariable("DEV_CROSSFIRE_SANDBOX") == "1";
    private static bool SwarmDummySandboxActive => SwarmCutDummyAutoSetup || SwarmCrossfireSandbox;
    private const int SwarmCutDummyOrbCount = 10;
    private const int SwarmCrossfireDummyOrbCount = 3;
    private static int SwarmDummyOrbCount => SwarmCrossfireSandbox ? SwarmCrossfireDummyOrbCount : SwarmCutDummyOrbCount;
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
        if (CountSwarmSquadOrbs(matchingId, dummy.PlayerId) >= SwarmDummyOrbCount)
        {
            GetSwarmMatchRuntime(matchingId).Pacing.CutDummyRefillAtUtc.Remove(key);
            return;
        }

        if (!GetSwarmMatchRuntime(matchingId).Pacing.CutDummyRefillAtUtc.TryGetValue(key, out var refillAtUtc))
        {
            GetSwarmMatchRuntime(matchingId).Pacing.CutDummyRefillAtUtc[key] = nowUtc.AddSeconds(SwarmCutDummyRefillDelaySeconds);
            return;
        }

        if (nowUtc < refillAtUtc)
            return;

        GetSwarmMatchRuntime(matchingId).Pacing.CutDummyRefillAtUtc.Remove(key);
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
             index < SwarmDummyOrbCount;
             index++)
            _inGameInventoryManager.TryAddItemWithCapacity(
                matchingId, dummy.PlayerId, SwarmCutDummyOrbItemId, Config.SWARM_ORB_CAPACITY, out _);

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
                GetSwarmMatchRuntime(matchingId).TrailCombat.OrbDurabilityBonus[key] = SwarmArmorDurabilityBonus;
            else
                GetSwarmMatchRuntime(matchingId).TrailCombat.OrbDurabilityBonus.Remove(key);
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
            var humanIds = activeIds.Where(id => GetSessionsByInstance(Config.SWARM_MATCH_MAP, id)
                .Any(session => session.PlayerId.HasValue)).ToList();
            matchingId = (humanIds.Count > 0 ? humanIds : activeIds).DefaultIfEmpty(0).Max();
        }

        if (matchingId <= 0)
            return new { error = "no active match" };

        if (!MatchRuntimes.Enter(matchingId, out MatchScope scope))
            return new { error = "match is no longer active " + matchingId };

        using (scope)
        {
            if (scope.Runtime.IsTerminal)
                return new { error = "match is no longer active " + matchingId };

            object result = SetupSwarmCutDummyCore(matchingId, out BotMovementEvent? movement);
            if (movement != null)
                DispatchSwarmExternalBotMovement(matchingId, movement);
            return result;
        }
    }

    /// <summary>수동 더미 이동을 매치 잠금 안에서 계획·송신한다 — 궤도는 돌리지 않는다.</summary>
    private void DispatchSwarmExternalBotMovement(long matchingId, BotMovementEvent movement)
    {
        GameClientSession[] sessionSnapshot = _sessionRegistry.GetByMatch(matchingId)
            .Where(session =>
                session.PlayerId is > 0 &&
                session.CurrentMapId == Config.SWARM_MATCH_MAP &&
                session.CurrentMapSubId == matchingId)
            .ToArray();
        ImmutableArray<SwarmBotObserverSnapshot> observers =
            CaptureSwarmBotObservers(matchingId, sessionSnapshot);
        SwarmBotMovementPlan plan = _swarmBotMovementCoordinator.PrepareExternalMovement(
            matchingId,
            movement,
            observers);
        DispatchSwarmBotMovementPlan(plan, sessionSnapshot);
    }

    private object SetupSwarmCutDummyCore(long matchingId, out BotMovementEvent? movement)
    {
        movement = null;
        var bots = _botPlayerManager.GetBots(matchingId)
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
        dummy.Corruption = 0;

        // 꼬리: 동→서 일자 경로를 미리 심는다 — points[0] = 현재 위치(최신).
        var trailPoints = new List<Vector3f>();
        for (float distance = 0f; distance <= 12f; distance += 0.3f)
            trailPoints.Add(new Vector3f(dummy.Position.X - distance, dummy.Position.Y, 0f));
        GetSwarmMatchRuntime(matchingId).TrailCombat.OrbTrails[(matchingId, dummy.PlayerId)] = trailPoints;
        GetSwarmMatchRuntime(matchingId).TrailCombat.TrailLastTickPositions[(matchingId, dummy.PlayerId)] =
            new Vector3f(dummy.Position.X, dummy.Position.Y, 0f);
        // 시작 지급의 무작위 색(파도 포함)을 비우고 단색 태양 열로 재구성한다 (#227).
        _inGameInventoryManager.TakeAllItems(matchingId, dummy.PlayerId);
        GetSwarmMatchRuntime(matchingId).Pacing.CutDummyRefillAtUtc.Remove((matchingId, dummy.PlayerId));
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
    ///     더미 WASD 조종 (#226 실험장): 클라 방향 입력을 스텝 이동으로 적용하고 걷기를
    ///     브로드캐스트한다 — 더미가 움직여야 클라 열이 자연 간격(0.9)으로 펼쳐진다.
    /// </summary>
    private void MoveSwarmCutDummy(long matchingId, float dirX, float dirY)
    {
        if (!MatchRuntimes.Enter(matchingId, out MatchScope scope))
            return;

        using (scope)
        {
            if (scope.Runtime.IsTerminal)
                return;

            BotMovementEvent? movement = MoveSwarmCutDummyCore(matchingId, dirX, dirY);
            if (movement != null)
                DispatchSwarmExternalBotMovement(matchingId, movement);
        }
    }

    private BotMovementEvent? MoveSwarmCutDummyCore(long matchingId, float dirX, float dirY)
    {
        var dummy = _botPlayerManager.GetBots(matchingId)
            .FirstOrDefault(bot => bot.IsSwarmCutDummy && !bot.IsEliminated);
        if (dummy == null)
            return null;

        float length = MathF.Sqrt(dirX * dirX + dirY * dirY);
        if (length < 0.01f)
            return null;

        // 10Hz 전송 기준 스텝 0.5 = 5u/s — 플레이어 달리기와 동급.
        const float step = 0.5f;
        var proposed = new Vector3f(
            dummy.Position.X + dirX / length * step,
            dummy.Position.Y + dirY / length * step,
            0f);
        var proposedCell = MapCoordinateConverter.WorldToCell(Config.SWARM_MATCH_MAP, proposed);
        if (!GameMapData.IsMoveablePosition(Config.SWARM_MATCH_MAP, proposedCell))
            return null;

        var fromArea = dummy.CurrentArea;
        var fromCell = dummy.Cell;
        dummy.Position = proposed;
        dummy.Cell = proposedCell;
        var currentArea = GameMapData.GetCurrentArea(Config.SWARM_MATCH_MAP, proposedCell);
        if (currentArea != AreaType.None)
            dummy.CurrentArea = currentArea;

        return new BotMovementEvent
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
        };
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
        position = null!;
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
        position = null!;
        foreach (var other in _botPlayerManager.GetBots(matchingId))
        {
            if (other.PlayerId != playerId || other.IsEliminated) continue;
            position = other.Position;
            return true;
        }

        foreach (var session in GetSessionsByInstance(Config.SWARM_MATCH_MAP, matchingId))
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

    private void LogSwarmChaseIssued(long matchingId, long chaserId, long targetId, Vector3f aimPoint)
    {
        var now = DateTime.UtcNow;
        var key = (matchingId, chaserId, targetId);
        if (GetSwarmMatchRuntime(matchingId).BotTactics.ChaseLogThrottle.TryGetValue(key, out var lastAtUtc) &&
            (now - lastAtUtc).TotalSeconds < 3d)
            return;

        GetSwarmMatchRuntime(matchingId).BotTactics.ChaseLogThrottle[key] = now;
        _gameEventLogManager.LogSystem(matchingId,
            $"swarm_chase chaser={chaserId} target={targetId} " +
            $"targetOrbs={CountSwarmSquadOrbs(matchingId, targetId)} " +
            $"aim=({aimPoint.X:F1},{aimPoint.Y:F1})");
    }


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
        if (_swarmMonsterDirector.IsWavePatternMonster(matchingId, damage.MonsterId))
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
            // 피격은 수면을 깨지 않는다 — 자면서 맞는 건 본인의 선택이다.
            // 3초 진입 잠금만 찍어 맞자마자 새로 눕는 것은 계속 막는다.
            session.MarkSwarmCombat(DateTime.UtcNow);
            // #229: 문 게이지도 같이 끊는다 — 문 앞을 비우지 못하면 방을 못 연다.
            session.BreakDoorUnlockGauge();
            // 오염 경로 — 오염 증가·피격 피드백·일반 탈락 흐름까지 담당한다.
            session.ApplySwarmAfterimageMonsterHit(damage.MonsterId, damage.Damage);
            return;
        }

        var bot = aliveBots.FirstOrDefault(candidate => candidate.PlayerId == damage.TargetPlayerId);
        if (bot == null)
            return;

        // 반올림으로 맞춘다 (#229 4단계-보정): 잘라내기라 raw 6(배율 통과 3)이 1로, raw 8(4)이
        // 2로 뭉개져 페이즈별 접촉 곡선이 봇에게는 통째로 평평했다. 사람 경로는 Round를 쓴다.
        int botDamage = Math.Max(1, (int)MathF.Round(damage.Damage * SwarmBotContactDamageMultiplier));
        int legacyBefore = bot.Corruption;
        bot.Corruption = Math.Min(Config.MAX_CORRUPTION, bot.Corruption + botDamage);
        GetSwarmMatchRuntime(matchingId).BotTactics.LastDamagedAtUtc[(matchingId, bot.PlayerId)] = DateTime.UtcNow;
        // 세 번째 봇 경로도 남긴다 — 앞의 두 경로만 로그를 붙여 놓으면 여기로 빠진 피해가
        // 그대로 안 보인다.
        _gameEventLogManager.LogSwarmAfterimageHit(
            matchingId, damage.MonsterId, bot.PlayerId, damage.Area.ToString(),
            botDamage, legacyBefore, bot.Corruption,
            bot.Corruption >= Config.MAX_CORRUPTION, isBot: true, DateTimeOffset.UtcNow);
    }

    /// <summary>앞줄 오브 = 최저 티어·선입(ItemUid) — 피해·표시가 같은 기준을 읽는다.</summary>
    private InGameItemInfo? FindSwarmFrontOrb(long matchingId, long playerId)
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
    ///     낮은 쪽) → PlayerId 낮은 쪽. 단독 생존 조기 종료와 같은 TryEndMatch
    ///     경로라 결과 화면도 같다. 잼 승점(#222 M3-2)은 퇴역.
    /// </summary>
    private bool ProcessSwarmScoreTimeout(
        long matchingId,
        DateTime nowUtc,
        List<GameClientSession> sessions,
        List<GameClientSession> aliveSessions,
        List<BotPlayerState> aliveBots)
    {
        if (DevFlags.DisableGameEnd || GetSwarmMatchRuntime(matchingId).Pacing.TimeoutEndedMatchings.Contains(matchingId))
            return false;

        var startedAtUtc = MatchStartGate.GetGameplayStartedAtUtc(matchingId);
        if (startedAtUtc == null)
        {
            // 봇 전용 매치(어드민 검증)는 게이트가 없다 — 스웜 첫 틱을 앵커로 대신 쓴다.
            if (!GetSwarmMatchRuntime(matchingId).Pacing.MatchFallbackAnchorUtc.TryGetValue(matchingId, out var fallbackAnchor))
            {
                GetSwarmMatchRuntime(matchingId).Pacing.MatchFallbackAnchorUtc[matchingId] = nowUtc;
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
            .ThenByDescending(candidate => GetSwarmMatchRuntime(matchingId).TrailCombat.OrbDurabilityBonus
                .Where(pair => pair.Key.MatchingId == matchingId &&
                               pair.Key.PlayerId == candidate.PlayerId)
                .Sum(pair => pair.Value))
            .ThenBy(candidate => candidate.Corruption)
            .ThenBy(candidate => candidate.PlayerId)
            .ToList();
        long winnerId = candidates.Count > 0 ? candidates[0].PlayerId : 0;
        GetSwarmMatchRuntime(matchingId).Pacing.TimeoutEndedMatchings.Add(matchingId);
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
            resultHost.TryEndMatch(winnerId, "orb_score_timeout");
            CleanupMatchSettlementState(matchingId);
            return true;
        }

        EndBotOnlyMatchIfSettled(matchingId, winnerId);
        return true;
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
        bool isFirstBroadcast = !GetSwarmMatchRuntime(matchingId).Pacing.JamRankingsSignature.TryGetValue(matchingId, out var previous);
        if (!isFirstBroadcast && previous == signature)
            return;

        GetSwarmMatchRuntime(matchingId).Pacing.JamRankingsSignature[matchingId] = signature;
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
    // 성장 직후 오퍼 휴지는 없다: 성장이 상시 버튼이라 석이 남아 있는데 오퍼가 서지 않으면 버튼이 "아직 성장할 수
    // 없습니다"로 답하는 벽이 된다. 성장 속도는 소환석 수입과 비용 곡선(5+2N)이 정한다.
    // 이중 차감은 오퍼 소유권이 막는다: 픽이 성립하면 오퍼가 사라지고, 같은 OfferId로 온 두 번째
    // 요청은 대조에서 걸러진다. 클라도 응답 전까지 입력을 잠근다.
    private const int SwarmGrowthCardMultiply = SwarmGrowthOfferState.CardMultiply;
    private const int SwarmGrowthCardEnhance = SwarmGrowthOfferState.CardEnhance;
    private const int SwarmGrowthCardArmor = SwarmGrowthOfferState.CardArmor;

    // 오퍼 서술자(SwarmGrowthOfferState)·오퍼/내구 상태는 #294에서
    // Services/SwarmArenaStates.cs의 매치별 GrowthOffers·TrailCombat으로 이동.

    /// <summary>열 순서의 오브 목록 — 강화·철갑의 "가장 앞" 판정과 트레일 순번의 단일 출처.</summary>
    private List<InGameItemInfo> GetSwarmTrailOrbs(long matchingId, long playerId) =>
        _inGameInventoryManager.GetPlayerInventory(matchingId, playerId)
            .GetAllItems()
            .Where(item => item.Count > 0 && GetSquadOrbTier(item.ItemId) > 0)
            // uid 오름차순 = 열 순번·링 마스크·카드 대상의 단일 정렬 (통일)
            .OrderBy(item => item.ItemUid)
            .ToList();

    /// <summary>
    ///     방어 강화(내구 2+) 오브 순번 마스크 — 클라 은백 링 표시용 (#226).
    ///     순서는 비주얼 브로드캐스트의 OrbItemIds와 동일한 ItemUid 오름차순 — 슬롯 인덱스 정합.
    /// </summary>
    private long GetSwarmArmorMask(long matchingId, long playerId)
    {
        long mask = 0;
        var orbs = GetSwarmTrailOrbs(matchingId, playerId);
        for (int ordinal = 0; ordinal < orbs.Count && ordinal < 64; ordinal++)
            if (GetSwarmMatchRuntime(matchingId).TrailCombat.OrbDurabilityBonus.ContainsKey((matchingId, playerId, orbs[ordinal].ItemUid)))
                mask |= 1L << ordinal;
        return mask;
    }

    /// <summary>
    ///     성장 비용 (#229): 기본 = 5+2N, 오브 수 할증 없음, 최종 = min(21, 기본).
    ///     0오브는 최종 3으로 T1 재건을 보장한다.
    /// </summary>
    /// <summary>
    ///     카드별 비용 (#229): 소환·공격 강화·방어 강화가 각자 자기 성공 횟수로 값을 매긴다.
    ///     예전엔 셋이 한 카운터를 공유해 오브를 늘릴수록 강화가 비싸지고 그 반대도 됐다 —
    ///     한 축에 투자하면 다른 축이 벌을 받는 구조라 빌드를 고르는 의미가 사라진다.
    ///     BaseCost·Surcharge·FinalCost는 계측 호환을 위해 가장 싼 카드 기준으로 남긴다.
    /// </summary>
    private (int BaseCost, int Surcharge, int FinalCost, int OrbCount,
        int CostSummon, int CostAttack, int CostDefense) GetSwarmGrowthCostBreakdown(
        long matchingId, long playerId)
    {
        var (orbCount, _) = GetSwarmOrbScore(matchingId, playerId);
        int CardCost(int cardIndex) => Config.GetSwarmGrowthCardCost(
            _summonStoneManager.GetGrowthSuccessCount(matchingId, playerId, cardIndex), orbCount);

        int summon = CardCost(SwarmGrowthCardMultiply);
        int attack = CardCost(SwarmGrowthCardEnhance);
        int defense = CardCost(SwarmGrowthCardArmor);
        int cheapest = Math.Min(summon, Math.Min(attack, defense));
        int growthCount = _summonStoneManager.GetGrowthSuccessCount(matchingId, playerId);
        return (
            Config.GetSwarmGrowthBaseCost(growthCount),
            Config.GetSwarmGrowthScoreSurcharge(orbCount),
            cheapest,
            orbCount,
            summon, attack, defense);
    }

    /// <summary>
    ///     오퍼 구성 확정 (#226 C 등급): 품질은 기본 비용 구간이 정한다.
    ///     오브 생성 = 색 균등 + 티어(기본 5+ T2 20% · 기본 7+ T2 35%/T3 10%),
    ///     공격 강화 = 선두 유효 대상 티어(I=T1→T2, II=T2→T3),
    ///     방어 강화 = 외피 장수(기본 1, 기본 5+ 15% · 기본 7+ 30% 확률로 2 — 무외피 수 캡).
    ///     0오브(재건 보장): 비용 3의 T1 생성만 유효 — 이 재건도 N에 포함된다.
    /// </summary>
    private SwarmGrowthOfferState GenerateSwarmGrowthOffer(
        long matchingId, long playerId, int offerId, int finalCost, int qualityCost, int orbCount,
        int costSummon, int costAttack, int costDefense)
    {
        if (orbCount <= 0)
        {
            int rebuildItemId = SwarmStartingOrbPool[Random.Shared.Next(SwarmStartingOrbPool.Length)];
            return new SwarmGrowthOfferState(
                offerId, finalCost, rebuildItemId,
                EnhanceTargetTier: 0, ArmorCount: 0,
                CostSummon: costSummon, CostAttack: costAttack, CostDefense: costDefense);
        }

        // 소환은 늘 T1: 티어는 오브마다 계열 버튼으로 따로 산다 — 공유 레벨 상속은 퇴역.
        // 품질 티어 RNG도 퇴역.
        _ = qualityCost;
        int spawnItemId = SwarmStartingOrbPool[Random.Shared.Next(SwarmStartingOrbPool.Length)];

        int armorSlots = GetSwarmTrailOrbs(matchingId, playerId)
            .Count(item => !GetSwarmMatchRuntime(matchingId).TrailCombat.OrbDurabilityBonus.ContainsKey((matchingId, playerId, item.ItemUid)));
        int armorCount = armorSlots <= 0 ? 0 : 1;
        if (armorCount > 0 && armorSlots >= 2)
        {
            int armorRoll = Random.Shared.Next(100);
            if (qualityCost >= 7 && armorRoll < 30 || qualityCost >= 5 && armorRoll < 15)
                armorCount = 2;
        }

        // 6/6 포화 (#232 4단계): 소환 카드가 닫힌다 — 파괴로 빈칸을 만들어야 다시 열린다.
        if (GetSwarmTrailOrbs(matchingId, playerId).Count >= Config.SWARM_ORB_CAPACITY)
            spawnItemId = 0;

        return new SwarmGrowthOfferState(
            offerId,
            finalCost,
            spawnItemId,
            // 개별 강화 퇴역 (#232 4단계) — 계열 강화 버튼이 대신한다.
            EnhanceTargetTier: 0,
            armorCount,
            costSummon,
            costAttack,
            costDefense);
    }

    /// <summary>
    ///     성장 오퍼 틱: 사람은 소환석이 비용에 닿는 즉시 오퍼 패킷(3택), 봇은 같은 규칙으로
    ///     즉시 자동 투자한다. 성공한 선택은 다음 카드별 비용 곡선에 바로 반영된다.
    /// </summary>
    // 비용 예고·재전송·선택 소유권은 match-owned SwarmGrowthOfferCoordinator가 맡는다.

    private void ProcessSwarmGrowthOffers(
        long matchingId, DateTime nowUtc,
        List<GameClientSession> aliveSessions, List<BotPlayerState> aliveBots)
    {
        SwarmGrowthOfferCoordinator growthOffers =
            GetSwarmMatchRuntime(matchingId).GrowthOfferCoordinator;

        foreach (var session in aliveSessions)
        {
            if (!session.PlayerId.HasValue)
                continue;
            long playerId = session.PlayerId.Value;
            SwarmStandingOfferDecision standing = growthOffers.EvaluateStanding(playerId, nowUtc);
            if (standing.Action != SwarmStandingOfferAction.Missing)
            {
                // 서 있는 오퍼는 주기적으로 다시 보낸다 (#229 7단계): 오퍼는 한 번만 나가므로
                // UI가 늦게 붙거나 그 한 패킷을 놓치면 버튼이 영영 "못 삼"으로 남는다.
                // 오퍼가 곧 구매 가능 신호라 이 재전송이 버튼 색의 자가 복구다.
                if (standing.Action == SwarmStandingOfferAction.Resend)
                {
                    SwarmGrowthOfferState standingOffer = standing.Offer;
                    session.SendSwarmGrowthOffer(
                        standingOffer.OfferId, standingOffer.Cost, standingOffer.SpawnItemId,
                        standingOffer.EnhanceTargetTier, standingOffer.ArmorCount,
                        standingOffer.CostSummon, standingOffer.CostAttack, standingOffer.CostDefense);
                }

                continue;
            }
            var (baseCost, surcharge, finalCost, orbCount, costSummon, costAttack, costDefense) =
                GetSwarmGrowthCostBreakdown(matchingId, playerId);
            SwarmGrowthFundingAction funding = growthOffers.EvaluateFunding(
                playerId,
                nowUtc,
                _summonStoneManager.GetSnapshot(matchingId, playerId).StoneCount,
                finalCost);
            if (funding != SwarmGrowthFundingAction.ReadyToCreate)
            {
                // 비용 예고 (#229 7단계): 아직 못 사도 얼마가 필요한지는 늘 보여야 버튼이
                // "모으는 중"으로 읽힌다. OfferId 0 = 표시 전용, 고를 수 없음.
                // 소환석 상태의 NextCost는 구 소환 곡선(삼각수)이라 이 값과 다르다 —
                // 성장 게이트의 단일 출처는 GetSwarmGrowthCardCost뿐이다.
                // 값이 바뀔 때만 보내면 UI가 늦게 붙었을 때 그 한 번을 놓치고 비용이 영영 비어
                // 있다 — 서 있는 오퍼와 같은 주기로 다시 보내 표시가 스스로 복구되게 한다.
                if (funding == SwarmGrowthFundingAction.SendPreview)
                    session.SendSwarmGrowthOffer(
                        0, finalCost, 0, 0, 0, costSummon, costAttack, costDefense);

                continue;
            }

            var offer = GenerateSwarmGrowthOffer(
                matchingId, playerId, growthOffers.AllocateOfferId(), finalCost, baseCost, orbCount,
                costSummon, costAttack, costDefense);
            growthOffers.RegisterOffer(playerId, offer);
            _gameEventLogManager.LogSwarmGrowthOffered(
                matchingId, playerId, isBot: false, baseCost, surcharge, finalCost, orbCount);
            session.SendSwarmGrowthOffer(
                offer.OfferId, offer.Cost, offer.SpawnItemId, offer.EnhanceTargetTier, offer.ArmorCount,
                offer.CostSummon, offer.CostAttack, offer.CostDefense);
        }

        foreach (var bot in aliveBots)
        {
            if (bot.IsSwarmCutDummy)
                continue;
            var (baseCost, surcharge, finalCost, orbCount, costSummon, costAttack, costDefense) =
                GetSwarmGrowthCostBreakdown(matchingId, bot.PlayerId);
            if (_summonStoneManager.GetSnapshot(matchingId, bot.PlayerId).StoneCount < finalCost)
                continue;

            var offer = GenerateSwarmGrowthOffer(
                matchingId, bot.PlayerId, growthOffers.AllocateOfferId(), finalCost, baseCost, orbCount,
                costSummon, costAttack, costDefense);
            // 계열 강화 (#232 4단계): 6/6 포화면 소환이 닫히므로 강화가 봇의 주 지출이 된다.
            // 그 전에도 오브 4개 이상이면 셋에 한 번은 강화를 시도한다 — 카드 정책의 공격 강화
            // 자리를 잇는 셈이다 (개별 강화 카드는 퇴역).
            bool preferFamilyUpgrade = orbCount >= Config.SWARM_ORB_CAPACITY ||
                                       (orbCount >= 4 && Random.Shared.Next(3) == 0);
            if (preferFamilyUpgrade && TryUpgradeSwarmFamilyForBot(matchingId, bot.PlayerId))
                continue;

            int cardIndex = ChooseSwarmBotGrowthCard(
                matchingId, bot, offer, orbCount, aliveSessions, aliveBots);
            if (cardIndex == SwarmGrowthCardEnhance)
            {
                TryUpgradeSwarmFamilyForBot(matchingId, bot.PlayerId);
                continue;
            }
            bool applied = ApplySwarmGrowthCard(matchingId, bot.PlayerId, cardIndex, offer, session: null);
            if (applied)
            {
                int successCountBefore = _summonStoneManager.GetGrowthSuccessCount(matchingId, bot.PlayerId);
                // 카드별 카운터는 봇도 함께 민다 (#229) — 안 그러면 봇만 값이 안 올라
                // 사람보다 싸게 무한 성장하고, 봇 매치로 곡선을 검증할 수도 없다.
                _summonStoneManager.RecordGrowthSuccess(matchingId, bot.PlayerId, cardIndex);
                _gameEventLogManager.LogSwarmGrowthSelected(
                    matchingId, bot.PlayerId, isBot: true,
                    GetSwarmGrowthCardRole(cardIndex), GetSwarmGrowthCardGrade(cardIndex, offer),
                    baseCost, surcharge, offer.GetCost(cardIndex), successCountBefore, orbCount);
            }
        }
    }

    /// <summary>생존자 최다 오브 수 — 봇 성장·추격 판단의 순위 기준.</summary>
    private int GetSwarmTopOrbCount(long matchingId)
    {
        int top = 0;
        foreach (var session in GetSessionsByMatch(matchingId))
        {
            if (session.PlayerId.HasValue && !session.IsEliminated)
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
                return OrbData.TryGetColorAndTier(offer.SpawnItemId, out _, out int tier)
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

    /// <summary>
    ///     성장 카드 선택 처리 — 이 GameServer instance가 생성한 human session delegate를 통해
    ///     ordered publication의 authoritative prepare 안에서 호출된다. 실패 시 오퍼는 유지된다.
    /// </summary>
    internal void HandleSwarmGrowthPick(GameClientSession session, long matchingId, int offerId, int cardIndex)
    {
        if (!session.PlayerId.HasValue || matchingId <= 0)
            return;

        long playerId = session.PlayerId.Value;
        int baseCost = 0;
        int surcharge = 0;
        int orbCountBefore = 0;
        SwarmGrowthPickResolution resolution =
            GetSwarmMatchRuntime(matchingId).GrowthOfferCoordinator.TryApplyPick(
                playerId,
                offerId,
                offer =>
                {
                    // 선택 시점 상태 (#226 F 계측): 비용 분해·오브 수는 적용 전 값을 남긴다.
                    (baseCost, surcharge, _, orbCountBefore, _, _, _) =
                        GetSwarmGrowthCostBreakdown(matchingId, playerId);
                    return ApplySwarmGrowthCard(matchingId, playerId, cardIndex, offer, session);
                });
        if (!resolution.OfferMatched)
        {
            session.SendSwarmGrowthResult(offerId, cardIndex, success: false);
            return;
        }

        SwarmGrowthOfferState offer = resolution.Offer;
        bool success = resolution.Applied;
        if (success)
        {
            // N 누적 (#226 C 잔여): 성공한 선택만 — 실패(재검증 탈락)는 비용 곡선을 밀지 않는다.
            int successCountBefore = _summonStoneManager.GetGrowthSuccessCount(matchingId, playerId);
            _summonStoneManager.RecordGrowthSuccess(matchingId, playerId, cardIndex);
            _gameEventLogManager.LogSwarmGrowthSelected(
                matchingId, playerId, isBot: false,
                GetSwarmGrowthCardRole(cardIndex), GetSwarmGrowthCardGrade(cardIndex, offer),
                baseCost, surcharge, offer.GetCost(cardIndex), successCountBefore, orbCountBefore);
        }

        session.SendSwarmGrowthResult(offerId, cardIndex, success);
        session.SendSummonStoneState();
        logger.LogInformation(
            "Swarm growth pick: MatchingId={MatchingId}, PlayerId={PlayerId}, Card={Card}, Cost={Cost}, Success={Success}",
            matchingId, playerId, cardIndex, offer.GetCost(cardIndex), success);
    }

    /// <summary>
    ///     카드 효과 적용 (사람·봇 공통): 오퍼 시점에 확정된 서술자를 그대로 집행한다.
    ///     비용 차감이 성립할 때만 효과가 나가고, 픽 시점 재검증 실패면 오퍼가 유지된다.
    /// </summary>
    private bool ApplySwarmGrowthCard(
        long matchingId, long playerId, int cardIndex, SwarmGrowthOfferState offer,
        GameClientSession? session)
    {
        // 차감은 고른 카드의 값으로 (#229): 세 카드가 각자 자기 곡선을 탄다.
        int cost = offer.GetCost(cardIndex);
        var inventory = _inGameInventoryManager.GetPlayerInventory(matchingId, playerId);
        switch (cardIndex)
        {
            case SwarmGrowthCardMultiply:
                {
                    // 6/6 포화 (#232 4단계): 소환 불가 — 기존 5회 탭 파괴로 빈칸을 만든 뒤 다시 소환한다.
                    if (inventory.GetAllItems().Count >= Config.SWARM_ORB_CAPACITY)
                        return false;
                    if (!_summonStoneManager.TrySpendStones(matchingId, playerId, cost, out _))
                        return false;
                    // 소환은 T1 그대로: 티어는 오브마다 따로 산다 — 공유 레벨 상속은 퇴역.
                    if (session != null)
                        session.GrantSwarmArenaOrb(offer.SpawnItemId);
                    else
                        inventory.TryAddItemWithCapacity(offer.SpawnItemId, Config.SWARM_ORB_CAPACITY, out _);
                    SendSwarmFamilyLevels(matchingId, playerId, session);
                    return true;
                }
            case SwarmGrowthCardEnhance:
                // 개별 오브 공격 강화 퇴역 (#232 4단계): 강화는 계열 공유 레벨(태양·바람·파도 직접
                // 버튼 → C_TO_G_SWARM_ORB_DECISION)이 맡는다. 오퍼도 EnhanceTargetTier 0으로 나간다.
                return false;
            case SwarmGrowthCardArmor:
                {
                    if (offer.ArmorCount <= 0)
                        return false;
                    var targets = GetSwarmTrailOrbs(matchingId, playerId)
                        .Where(item =>
                            !GetSwarmMatchRuntime(matchingId).TrailCombat.OrbDurabilityBonus.ContainsKey((matchingId, playerId, item.ItemUid)))
                        .Take(offer.ArmorCount)
                        .ToList();
                    if (targets.Count == 0)
                        return false;
                    if (!_summonStoneManager.TrySpendStones(matchingId, playerId, cost, out _))
                        return false;
                    foreach (var target in targets)
                        GetSwarmMatchRuntime(matchingId).TrailCombat.OrbDurabilityBonus[(matchingId, playerId, target.ItemUid)] =
                            SwarmArmorDurabilityBonus;
                    return true;
                }
            default:
                return false;
        }
    }

    /// <summary>
    ///     앞줄 오브의 HP — 오브별 체력바 브로드캐스트용. 오브 HP 전투가 퇴역해 서버는 HP를 깎지 않으므로
    ///     항상 만충을 보낸다. 빈손은 -1.
    /// </summary>
    private int GetSwarmFrontOrbHp(long matchingId, long playerId)
    {
        var frontOrb = FindSwarmFrontOrb(matchingId, playerId);
        return frontOrb == null ? -1 : GetSquadOrbMaxHp(GetSquadOrbTier(frontOrb.ItemId));
    }

    private bool HasAnySquadOrb(long matchingId, long playerId)
    {
        return _inGameInventoryManager.GetPlayerInventory(matchingId, playerId)
            .GetAllItems()
            .Any(item => item.Count > 0 && GetSquadOrbTier(item.ItemId) > 0);
    }

    private static int GetSquadOrbTier(int itemId)
    {
        if (OrbData.TryGetColorAndTier(itemId, out _, out int tier))
            return tier;
        return OrbData.TryGetRecoveryTier(itemId, out int recoveryTier) ? recoveryTier : 0;
    }

    /// <summary>
    ///     PvP 미사일 적용 (#226 재개편): 오브 HP·본체 보호 퇴역 — 모든 발은 본체 오염으로
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
        bool aggregateEventFeedback = false,
        bool sendAttackerFeedback = true)
    {
        // 반격 보호 (#227 7단계): 방금 이 표적의 꼬리를 자른 공격자의 본체 피해는 통과하지 못한다.
        // 착탄 시점에 보므로 창이 열리기 '전에' 발사된 대기 투사체도 함께 걸린다.
        // 제3자·잔상·폐쇄는 이 경로를 타지 않아 종전대로 들어간다.
        var nowUtc = DateTime.UtcNow;
        if (GetSwarmMatchRuntime(matchingId).TrailCombat.CutRetaliationWindows.TryGetValue(
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
            GetSwarmMatchRuntime(matchingId).BotTactics.LastDamagedAtUtc[(matchingId, bot.PlayerId)] = DateTime.UtcNow;
            bot.LastDamagedAtUtc = DateTime.UtcNow;
            if (corruption > 0)
            {
                // 킬 크레딧 (#226 F 계측): 봇 표적도 사람 표적과 같은 피격 로그를 남긴다 —
                // 이게 빠지면 사람이 봇을 잡아도 killCount·totalDamageDealt가 0으로 남는다.
                _gameEventLogManager.LogHit(
                    matchingId, attack.AttackerPlayerId, bot.PlayerId, attack.WeaponItemId,
                    corruption,
                    bot.Corruption < Config.MAX_CORRUPTION &&
                    bot.Corruption + corruption >= Config.MAX_CORRUPTION,
                    BotPlayerManager.IsBotPlayerId(attack.AttackerPlayerId), DateTimeOffset.UtcNow);
                bot.Corruption = Math.Min(Config.MAX_CORRUPTION, bot.Corruption + corruption);
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
            BroadcastSwarmAttackVfxToTargetAndObservers(attack, allSessions);
        return corruption;
    }


    private void CleanupSwarmArenaState(long matchingId)
    {
        try
        {
            _swarmMonsterDirector.RemoveMatching(matchingId);
        }
        finally
        {
            // director cleanup이 실패해도 Crossfire를 포함한 match-owned aggregate는 남기지 않는다.
            _swarmMatchRuntimes.Remove(matchingId);
        }
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
            // #229 6단계: 수면 중에는 자동 공격이 멈춘다 — 누워서 쏘면 회복이 순수 이득이 된다.
            if (session.IsSleeping)
                continue;

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

        foreach (var target in _swarmMonsterDirector.GetCombatTargets(matchingId))
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
        var inventory = _inGameInventoryManager.GetPlayerInventory(matchingId, spatial.PlayerId);
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
        AddInventoryCombatActors(
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
        var actorTiers = GetSwarmOrbTiersInOrder(matchingId, spatial.PlayerId);
        int orbCount = actors.Count - before;
        long nowUnixMs = (long)(nowUtc - DateTime.UnixEpoch).TotalMilliseconds;
        for (int index = before; index < actors.Count; index++)
        {
            var actor = actors[index];
            // 오브열 (#226 α+): 공격 원점·피격 위치 = 각 오브의 열 좌표 — 표시가 곧 판정.
            // 오브마다 제 자리에서 가장 가까운 몹을 고르고, 예고선은 그 오브에서 나간다.
            var trailPosition = GetSwarmOrbTrailPosition(
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
            bool crossfireSun = IsSwarmCrossfireSun(actor.WeaponItemId);
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
            observer.Send(packet);
        }
    }

    private void SpawnSwarmSummonStone(
        long matchingId,
        MonsterRuntimeInfo defeatedWave,
        IReadOnlyCollection<GameClientSession> sessions,
        int heartReward = 0,
        int bootsReward = 0,
        int keyReward = 0)
    {
        if (defeatedWave.SummonStoneReward <= 0 && heartReward <= 0 &&
            bootsReward <= 0 && keyReward <= 0)
            return;

        // #229 5단계: 스웜에서는 이동속도(부츠)·열쇠를 떨구지 않는다. 기동력은 바람 오브가
        // 맡고, 폐쇄 문은 시간이 여닫는 것이라 열쇠로 뚫는 예외가 없다 — 몹이 떨구면 바닥에
        // 쓰지 못하는 아이템만 쌓인다.
        // 하트는 떨군다(회복이 수면밖에 없다는 결정): 상자 탐색을 끈 뒤로 즉시 회복 공급처가 통째로 사라지므로,
        // 일반 몹 3% 드롭을 이 게이트가 스폰 직전에 지우면 안 된다.
        if (Config.IsSwarmExploreDisabled())
        {
            bootsReward = 0;
            keyReward = 0;
        }

        // 소환석은 바닥에 떨어진다 (즉시 귀속 철회): 처치자도 다른 플레이어와 같은 픽업 경쟁 규칙으로 줍는다.
        // 클라는 재화를 자석 반경에서 몸으로 끌어와 픽업을 요청하므로 동선 부담은 작다.
        int groundStoneReward = defeatedWave.SummonStoneReward;

        if (groundStoneReward <= 0 && heartReward <= 0 &&
            bootsReward <= 0 && keyReward <= 0)
            return;

        // 하트·부츠·열쇠 (#222 M4): 소환석과 함께 흩어진다 — 픽업 경쟁 규칙 공유.
        // 잼 낙수는 잼 승점 퇴역과 함께 제거 (#226 D).
        var itemIds = Enumerable.Repeat(
                Config.SUMMON_STONE_GROUND_ITEM_ID, Math.Max(0, groundStoneReward))
            .Concat(Enumerable.Repeat(Config.HEART_GROUND_ITEM_ID, Math.Max(0, heartReward)))
            .Concat(Enumerable.Repeat(Config.BOOTS_GROUND_ITEM_ID, Math.Max(0, bootsReward)))
            .Concat(Enumerable.Repeat(Config.KEY_GROUND_ITEM_ID, Math.Max(0, keyReward)))
            .ToArray();
        var spawned = _groundItemManager.SpawnItems(
            matchingId,
            defeatedWave.AreaType,
            defeatedWave.PositionX,
            defeatedWave.PositionY,
            itemIds,
            mapId: Config.SWARM_MATCH_MAP,
            layout: GroundItemSpawnLayout.EliminationScatter);

        foreach (var item in spawned)
        {
            _gameEventLogManager.LogGroundItemSpawned(
                matchingId,
                0,
                item.GroundItemUid,
                item.ItemId,
                defeatedWave.AreaType.ToString(),
                0,
                isBot: false);
        }

        // 드랍 개수가 늘어도 버퍼(2048)를 넘지 않게 청크로 나눠 보낸다 (#222).
        int remaining = _areaItemStockManager.GetRemainingCount(matchingId, (int)defeatedWave.AreaType);
        const int chunkSize = 8;
        for (int offset = 0; offset < spawned.Count; offset += chunkSize)
        {
            var chunk = spawned.Skip(offset).Take(chunkSize).ToList();
            using var packet = PacketMaker.G_TO_C_GROUND_ITEM_SPAWN(
                (int)defeatedWave.AreaType,
                remaining,
                chunk);
            foreach (var session in sessions.Where(session => session.CurrentArea == defeatedWave.AreaType))
                session.Send(packet);
        }
    }
}
