using game_server.players.bots;
using game_server.combat;
using game_server.matches.results;
using game_server.players;
using game_server.matches;
using System.Text.RegularExpressions;
using game_server;
using game_server.sessions;
using network.common;
using network.common.data;
using network.common.data.helpers;

namespace demo_regression_tests;

// #227 M2 피해 경로 고정: 두 공격이 서로 다른 것을 깎아야 "죽이러 갈지 / 무장 해제하러 갈지"가
// 선택이 된다. 원거리(미사일·물폭탄·잔상·폐쇄)는 본체 HP만, 전투 오브 내구는 몸으로 가로지르는
// 절단만 깎는다. 이 경계는 코드 몇 줄로 조용히 무너질 수 있어 여기서 잠근다.
public class SwarmDamagePathTests
{
    /// <summary>
    ///     고위험 절단 계약 (#232, 2026-08-18 유저 결정 "오브 절단면 다 깨지게"): 크랙 5칸·방어 장갑은 퇴역 —
    ///     절단 내구에 값을 쓰는 곳이 하나라도 남으면 "유효 교차 한 번 = 즉시 절단"이 무너진다.
    ///     절단은 켜져 있고, 한 교차는 밟은 지점부터 꼬리 끝까지 지우며(스네이크 접미), 낙수를 흩지 않고,
    ///     공격자는 +35 선결 검사 뒤 같은 사건으로 치명상과 8초 회복 차단을 받는다.
    /// </summary>
    [Fact]
    public void TailCut_RemovesSuffixAndChargesAttacker()
    {
        string source = File.ReadAllText(
            Path.Combine(FindRepositoryRoot(), "game_server", "Combat", "MatchCombatService.cs"));

        // 2026-09-02 재무장 — 절단은 켜져 있어야 한다 (끌 때는 이 어서션도 같이 바꾼다).
        Assert.Contains("SwarmTrailCutEnabled = true", source);
        Assert.DoesNotContain("OrbCutCracks", source);

        int cutMethodStart = source.IndexOf("private void TryPerformSwarmTrailCut(", StringComparison.Ordinal);
        Assert.True(cutMethodStart >= 0, "TryPerformSwarmTrailCut를 찾지 못했다");
        int cutMethodEnd = source.IndexOf("// 링 연출 종류", cutMethodStart, StringComparison.Ordinal);
        Assert.True(cutMethodEnd > cutMethodStart, "절단 판정 메서드의 끝을 찾지 못했다");
        string cutBody = source.Substring(cutMethodStart, cutMethodEnd - cutMethodStart);

        // 한 교차 = 밟은 순번부터 꼬리 끝까지. 낙수 흩기는 절단 경로에 없어야 한다.
        Assert.Contains("DestroySwarmOrbsFromOrdinal(matchingId, bestOwnerId, bestTailOrdinal)", cutBody);
        Assert.DoesNotContain("DestroySwarmOrbAtOrdinal", source);
        Assert.DoesNotContain("ScatterSwarmOrbBreakStones", cutBody);
        // 공격자 비용: 선결 검사 → 치명상 → 회복 차단이 같은 사건 안에 있다.
        // 값의 원천은 swarm_config.csv(#335) — CSV 행과 코드 폴백 기본값을 함께 잠근다.
        Assert.Contains("SwarmConfigData.GetInt(\"SWARM_SINGLE_CUT_HEALTH_COST\", 35)", source);
        Assert.Contains("SwarmConfigData.GetDouble(\"SWARM_SINGLE_CUT_HEAL_LOCK_SECONDS\", 8d)", source);
        Assert.Equal("35", ReadSwarmConfigValue("SWARM_SINGLE_CUT_HEALTH_COST"));
        Assert.Equal("8", ReadSwarmConfigValue("SWARM_SINGLE_CUT_HEAL_LOCK_SECONDS"));
        Assert.Contains("ORB_SINGLE_CUT_REFUSED", cutBody);
        Assert.Contains("BlockHealingUntil(healLockUntil)", cutBody);
        Assert.Contains("ORB_TAIL_CUT ", cutBody);
        // 0.8초 재접촉 억제 시작값.
        Assert.Contains("SwarmConfigData.GetDouble(\"SWARM_TRAIL_CUT_SAME_ORB_DEBOUNCE_SECONDS\", 0.8d)", source);
        Assert.Equal("0.8", ReadSwarmConfigValue("SWARM_TRAIL_CUT_SAME_ORB_DEBOUNCE_SECONDS"));
    }

