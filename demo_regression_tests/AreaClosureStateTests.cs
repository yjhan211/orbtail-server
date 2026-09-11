using game_server.matches;
using network.common;
using network.common.data;
using network.common.data.helpers;

namespace demo_regression_tests;

public sealed class AreaClosureStateTests
{
    public AreaClosureStateTests()
    {
        GameDataHelper.SetBasePath(FindNetworkBasePath());
        GameDataHelper.Initialize();
    }

    [Fact]
    public void ClosureSchedule_WarnsFifteenSecondsAheadThenClosesInOrder()
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

        var waves = new[]
        {
            new ClosureWaveDefinition(20, [areas[0]], 0),
            new ClosureWaveDefinition(40, [areas[1]], 0)
        };
        var state = closures.InitializeMatching(wavesOverride: waves);
        Assert.Same(state, closures.InitializeMatching(wavesOverride: waves));
        Assert.Equal(2, state.Waves.Count);

        var initial = closures.GetClientStateSnapshot();
        Assert.Empty(initial.ClosedAreas);
        Assert.Empty(initial.WarningAreas);
        Assert.Equal(5, initial.NextWarningSeconds);

        now = startedAt.AddSeconds(5);
        var firstWarningTick = closures.CheckClosureSchedule();
        Assert.Equal([areas[0]], firstWarningTick.WarningAreas);
        Assert.Equal(15, firstWarningTick.WarningSeconds);
        Assert.Empty(closures.CheckClosureSchedule().WarningAreas);

        var firstWarning = closures.GetClientStateSnapshot();
        Assert.Equal([areas[0]], firstWarning.WarningAreas);
        Assert.Equal(15, firstWarning.WarningSeconds);
        Assert.Equal(new DateTimeOffset(startedAt.AddSeconds(20)).ToUnixTimeMilliseconds(), firstWarning.ClosureAtUnixMs);

        now = startedAt.AddSeconds(20);
        var firstClosureTick = closures.CheckClosureSchedule();
        Assert.Equal([areas[0]], firstClosureTick.ClosedAreas);
        Assert.True(closures.IsAreaClosed(areas[0]));
        Assert.False(closures.IsAreaClosed(areas[1]));

        var afterFirstClosure = closures.GetClientStateSnapshot();
        Assert.Equal([areas[0]], afterFirstClosure.ClosedAreas);
        Assert.Empty(afterFirstClosure.WarningAreas);
        Assert.Equal(5, afterFirstClosure.NextWarningSeconds);

        now = startedAt.AddSeconds(25);
        Assert.Equal([areas[1]], closures.CheckClosureSchedule().WarningAreas);
        var secondWarning = closures.GetClientStateSnapshot();
        Assert.Equal([areas[1]], secondWarning.WarningAreas);
        Assert.Equal(15, secondWarning.WarningSeconds);

        now = startedAt.AddSeconds(40);
        Assert.Equal([areas[1]], closures.CheckClosureSchedule().ClosedAreas);
        var completed = closures.GetClientStateSnapshot();
        Assert.Equal(areas.OrderBy(area => (int)area), completed.ClosedAreas);
        Assert.Empty(completed.WarningAreas);
        Assert.Equal(0, completed.WarningSeconds);

        closures.Release();
        Assert.Null(closures.GetMatchingState());
        Assert.Throws<InvalidOperationException>(() => closures.InitializeMatching(wavesOverride: waves));
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
