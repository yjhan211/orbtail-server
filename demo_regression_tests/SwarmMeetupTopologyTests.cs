using game_server.players.bots;
using network.common;
using network.common.data;
using network.common.data.helpers;

namespace demo_regression_tests;

// #272 School2 위상: 시작방 8 → 복도 → 합류 4 → 1차 통로 → 운동장 동심원.
// 스폰 후보 전원이 운동장에 닿고, 운동장에서 어느 방으로든 되돌아갈 수 있어야 한다.
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
        foreach (AreaType pod in MatchSpawnData.GetPhaseRoomCandidates())
            AssertReachable(pod, AreaType.S2Corridor9);
    }

    [Fact]
    public void GroundPlaza_ReachesEveryPodAndBothBands()
    {
        // #272 가운데 병합: 밴드·테라스가 운동장(S2Corridor9)과 한 구역이라 별도 왕복 검증이 없다.
        foreach (AreaType pod in MatchSpawnData.GetPhaseRoomCandidates())
            AssertReachable(AreaType.S2Corridor9, pod);
    }

    [Fact]
    public void DiagonalPods_ReachEachOtherAcrossTheMap()
    {
        // 대각 횡단: 교실1(북서)↔행정실2(남동), 교실2(북동)↔창고(남서)
        AssertReachable(AreaType.S2Classroom1, AreaType.S2AdminOffice2);
        AssertReachable(AreaType.S2Classroom2, AreaType.S2Storage);
    }

    private static void AssertReachable(AreaType from, AreaType to)
    {
        var path = FindPath(from, to);
        Assert.True(path is { Count: > 0 }, $"{from} → {to} 경로 없음");
    }

    private static List<MapPathfinder.Step>? FindPath(AreaType from, AreaType to)
    {
        return MapPathfinder.FindPath(
            Config.SWARM_MATCH_MAP, from,
            GameMapData.GetAreaSpawnCell(Config.SWARM_MATCH_MAP, from),
            to, GameMapData.GetAreaSpawnCell(Config.SWARM_MATCH_MAP, to));
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