    /// <summary>swarm_config.csv의 한 키 값(문자열 그대로). 없으면 실패한다.</summary>
    private static string ReadSwarmConfigValue(string key)
    {
        string csvPath = Path.Combine(FindRepositoryRoot(), "network", "Common", "csv", "swarm_config.csv");
        foreach (string line in File.ReadLines(csvPath))
        {
            string[] parts = line.Split(',');
            if (parts.Length >= 2 && parts[0] == key)
                return parts[1].Trim();
        }

        throw new Xunit.Sdk.XunitException($"swarm_config.csv에 {key} 행이 없다");
    }

    /// <summary>
    ///     #229 5단계: 스웜 탐색·소비품 임시 중단. 데이터·CSV·레거시 코드는 남기고 플래그로만
    ///     끈다 — 이 게이트가 열리면 회복이 다시 랜덤 상자로 새고 부츠가 기동 축을 가져간다.
    /// </summary>
    [Fact]
    public void SwarmExploreAndConsumables_StayDisabled()
    {
        Assert.True(
            Config.IsSwarmExploreDisabled(),
            "스웜 탐색·소비품이 다시 켜졌다 — 회복은 수면이, 기동력은 바람 오브가 맡는다 (#229 5단계).");

        // 게이트가 실제로 물려 있어야 한다: 문 목록 필터와 몬스터 소비품 드롭. 상자 시작·봇 개봉 코드는 제거됐다.
        foreach (var (file, marker) in new[]
                 {
                     (Path.Combine("game_server", "Players", "PlayerMovementService.cs"),
                         "public void SendInteractableList"),
                     (Path.Combine("game_server", "Combat", "MatchCombatDamageService.cs"),
                         "private void SpawnSwarmSummonStone")
                 })
        {
            string source = File.ReadAllText(Path.Combine(FindRepositoryRoot(), file));
            int start = source.IndexOf(marker, StringComparison.Ordinal);
            Assert.True(start >= 0, $"{marker}를 찾지 못했다");
            string body = source.Substring(start, Math.Min(1400, source.Length - start));
            Assert.Contains("Config.IsSwarmExploreDisabled()", body);
        }
    }

