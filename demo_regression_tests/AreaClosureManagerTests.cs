using game_server.services;
using Microsoft.Extensions.Logging.Abstractions;
using network.common;
using network.common.data;
using network.common.data.helpers;

namespace demo_regression_tests;

public class AreaClosureManagerTests
{
    public AreaClosureManagerTests()
    {
        GameDataHelper.SetBasePath(FindNetworkBasePath());
        GameDataHelper.Initialize();
    }

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
    public void InitializeMatching_UsesFixedP0WavesAndClosesCorridorLast()
    {
        var now = new DateTime(2026, 7, 20, 0, 0, 0, DateTimeKind.Utc);
        var manager = CreateManager(() => now);

        var state = manager.InitializeMatching(195001);

        Assert.Empty(state.ClosedAreas);
        Assert.Equal(6, state.Waves.Count);
        Assert.Equal([105, 165, 215, 255, 290, 320], state.Waves.Select(wave => wave.ClosureAtSeconds));
        Assert.Equal(AreaType.Corridor, state.ClosureOrder[^1]);
        Assert.Equal(
            [AreaType.ExamRoom, AreaType.BroadcastRoom, AreaType.Classroom2],
            state.Waves[0].Areas);
        Assert.Equal(
            [AreaType.Corridor],
            state.Waves[^1].Areas);
    }

    [Fact]
    public void InitializeMatching_WithStartingRoomsClosesCorridorBeforeFirstPhaseTick()
    {
        var now = new DateTime(2026, 8, 4, 0, 0, 0, DateTimeKind.Utc);
        var manager = CreateManager(() => now);
        var startingRooms = SurvivorRoyaleSpawnData.GetPhaseRoomCandidates();

        var state = manager.InitializeMatching(214101, initiallyOpenAreas: startingRooms);
        var clientState = manager.GetClientStateSnapshot(214101);

        Assert.True(state.PhaseDriven);
        Assert.All(startingRooms, area => Assert.DoesNotContain(area, clientState.ClosedAreas));
        Assert.Contains(AreaType.Corridor, clientState.ClosedAreas);
        // #217 3쌍 조우 토폴로지로 보건실·3-2가 시작방이 됐다 — 비시작방 폐쇄 검증은 강당·도서관으로.
        Assert.Contains(AreaType.Gym, clientState.ClosedAreas);
        Assert.Contains(AreaType.Library, clientState.ClosedAreas);
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
        Assert.Equal(20, manager.GetEnvironmentalCorruptionDelta(matchingId, AreaType.ExamRoom));
        Assert.Equal(0, manager.GetEnvironmentalCorruptionDelta(matchingId, AreaType.Ground));

        now = now.AddSeconds(185); // 4:50: all room waves close together after a delayed timer tick.
        manager.CheckClosureSchedule(matchingId);
        Assert.Equal(60, manager.GetEnvironmentalCorruptionDelta(matchingId, AreaType.ExamRoom));
        Assert.Equal(0, manager.GetEnvironmentalCorruptionDelta(matchingId, AreaType.Ground));

        now = now.AddSeconds(30);
        manager.CheckClosureSchedule(matchingId);
        Assert.Equal(80, manager.GetEnvironmentalCorruptionDelta(matchingId, AreaType.ExamRoom));
        Assert.Equal(10, manager.GetEnvironmentalCorruptionDelta(matchingId, AreaType.Ground));
        Assert.Equal(80, manager.GetEnvironmentalCorruptionDelta(matchingId, AreaType.Corridor));
    }

    [Fact]
    public void ClosureAndOvertimeReachTheFiveToSevenMinuteTerminationEnvelope()
    {
        var now = new DateTime(2026, 7, 24, 0, 0, 0, DateTimeKind.Utc);
        var manager = CreateManager(() => now);
        const long matchingId = 198502;
        var state = manager.InitializeMatching(matchingId);

        Assert.Equal(320, state.Waves[^1].ClosureAtSeconds);
        now = now.AddSeconds(320);
        manager.CheckClosureSchedule(matchingId);
        Assert.True(manager.IsAreaClosed(matchingId, AreaType.Corridor));
        Assert.True(manager.IsOvertimeActive(matchingId));
        Assert.Equal((1, 2), manager.GetOvertimeStatus(matchingId));

        now = now.AddSeconds(70);
        Assert.Equal((4, 16), manager.GetOvertimeStatus(matchingId));
    }
    [Fact]
    public void GlobalClosureSchedule_RemainsDisabledWhenCorridorCloses()
    {
        var now = new DateTime(2026, 7, 24, 0, 0, 0, DateTimeKind.Utc);
        var manager = CreateManager(() => now);
        const long matchingId = 202001;
        manager.InitializeMatching(matchingId);

        now = now.AddSeconds(290);
        manager.CheckClosureSchedule(matchingId);

        now = now.AddSeconds(15);
        var warning = manager.CheckClosureSchedule(matchingId);
        Assert.Equal([AreaType.Corridor], warning.WarningAreas);
        Assert.False(manager.CheckGlobalClosureSchedule(matchingId).HasTransition);
        Assert.False(manager.GetGlobalClosureClientState(matchingId).IsKnown);

        now = now.AddSeconds(15);
        var closure = manager.CheckClosureSchedule(matchingId);
        Assert.Equal([AreaType.Corridor], closure.ClosedAreas);
        Assert.True(manager.IsAreaClosed(matchingId, AreaType.Corridor));
        Assert.False(manager.CheckGlobalClosureSchedule(matchingId).HasTransition);
    }

    [Fact]
    public void CleanupMatching_RemovesClosureStateBeforeMatchingIdIsReused()
    {
        var now = new DateTime(2026, 7, 20, 0, 0, 0, DateTimeKind.Utc);
        var manager = CreateManager(() => now);
        const long matchingId = 195004;
        var state = manager.InitializeMatching(matchingId);
        state.ClosedAreas.Add(AreaType.ExamRoom);
        Assert.True(manager.IsAreaClosed(matchingId, AreaType.ExamRoom));

        manager.CleanupMatching(matchingId);

        Assert.False(manager.IsAreaClosed(matchingId, AreaType.ExamRoom));
        Assert.Empty(manager.GetClientStateSnapshot(matchingId).ClosedAreas);
    }

    private static AreaClosureManager CreateManager(Func<DateTime> utcNow)
    {
        return new AreaClosureManager(
            NullLogger.Instance,
            new MatchingConfigService(null!, NullLogger.Instance),
            utcNow);
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
