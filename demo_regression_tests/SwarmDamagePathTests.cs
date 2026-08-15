using System.Reflection;
using System.Text.RegularExpressions;
using game_server;
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
    ///     오브 HP 경로(몹 접촉이 앞줄 오브를 깎던 구형 모델)는 꺼진 채로 유지한다.
    ///     이 플래그가 켜지면 원거리·접촉 피해가 다시 오브를 깎아 두 경로가 한 축으로 합쳐진다.
    /// </summary>
    [Fact]
    public void SwarmOrbHealth_StaysDisabled()
    {
        var field = typeof(GameServer).GetField(
            "SwarmOrbHealthEnabled", BindingFlags.NonPublic | BindingFlags.Static);
        Assert.NotNull(field);

        var value = field!.GetValue(null);
        Assert.False(
            (bool)value!,
            "SwarmOrbHealthEnabled가 켜졌다 — 원거리 피해가 전투 오브를 깎으면 " +
            "본체 HP와 절단 내구의 역할 분리가 무너진다 (#227 M2).");
    }

    /// <summary>
    ///     절단 내구(_swarmOrbCutCracks)에 값을 쓰는 곳은 절단 판정 하나뿐이어야 한다.
    ///     제거(Remove)는 파괴·정리 경로라 대상이 아니고, 증가 대입만 센다.
    /// </summary>
    [Fact]
    public void OrbCutDurability_IsWrittenOnlyByTrailCut()
    {
        string source = File.ReadAllText(
            Path.Combine(FindRepositoryRoot(), "game_server", "GameServer.SwarmArena.cs"));

        var writes = Regex.Matches(source, @"_swarmOrbCutCracks\[[^\]]+\]\s*=");
        Assert.True(
            writes.Count == 1,
            $"절단 내구 대입 지점이 {writes.Count}곳이다 — 절단 외의 경로가 오브 내구를 깎으면 " +
            "원거리 공격으로 크랙이 생겨 두 피해 경로가 뒤섞인다 (#227 M2).");

        // 그 한 곳이 실제로 절단 판정 안인지 확인한다.
        int cutMethodStart = source.IndexOf("private void TryPerformSwarmTrailCut(", StringComparison.Ordinal);
        Assert.True(cutMethodStart >= 0, "TryPerformSwarmTrailCut를 찾지 못했다");
        Assert.True(
            writes[0].Index > cutMethodStart,
            "절단 내구 대입이 절단 판정 밖에 있다 (#227 M2).");
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

        // 게이트가 실제로 물려 있어야 한다: 목록 전송·탐색 시작·봇 자동 탐색·소비품 드롭 네 곳.
        foreach (var (file, marker) in new[]
                 {
                     (Path.Combine("game_server", "Network", "GameClientSession.Movement.cs"),
                         "private void SendInteractableList"),
                     (Path.Combine("game_server", "Network", "GameClientSession.RngCollect.cs"),
                         "private Task HandleSwarmRngCollectStart"),
                     (Path.Combine("game_server", "GameServer.SwarmArena.cs"),
                         "private void ProcessSwarmBotExplores"),
                     (Path.Combine("game_server", "GameServer.SpotArena.cs"),
                         "private void SpawnSpotArenaSummonStone")
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
    ///     #229 6단계 수면 회복: 회복을 줍는 운이 아니라 위치 판단으로 바꾸는 규칙이다.
    ///     준비 시간·교전 잠금·중단 조건 중 하나라도 빠지면 "눌러서 피해 무시"가 되어
    ///     완료 조건(교전 중 수면으로 피해를 무시한 사례 0회)이 바로 깨진다.
    /// </summary>
    [Fact]
    public void SwarmSleepRecovery_KeepsWarmupCombatLockAndBreakConditions()
    {
        string root = FindRepositoryRoot();
        string combat = File.ReadAllText(
            Path.Combine(root, "game_server", "Network", "GameClientSession.Combat.cs"));

        // 수치 계약: 1.5초 준비 · 초당 최대 HP 5% · 가해·피해 뒤 3초 진입 잠금.
        Assert.Contains("SwarmSleepWarmupSeconds = 1.5d", combat);
        Assert.Contains("SwarmSleepRecoveryRatioPerSecond = 0.05f", combat);
        Assert.Contains("SwarmSleepCombatLockSeconds = 3d", combat);
        // 소수 이월이 없으면 짧은 아레나 틱에서 회복이 매번 0으로 잘린다.
        Assert.Contains("_swarmSleepRecoveryCarry", combat);

        // 중단 조건 세 갈래가 실제로 물려 있어야 한다.
        string movement = File.ReadAllText(
            Path.Combine(root, "game_server", "Network", "GameClientSession.Movement.cs"));
        Assert.Contains("BreakSwarmSleep(DateTime.UtcNow, markCombat: false)", movement);

        string arena = File.ReadAllText(
            Path.Combine(root, "game_server", "GameServer.SwarmArena.cs"));
        // 피격·가해는 교전 잠금을 함께 찍는다.
        Assert.Contains("BreakSwarmSleep(DateTime.UtcNow, markCombat: true)", arena);
        Assert.Contains("BreakSwarmSleep(nowUtc, markCombat: true)", arena);
        // 폐쇄·경고 구역은 잠금 없이 깨우기만 한다.
        Assert.Contains("sleepBreakAreas", arena);
        Assert.Contains("ProcessSwarmSleepRecovery(aliveSessions, nowUtc)", arena);
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

        // 신고 건: 교실1(Classroom4=40) 폐쇄 목록에 고사실 소속 door 21이 들어와야 한다.
        var classroom1Doors = GameDoorData.GetByAreaType(AreaType.Classroom4).ToList();
        Assert.Contains(classroom1Doors, door => door.DoorId == 21);
        Assert.Contains(classroom1Doors, door => door.DoorId == 22);

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
        // #229: 스폰 방 10곳은 문이 잠긴 채 시작하고, 여는 수단은 탐색 게이지뿐이다.
        // 잠금이 빠지면 방을 탈출하는 목표 자체가 사라진다.
        var spawnRooms = SurvivorRoyaleSpawnData.GetPhaseRoomCandidates();
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
        // 문은 안에서만 연다 (#229): 복도·운동장·쓰레기장 쪽에는 잠금해제 오브젝트가 없다.
        var spawnRooms = SurvivorRoyaleSpawnData.GetPhaseRoomCandidates().ToHashSet();
        var unlockSides = new HashSet<(int DoorId, int Zone)>();
        foreach (var zone in Enum.GetValues<AreaType>())
        {
            foreach (var info in GameInteractableData.GetByZone((int)zone))
            {
                if (info.DoorId <= 0) continue;
                unlockSides.Add((info.DoorId, (int)zone));
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
}