    /// <summary>
    ///     봇 교전 튜닝 계약 (2026-08-18 촬영 튜닝, 봇 매치 9126059 → 9133549 → 9134053):
    ///     ① 봇 절단 자제 — 오염 절반 아래 + 6초 쿨다운일 때만 자르고, 사람에게는 걸지 않는다.
    ///     ② 도주 임계 ×1.5 — 동수·소폭 열세는 중립(피하지도 붙지도 않음).
    ///     ③ 치명상 이탈 — 60% 진입 · 45% 해제, 치명상이면 전력 비교 없이 물러난다.
    ///     셋 중 하나라도 조용히 빠지면 봇이 다시 자해로 죽거나(①), 카메라 앞에서 등만 보이거나(②),
    ///     죽을 때까지 맞붙어 후반 판이 빈다(③).
    /// </summary>
    [Fact]
    public void SwarmBotEngagement_KeepsCutRestraintNeutralBandAndWoundedRetreat()
    {
        // #312 분리: 절단 기계는 SwarmArena, 봇 판단(자제·도주·치명상)은 SwarmBots가 소유한다.
        string source = File.ReadAllText(
            Path.Combine(FindRepositoryRoot(), "game_server", "Combat", "MatchCombatService.cs"));
        string botSource = File.ReadAllText(
            Path.Combine(FindRepositoryRoot(), "game_server", "Players", "Bots", "BotDecisionService.cs"));

        // ① 절단 자제: 봇 전용, 래치 앞에서 걸린다.
        Assert.Contains("SwarmBotCutMinHealthRatio = 0.5f", botSource);
        Assert.Contains("SwarmBotCutCooldownSeconds = 6d", botSource);
        int cutMethodStart = source.IndexOf("private void TryPerformSwarmTrailCut(", StringComparison.Ordinal);
        int cutMethodEnd = source.IndexOf("// 링 연출 종류", cutMethodStart, StringComparison.Ordinal);
        string cutBody = source.Substring(cutMethodStart, cutMethodEnd - cutMethodStart);
        Assert.Contains("cutterBot != null && !botDecisions.IsSwarmBotCutAllowed(", cutBody);
        Assert.Contains("BotTactics.LastTrailCutAtUtc[(matchingId, cutterBot.PlayerId)] = nowUtc", cutBody);
        // 사람 절단은 자제 규칙을 타지 않는다 — 봇 분기 안에서만 호출된다.
        Assert.Single(Regex.Matches(cutBody, @"IsSwarmBotCutAllowed\("));

        // ② 도주 임계: 강자 판정과 피격 반응 둘 다 ×1.5를 쓴다.
        Assert.Contains("SwarmBotFleePowerRatio = 1.5f", botSource);
        Assert.Contains("rivalPower >= myPower * SwarmBotFleePowerRatio", botSource);
        Assert.Contains("wounded || attackerPower >= squadPower * SwarmBotFleePowerRatio", botSource);

        // ③ 치명상 이탈: 히스테리시스 + 전력 0으로 스캔.
        Assert.Contains("SwarmBotWoundedEnterRatio = 0.4f", botSource);
        Assert.Contains("SwarmBotWoundedExitRatio = 0.55f", botSource);
        Assert.Contains("wounded ? 0f : squadPower", botSource);
    }

    /// <summary>
    ///     수면 계약 (2026-08-17 재조정): 준비 1초 · 1초마다 최대 HP 5% 회복 · 가해·피해 뒤
    ///     3초 진입 잠금. 중단은 이동뿐이다 — 피격·폐쇄가 다시 깨우기 시작하면
    ///     "움직이지 않으면 안 깬다"는 유저 결정이 소리 없이 뒤집힌다.
    /// </summary>
    [Fact]
    public void SwarmSleepRecovery_KeepsWarmupCombatLockAndBreakConditions()
    {
        string root = FindRepositoryRoot();
        string condition = File.ReadAllText(
            Path.Combine(root, "game_server", "Players", "Player.cs"));

        // 수치 계약: 1초 준비 · 1초 틱당 최대 HP 5% · 가해·피해 뒤 3초 진입 잠금.
        Assert.Contains("SwarmSleepWarmupSeconds = 1d", condition);
        Assert.Contains("SwarmSleepRecoveryRatioPerSecond = 0.05f", condition);
        Assert.Contains("SwarmSleepCombatLockSeconds = 3d", condition);
        // 회복은 연속 이월이 아니라 1초 단위 틱으로 센다.
        Assert.Contains("_swarmSleepGrantedTicks", condition);

        // 중단 경로는 이동 하나뿐이다.
        string movement = File.ReadAllText(
            Path.Combine(root, "game_server", "Players", "PlayerMovementService.cs"));
        Assert.Contains("player.TryStopSleep()", movement);

        string combat = File.ReadAllText(
            Path.Combine(root, "game_server", "Combat", "MatchCombatService.cs"));
        // 피격·절단 가해는 수면을 깨지 않고 교전 잠금만 찍는다.
        Assert.Contains("MarkSwarmCombat(DateTime.UtcNow)", combat);
        Assert.Contains("MarkSwarmCombat(nowUtc)", combat);
        // 아레나에서 수면을 깨우는 호출이 되살아나면 계약 위반이다 (폐쇄·경고 깨우기 퇴역).
        Assert.DoesNotContain("BreakSwarmSleep", combat);
        Assert.Contains("ProcessSwarmSleepRecovery(aliveSessions, nowUtc)", combat);
        // 봇 파셜(#312)도 같은 계약을 진다.
        Assert.DoesNotContain("BreakSwarmSleep", File.ReadAllText(
            Path.Combine(root, "game_server", "Players", "Bots", "BotDecisionService.cs")));
    }

