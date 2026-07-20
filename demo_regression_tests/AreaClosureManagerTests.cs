using game_server.services;
using Microsoft.Extensions.Logging.Abstractions;
using network.common;
using network.common.data;

namespace demo_regression_tests;

public class AreaClosureManagerTests
{
    [Fact]
    public void SpawnAssignments_AreSeededUniqueCorridorAnchorsForTheWholeRoster()
    {
        long[] playerIds = [101, 202, -1, -2, -3, -4, -5, -6];

        var first = SurvivorRoyaleSpawnData.CreateAssignments(195004, playerIds);
        var second = SurvivorRoyaleSpawnData.CreateAssignments(195004, playerIds.Reverse());

        Assert.Equal(8, first.Count);
        Assert.Equal(8, first.Values.Distinct().Count());
        Assert.Equal(
            SurvivorRoyaleSpawnData.GetCorridorAnchors().OrderBy(cell => cell.X).ThenBy(cell => cell.Y),
            first.Values.OrderBy(cell => cell.X).ThenBy(cell => cell.Y));
        foreach (var playerId in playerIds)
        {
            Assert.Equal(first[playerId], second[playerId]);
        }
    }
    [Fact]
    public void InitializeMatching_UsesFixedP0WavesAndNeverClosesCorridor()
    {
        var now = new DateTime(2026, 7, 20, 0, 0, 0, DateTimeKind.Utc);
        var manager = CreateManager(() => now);

        var state = manager.InitializeMatching(195001);

        Assert.Empty(state.ClosedAreas);
        Assert.Equal(5, state.Waves.Count);
        Assert.Equal([105, 165, 215, 255, 290], state.Waves.Select(wave => wave.ClosureAtSeconds));
        Assert.DoesNotContain(state.ClosureOrder, area => area.IsCorridor());
        Assert.Equal(
            [AreaType.ExamRoom, AreaType.BroadcastRoom, AreaType.Classroom2],
            state.Waves[0].Areas);
        Assert.Equal(
            [AreaType.StaffRoom, AreaType.Junkyard2, AreaType.Storage2],
            state.Waves[^1].Areas);
    }

    [Fact]
    public void CheckClosureSchedule_WarnsForEveryAreaThenClosesTheWholeWave()
    {
        var now = new DateTime(2026, 7, 20, 0, 0, 0, DateTimeKind.Utc);
        var manager = CreateManager(() => now);
        const long matchingId = 195002;
        manager.InitializeMatching(matchingId);

        now = now.AddSeconds(90);
        var warning = manager.CheckClosureSchedule(matchingId);

        Assert.Equal(15, warning.WarningSeconds);
        Assert.Equal([AreaType.ExamRoom, AreaType.BroadcastRoom, AreaType.Classroom2], warning.WarningAreas);
        Assert.Empty(warning.ClosedAreas);

        now = now.AddSeconds(15);
        var closure = manager.CheckClosureSchedule(matchingId);

        Assert.Empty(closure.WarningAreas);
        Assert.Equal([AreaType.ExamRoom, AreaType.BroadcastRoom, AreaType.Classroom2], closure.ClosedAreas);
        Assert.True(manager.IsAreaClosed(matchingId, AreaType.ExamRoom));
        Assert.True(manager.IsAreaClosed(matchingId, AreaType.BroadcastRoom));
        Assert.False(manager.IsAreaClosed(matchingId, AreaType.Corridor));
    }

    [Fact]
    public void EnvironmentalDamage_UsesLatestClosedWaveRateAndAddsOvertime()
    {
        var now = new DateTime(2026, 7, 20, 0, 0, 0, DateTimeKind.Utc);
        var manager = CreateManager(() => now);
        const long matchingId = 195003;
        manager.InitializeMatching(matchingId);

        now = now.AddSeconds(105);
        manager.CheckClosureSchedule(matchingId);
        Assert.Equal(10, manager.GetEnvironmentalCorruptionDelta(matchingId, AreaType.ExamRoom));
        Assert.Equal(0, manager.GetEnvironmentalCorruptionDelta(matchingId, AreaType.Ground));

        now = now.AddSeconds(185); // 4:50: remaining waves close together after a delayed timer tick.
        manager.CheckClosureSchedule(matchingId);
        Assert.Equal(35, manager.GetEnvironmentalCorruptionDelta(matchingId, AreaType.ExamRoom));
        Assert.Equal(5, manager.GetEnvironmentalCorruptionDelta(matchingId, AreaType.Ground));

        now = now.AddSeconds(30);
        Assert.Equal(40, manager.GetEnvironmentalCorruptionDelta(matchingId, AreaType.ExamRoom));
        Assert.Equal(10, manager.GetEnvironmentalCorruptionDelta(matchingId, AreaType.Ground));
    }

    private static AreaClosureManager CreateManager(Func<DateTime> utcNow)
    {
        return new AreaClosureManager(
            NullLogger.Instance,
            new MatchingConfigService(null!, NullLogger.Instance),
            utcNow);
    }
}