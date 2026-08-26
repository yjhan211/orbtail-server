using network.common;
using network.common.data;
using network.common.data.helpers;

namespace demo_regression_tests;

// #217 자기장: 보행 거리 필드가 깔때기 순서(시작방 > 쌍 구역 > 운동장)를 재현하는지 고정한다.
public class SwarmPressureFieldTests
{
    public SwarmPressureFieldTests()
    {
        GameDataHelper.SetBasePath(FindNetworkBasePath());
        GameDataHelper.Initialize();
    }

    [Fact]
    public void Ground_IsZeroAndMaxDistanceIsSane()
    {
        Assert.Equal(0, SwarmPressureField.GetAreaMinDistance(AreaType.Ground));
        Assert.InRange(SwarmPressureField.MaxDistance, 10, 1000);
    }

    // #272 클론 맵 재작성: 옛 맵 3쌍 조우 토폴로지 전제 테스트(시작방>쌍구역, 잠긴 뒷문)를
    // 퇴역시키고, 클론 맵의 실제 깔때기(방 > 복도·도서관·강당 밴드 > 운동장)를 잠근다.
    // 문은 전부 게이지로 여는 통로라 벽 취급하지 않는다 — SwarmPressureField 주석 참조.
    [Fact]
    public void AllMapAreas_AreReachableFromGround()
    {
        var mapAreas = GameMapData.GetAreas(MapId.School)
            .Select(region => region.AreaType)
            .Distinct();
        foreach (var area in mapAreas)
        {
            Assert.True(SwarmPressureField.GetAreaMinDistance(area) < int.MaxValue,
                $"{area} 도달 불가 — 문 데이터가 BFS 위상에서 빠졌는지 확인");
        }
    }

    [Fact]
    public void FunnelOrder_RoomsFartherThanInnerBand()
    {
        // 방은 복도·도서관·강당(운동장 인접 밴드)보다 멀다 — 수축이 방부터 스친다.
        foreach (var room in new[]
                 {
                     AreaType.Classroom2, AreaType.Classroom3, AreaType.Classroom4,
                     AreaType.Storage2, AreaType.ExamRoom, AreaType.BroadcastRoom,
                     AreaType.AdminOffice, AreaType.StaffRoom
                 })
        {
            AssertFarther(room, AreaType.Corridor);
            AssertFarther(room, AreaType.Library);
            AssertFarther(room, AreaType.Gym);
        }
    }

    [Fact]
    public void JunkyardPodRooms_AreTheFarthest()
    {
        // 행정실·교무실은 정크장 경유라 출구가 가장 멀다 (#229의 수동 조정 근거를 필드가 재현).
        AssertFarther(AreaType.AdminOffice, AreaType.Junkyard);
        AssertFarther(AreaType.StaffRoom, AreaType.Junkyard);
        AssertFarther(AreaType.Junkyard, AreaType.Corridor);
    }

    private static void AssertFarther(AreaType outer, AreaType inner)
    {
        int outerDistance = SwarmPressureField.GetAreaMinDistance(outer);
        int innerDistance = SwarmPressureField.GetAreaMinDistance(inner);
        Assert.True(outerDistance > innerDistance,
            $"{outer}({outerDistance}) 가 {inner}({innerDistance}) 보다 멀어야 한다");
        Assert.True(outerDistance < int.MaxValue, $"{outer} 도달 불가");
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

        throw new DirectoryNotFoundException("Could not locate network/Common/csv.");
    }
}