    /// <summary>
    ///     #229: 문은 두 구역의 간선인데 door_info.csv에 area_type이 하나뿐이라 폐쇄 잠금이
    ///     소유 구역 한쪽만 봤다 — 교실1(40)이 닫혀도 고사실(32) 소속인 door 21은 안 잠겼다.
    ///     통행 판정은 원래부터 양쪽을 인정하므로 잠금만 반쪽이었다.
    ///     상대편이 먼저 닫히는 문 다섯(16·17·19·21·22)이 같은 원인이었다.
    /// </summary>
    [Fact]
    public void ClosureLock_CoversBothSidesOfEveryDoor()
    {
        GameDataHelper.SetBasePath(FindNetworkBasePath());
        GameDataHelper.Initialize();

        var doors = GameDoorData.GetAll().ToList();
        Assert.NotEmpty(doors);

        // 모든 문이 반대편 구역을 들고 있어야 한다 — 없으면 그 문은 다시 반쪽 잠금이 된다.
        Assert.All(doors, door => Assert.NotEqual(AreaType.None, door.AreaTypeB));
        Assert.All(doors, door => Assert.NotEqual(door.AreaType, door.AreaTypeB));

        // 구 School 문(21·22) 개별 회귀는 #310에서 레거시 행과 함께 제거 —
        // 양쪽 구역 조회 보장은 아래 전수 루프가 잠근다.

        // 어느 문이든 양쪽 구역 각각으로 조회했을 때 잡혀야 한다.
        foreach (var door in doors)
        {
            Assert.Contains(GameDoorData.GetByAreaType(door.AreaType), found => found.DoorId == door.DoorId);
            Assert.Contains(GameDoorData.GetByAreaType(door.AreaTypeB), found => found.DoorId == door.DoorId);
        }
    }

