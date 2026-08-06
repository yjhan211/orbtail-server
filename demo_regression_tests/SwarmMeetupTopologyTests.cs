using game_server.services;
using network.common;
using network.common.data;
using network.common.data.helpers;

namespace demo_regression_tests;

// #217 3쌍 조우 토폴로지: 스폰 방 → 조우 지점 도달성과 잠긴 문 우회를 고정한다.
public class SwarmMeetupTopologyTests
{
    public SwarmMeetupTopologyTests()
    {
        GameDataHelper.SetBasePath(FindNetworkBasePath());
        GameDataHelper.Initialize();
    }

    [Fact]
    public void SpawnRooms_ReachTheirMeetingAreas()
    {
        AssertReachable(AreaType.ExamRoom, AreaType.Library);
        AssertReachable(AreaType.Storage, AreaType.Library);
        AssertReachable(AreaType.Classroom2, AreaType.Gym);
        AssertReachable(AreaType.Storage2, AreaType.Gym);
        AssertReachable(AreaType.AdminOffice, AreaType.Corridor);
        AssertReachable(AreaType.StaffRoom, AreaType.Corridor);
    }

    [Fact]
    public void LockedDoors_ForceDetourThroughMeetingAreas()
    {
        // 서쪽창고 → 운동장: 잠긴 112(창고↔운동장) 직행 대신 도서관 경유
        var path = FindPath(AreaType.Storage, AreaType.Ground);
        Assert.NotNull(path);
        Assert.Contains(path!, step => step.Area == AreaType.Library);

        // 동쪽창고 → 복도: 잠긴 114 대신 강당 경유
        var viaGym = FindPath(AreaType.Storage2, AreaType.Corridor);
        Assert.NotNull(viaGym);
        Assert.Contains(viaGym!, step => step.Area == AreaType.Gym);
    }

    [Fact]
    public void Junkyards_AreUnreachable()
    {
        Assert.Null(FindPath(AreaType.AdminOffice, AreaType.Junkyard));
        Assert.Null(FindPath(AreaType.StaffRoom, AreaType.Junkyard2));
        Assert.Null(FindPath(AreaType.Corridor, AreaType.Junkyard));
        Assert.Null(FindPath(AreaType.Corridor, AreaType.Junkyard2));
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
