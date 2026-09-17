using network.common;
using network.common.data;
using network.common.data.helpers;

namespace server_tests;

// #272 자기장(원형): 운동장 중심 유클리드 필드의 기본 성질을 고정한다 —
// 중심은 운동장 안, 운동장 거리 0, 원이 방보다 복도 밴드를 늦게 먹는 깔때기.
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
        Assert.Equal(0, SwarmPressureField.GetAreaMinDistance(AreaType.S2Corridor9));
        Assert.InRange(SwarmPressureField.MaxDistance, 10, 1000);
    }

    // #272 원형 전환: 모든 구역이 거리 필드에 있어야 파생 폐쇄 시간표가 전 구역을 덮는다.
    [Fact]
    public void AllMapAreas_HaveFieldDistances()
    {
        var mapAreas = GameMapData.GetAreas(Config.SWARM_MATCH_MAP)
            .Select(region => region.AreaType)
            .Distinct();
        foreach (var area in mapAreas)
        {
            Assert.True(SwarmPressureField.GetAreaMinDistance(area) < int.MaxValue,
                $"{area} 거리 없음 — 구역 rect 안에 이동 가능 셀이 없는지 확인");
        }
    }

    [Fact]
    public void CenterCell_IsInsideGround()
    {
        var (centerX, centerY) = SwarmPressureField.CenterCell;
        var centerArea = GameMapData.GetCurrentArea(
            Config.SWARM_MATCH_MAP,
            new network.common.data.models.Cell(
                (int)Math.Round(centerX), (int)Math.Round(centerY)));
        Assert.Equal(AreaType.S2Corridor9, centerArea);
    }

    [Fact]
    public void CircleFunnel_RoomsFartherThanCorridor()
    {
        // 원은 바깥 시작방을 먼저 먹고 운동장을 감싼 랩 밴드(1차 통로)를 마지막에 먹는다.
        foreach (var room in MatchSpawnData.GetPhaseRoomCandidates())
        {
            AssertFarther(room, AreaType.S2Corridor9);
        }
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