    private static string FindNetworkBasePath()
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);
        while (directory != null)
        {
            string candidate = Path.Combine(directory.FullName, "network", "Common", "csv");
            if (Directory.Exists(candidate))
                return Path.Combine(directory.FullName, "network");
            directory = directory.Parent;
        }

        throw new DirectoryNotFoundException("network/Common/csv 를 찾지 못했다");
    }

    private static string FindRepositoryRoot()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir != null)
        {
            if (Directory.Exists(Path.Combine(dir.FullName, "game_server")))
                return dir.FullName;
            dir = dir.Parent;
        }

        throw new InvalidOperationException("repository root not found");
    }

    [Fact]
    public void GaugeGatedDoors_LockEverySpawnRoomButKeepTheMapConnected()
    {
        // 실행 순서 무관하게 데이터가 있어야 한다 — 단독 실행에서 문 목록이 비어 실패했다 (2026-08-17).
        GameDataHelper.SetBasePath(FindNetworkBasePath());
        GameDataHelper.Initialize();

        // #229 → #272 School2(문 201~208): 시작방 8곳은 문이 잠긴 채 시작하고, 여는 수단은
        // 탐색 게이지뿐이다. 잠금이 빠지면 방을 탈출하는 목표 자체가 사라진다.
        var spawnRooms = MatchSpawnData.GetPhaseRoomCandidates();
        foreach (var room in spawnRooms)
        {
            var doors = GameDoorData.GetByAreaType(room).ToList();
            Assert.NotEmpty(doors);
            Assert.All(doors, door =>
            {
                Assert.False(door.IsInitiallyOpen,
                    $"{room}의 문 {door.DoorId}이 열린 채 시작한다 — 탈출 목표가 사라진다");
                Assert.True(GameInteractableData.IsGaugeGatedDoor(door.DoorId),
                    $"문 {door.DoorId}에 게이지가 없다 — 잠기기만 하고 열 수단이 없다");
            });
        }

        // 게이트 문은 영구 잠금이 아니다 — 봇 경로 그래프에서 잘라내면 방이 통째로 떨어져 나간다.
        Assert.All(GameDoorData.GetAll().Where(door => GameInteractableData.IsGaugeGatedDoor(door.DoorId)),
            door => Assert.Equal(0, door.RequiredItemId));
    }

    [Fact]
    public void EveryGaugeGatedDoor_HasAnUnlockObjectOnItsRoomSideOnly()
    {
        // 문은 안에서만 연다 (#229 → #272 School2): 복도·통로 쪽에는 잠금해제 오브젝트가 없다.
        // School 세대 데이터(문 1~22)는 보존돼 함께 로드되므로, 검사는 현행 매치 맵의
        // 시작방 문들(스폰 방 소속 door_id)로 한정한다.
        var spawnRooms = MatchSpawnData.GetPhaseRoomCandidates().ToHashSet();
        var spawnRoomDoorIds = GameDoorData.GetAll()
            .Where(door => spawnRooms.Contains(door.AreaType) || spawnRooms.Contains(door.AreaTypeB))
            .Select(door => door.DoorId)
            .ToHashSet();
        var unlockSides = new HashSet<(int DoorId, int Zone)>();
        foreach (var zone in Enum.GetValues<AreaType>())
        {
            foreach (var info in GameInteractableData.GetByZone((int)zone))
            {
                if (info.DoorId <= 0) continue;
                unlockSides.Add((info.DoorId, (int)zone));
                if (!spawnRoomDoorIds.Contains(info.DoorId)) continue;
                Assert.Contains((AreaType)zone, spawnRooms);
            }
        }

        foreach (var door in GameDoorData.GetAll())
        {
            if (!GameInteractableData.IsGaugeGatedDoor(door.DoorId)) continue;
            foreach (var side in new[] { door.AreaType, door.AreaTypeB })
            {
                if (!spawnRooms.Contains(side)) continue;
                Assert.Contains((door.DoorId, (int)side), unlockSides);
            }
        }
    }

    [Fact]
    public void MonsterHits_AccumulateSeparatelyFromPvpDamage()
    {
        // #229: 스웜 전투는 전부 몹 상대인데 어떤 카운터에도 안 쌓여 결과가 "처치 0회"였다.
        // 단 PvP 피해와 같은 칸에 넣으면 안 된다 — 그 칸은 동시 탈락 시 생존자를 가르는
        // 기준(MatchEnvironmentService.ResolveEliminationOrder)이라 의미가 섞이면 판정이 바뀐다.
        var manager = TestGameEventLogs.Create();
        const long matchingId = 771001;
        const long playerId = 4242;

        manager.RecordMonsterHit(matchingId, playerId, 12, killed: false);
        manager.RecordMonsterHit(matchingId, playerId, 12, killed: true);
        manager.RecordMonsterHit(matchingId, playerId, 21, killed: true);

        var stats = manager.GetResultStats(matchingId, playerId);
        Assert.Equal(2, stats.MonsterKillCount);
        Assert.Equal(45, stats.MonsterDamageDealt);

        // PvP 칸은 건드리지 않는다.
        Assert.Equal(0, stats.KillCount);
        Assert.Equal(0, stats.TotalDamageDealt);
    }
}
