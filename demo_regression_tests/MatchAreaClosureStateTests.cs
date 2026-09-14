using game_server.matches;
using network.common;
using network.common.data;
using network.common.data.helpers;

namespace demo_regression_tests;

public sealed class MatchAreaClosureStateTests
{
    public MatchAreaClosureStateTests()
    {
        GameDataHelper.SetBasePath(FindNetworkBasePath());
        GameDataHelper.Initialize();
    }

    [Fact]
    public void ClosureSchedule_ClosesAreasInOrderAtScheduledSeconds()
    {
        var startedAt = new DateTime(2030, 1, 2, 3, 4, 5, DateTimeKind.Utc);
        DateTime now = startedAt;
        var closures = MatchTestServices.Closures(() => now);
        AreaType[] areas = GameMapData.GetAreas(Config.SWARM_MATCH_MAP)
            .Select(region => region.AreaType)
            .Where(area => area != AreaType.None)
            .Distinct()
            .Take(2)
            .ToArray();
        Assert.Equal(2, areas.Length);

        var schedule = new[] { (areas[0], 20), (areas[1], 40) };
        Assert.True(closures.InitializeMatching(schedule));
        Assert.False(closures.InitializeMatching(schedule));
        Assert.False(closures.IsAreaClosed(areas[0]));

        now = startedAt.AddSeconds(19);
        Assert.Empty(closures.CloseDueAreas());
        Assert.False(closures.IsAreaClosed(areas[0]));

        now = startedAt.AddSeconds(20);
        Assert.Equal([areas[0]], closures.CloseDueAreas());
        Assert.True(closures.IsAreaClosed(areas[0]));
        Assert.False(closures.IsAreaClosed(areas[1]));
        Assert.Empty(closures.CloseDueAreas());

        now = startedAt.AddSeconds(40);
        Assert.Equal([areas[1]], closures.CloseDueAreas());
        Assert.True(closures.IsAreaClosed(areas[1]));

        closures.Release();
        Assert.Null(closures.GameStartTime);
    }

    [Fact]
    public void UnsafeAreaUsesClosureStateAndInjectedClock()
    {
        var now = new DateTime(2030, 1, 2, 3, 4, 5, DateTimeKind.Utc);
        var closures = new MatchAreaClosureState(() => now);
        var area = GameMapData.GetAreas(Config.SWARM_MATCH_MAP)
            .Select(region => region.AreaType)
            .First(candidate => candidate != AreaType.None && SwarmPressureField.GetAreaMinDistance(candidate) > 0);
        Assert.False(closures.IsAreaUnsafe(area));
        closures.InitializeMatching([]);
        Assert.False(closures.IsAreaUnsafe(area));
        now = now.AddHours(1);
        Assert.False(closures.IsAreaClosed(area));
        Assert.True(closures.IsAreaUnsafe(area));
        closures.Release();
        Assert.False(closures.IsAreaUnsafe(area));
        closures.InitializeMatching([(area, 0)]);
        closures.CloseDueAreas();
        Assert.True(closures.IsAreaUnsafe(area));
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
