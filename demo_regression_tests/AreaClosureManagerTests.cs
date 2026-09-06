using game_server.services;
using Microsoft.Extensions.Logging.Abstractions;
using network.common;
using network.common.data;
using network.common.data.helpers;

namespace demo_regression_tests;

public sealed class AreaClosureManagerTests
{
    public AreaClosureManagerTests()
    {
        GameDataHelper.SetBasePath(FindNetworkBasePath());
        GameDataHelper.Initialize();
    }

    [Fact]
    public void ClosureSnapshot_TracksSequenceCountdownWarningAndClosedTransitions()
    {
        const long matchingId = 219001;
        var startedAt = new DateTime(2030, 1, 2, 3, 4, 5, DateTimeKind.Utc);
        DateTime now = startedAt;
        var manager = MatchTestServices.Closures(NullLogger.Instance, () => now);
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
        manager.InitializeMatching(matchingId, wavesOverride: waves);

        var initial = manager.GetClosureSnapshot(matchingId);
        Assert.Equal(areas.Select(area => (int)area), initial.closureSequence);
        Assert.Empty(initial.closedAreaIds);
        Assert.Equal((int)areas[0], initial.nextAreaType);
        Assert.Equal(new DateTimeOffset(startedAt.AddSeconds(20)).ToUnixTimeSeconds(), initial.nextAtUnix);
        Assert.Equal(20, initial.secondsLeft);
        Assert.False(initial.warningActive);

        now = startedAt.AddSeconds(5);
        var firstWarningTick = manager.CheckClosureSchedule(matchingId);
        Assert.Equal([areas[0]], firstWarningTick.WarningAreas);
        Assert.Equal(15, firstWarningTick.WarningSeconds);

        var firstWarning = manager.GetClosureSnapshot(matchingId);
        Assert.Equal(15, firstWarning.secondsLeft);
        Assert.True(firstWarning.warningActive);

        now = startedAt.AddSeconds(20);
        var firstClosureTick = manager.CheckClosureSchedule(matchingId);
        Assert.Equal([areas[0]], firstClosureTick.ClosedAreas);

        var afterFirstClosure = manager.GetClosureSnapshot(matchingId);
        Assert.Equal([(int)areas[0]], afterFirstClosure.closedAreaIds);
        Assert.Equal((int)areas[1], afterFirstClosure.nextAreaType);
        Assert.Equal(new DateTimeOffset(startedAt.AddSeconds(40)).ToUnixTimeSeconds(), afterFirstClosure.nextAtUnix);
        Assert.Equal(20, afterFirstClosure.secondsLeft);
        Assert.False(afterFirstClosure.warningActive);

        now = startedAt.AddSeconds(25);
        manager.CheckClosureSchedule(matchingId);
        var secondWarning = manager.GetClosureSnapshot(matchingId);
        Assert.Equal(15, secondWarning.secondsLeft);
        Assert.True(secondWarning.warningActive);

        now = startedAt.AddSeconds(40);
        var finalClosureTick = manager.CheckClosureSchedule(matchingId);
        Assert.Equal([areas[1]], finalClosureTick.ClosedAreas);

        var completed = manager.GetClosureSnapshot(matchingId);
        Assert.Equal(
            areas.Select(area => (int)area).OrderBy(area => area),
            completed.closedAreaIds.OrderBy(area => area));
        Assert.Equal(-1, completed.nextAreaType);
        Assert.Equal(-1, completed.nextAtUnix);
        Assert.Equal(-1, completed.secondsLeft);
        Assert.False(completed.warningActive);
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
