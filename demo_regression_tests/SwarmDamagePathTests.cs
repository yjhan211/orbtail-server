using network.common;
using network.common.data;
using network.common.data.helpers;

namespace demo_regression_tests;

// 문·폐쇄 데이터 계약: 문은 양쪽 구역에서 잠기고, 시작방 문은 게이지로만 열리며 해제 오브젝트는 방 안쪽에만 있다.
public class SwarmDamagePathTests
{
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
}
