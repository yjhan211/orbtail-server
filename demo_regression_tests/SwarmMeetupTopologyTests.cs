using game_server.services;
using network.common;
using network.common.data;
using network.common.data.helpers;

namespace demo_regression_tests;

// #219 SB 클론 위상: 중앙 광장을 포드 10개와 상하 회랑 밴드가 포위한다.
// 스폰 후보 전원이 광장에 닿고, 광장에서 어느 방으로든 되돌아갈 수 있어야 한다.
public class SwarmMeetupTopologyTests
{
    public SwarmMeetupTopologyTests()
    {
        GameDataHelper.SetBasePath(FindNetworkBasePath());
        GameDataHelper.Initialize();
    }

    [Fact]
    public void EveryPod_ReachesTheGroundPlaza()
    {
        foreach (AreaType pod in SurvivorRoyaleSpawnData.GetPhaseRoomCandidates())
            AssertReachable(pod, AreaType.Ground);
    }

    [Fact]
    public void GroundPlaza_ReachesEveryPodAndBothBands()
    {
        foreach (AreaType pod in SurvivorRoyaleSpawnData.GetPhaseRoomCandidates())
            AssertReachable(AreaType.Ground, pod);

        AssertReachable(AreaType.Ground, AreaType.Junkyard);
        AssertReachable(AreaType.Ground, AreaType.Corridor);
    }

    [Fact]
    public void DiagonalPods_ReachEachOtherAcrossTheMap()
    {
        // 대각 횡단: 교실1(남서)↔교무실(북동), 행정실(북서)↔방송실(남동)
        AssertReachable(AreaType.Classroom4, AreaType.StaffRoom);
        AssertReachable(AreaType.AdminOffice, AreaType.BroadcastRoom);
    }

    private static void AssertReachable(AreaType from, AreaType to)
    {
        var path = FindPath(from, to);
        Assert.True(path is { Count: > 0 }, $"{from} → {to} 경로 없음");
    }

    private static List<BotPathfinder.Step>? FindPath(AreaType from, AreaType to)
    {
        return BotPathfinder.FindPath(
            MapId.School, from, GameMapData.GetAreaSpawnCell(MapId.School, from),
            to, GameMapData.GetAreaSpawnCell(MapId.School, to));
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
