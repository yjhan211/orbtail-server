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
    public void InitializeMatching_UsesFixedP0WavesAndClosesGroundLast()
    {
        var now = new DateTime(2026, 7, 20, 0, 0, 0, DateTimeKind.Utc);
        var manager = CreateManager(() => now);

        var state = manager.InitializeMatching(195001);

        // #219 클론 스케줄 (08-09): 바깥 포드 → 중간 포드 → 쌍 구역 → 밴드 → 운동장 최종.
        Assert.Empty(state.ClosedAreas);
        Assert.Equal(5, state.Waves.Count);
        Assert.Equal([90, 150, 210, 270, 330], state.Waves.Select(wave => wave.ClosureAtSeconds));
        Assert.Equal(AreaType.Ground, state.ClosureOrder[^1]);
        Assert.Equal(
            [AreaType.Classroom4, AreaType.Classroom3, AreaType.Storage2, AreaType.Classroom2],
            state.Waves[0].Areas);
        Assert.Equal(
            [AreaType.Ground],
            state.Waves[^1].Areas);
    }

    [Fact(Skip = "#219 클론 전환: 레거시 페이즈 머신·구역 폐쇄 — 클론은 M3 젬 헌트 타이머로 대체, 부활 시 재작성")]
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

        now = now.AddSeconds(75);
        var warning = manager.CheckClosureSchedule(matchingId);

        Assert.Equal(15, warning.WarningSeconds);
        Assert.Equal(
            [AreaType.Classroom4, AreaType.Classroom3, AreaType.Storage2, AreaType.Classroom2],
            warning.WarningAreas);
        Assert.Empty(warning.ClosedAreas);

        now = now.AddSeconds(15);
        var closure = manager.CheckClosureSchedule(matchingId);

        Assert.Empty(closure.WarningAreas);
        Assert.Equal(
            [AreaType.Classroom4, AreaType.Classroom3, AreaType.Storage2, AreaType.Classroom2],
            closure.ClosedAreas);
        Assert.True(manager.IsAreaClosed(matchingId, AreaType.Classroom4));
        Assert.True(manager.IsAreaClosed(matchingId, AreaType.Storage2));
        Assert.False(manager.IsAreaClosed(matchingId, AreaType.Corridor));
    }

    [Fact]
    public void EnvironmentalDamage_UsesLatestClosedWaveRateAndAddsOvertime()
    {
        var now = new DateTime(2026, 7, 20, 0, 0, 0, DateTimeKind.Utc);
        var manager = CreateManager(() => now);
        const long matchingId = 195003;
        manager.InitializeMatching(matchingId);

        // #219 3배 상향(08-09): 웨이브 초당 오염 12/18/24/30/36 → 5초 틱 기준 검증.
        now = now.AddSeconds(90);
        manager.CheckClosureSchedule(matchingId);
        Assert.Equal(60, manager.GetEnvironmentalCorruptionDelta(matchingId, AreaType.Classroom4));
        Assert.Equal(0, manager.GetEnvironmentalCorruptionDelta(matchingId, AreaType.Ground));

        now = now.AddSeconds(200); // 4:50 — 운동장 전 웨이브(밴드까지)가 모두 닫힌 시점.
        manager.CheckClosureSchedule(matchingId);
        Assert.Equal(150, manager.GetEnvironmentalCorruptionDelta(matchingId, AreaType.Classroom4));
        Assert.Equal(0, manager.GetEnvironmentalCorruptionDelta(matchingId, AreaType.Ground));

        now = now.AddSeconds(40); // 5:30 — 운동장 최종 폐쇄 + 오버타임 개시(+2/초).
        manager.CheckClosureSchedule(matchingId);
        Assert.Equal(190, manager.GetEnvironmentalCorruptionDelta(matchingId, AreaType.Classroom4));
        Assert.Equal(190, manager.GetEnvironmentalCorruptionDelta(matchingId, AreaType.Ground));
        Assert.Equal(190, manager.GetEnvironmentalCorruptionDelta(matchingId, AreaType.Corridor));
    }

    [Fact]
    public void ClosureAndOvertimeReachTheFiveToSevenMinuteTerminationEnvelope()
    {
        var now = new DateTime(2026, 7, 24, 0, 0, 0, DateTimeKind.Utc);
        var manager = CreateManager(() => now);
        const long matchingId = 198502;
        var state = manager.InitializeMatching(matchingId);

        Assert.Equal(330, state.Waves[^1].ClosureAtSeconds);
        now = now.AddSeconds(330);
        manager.CheckClosureSchedule(matchingId);
        Assert.True(manager.IsAreaClosed(matchingId, AreaType.Ground));
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

        now = now.AddSeconds(240);
        manager.CheckClosureSchedule(matchingId);

        now = now.AddSeconds(15); // 4:15 — 밴드(테라스·복도) 웨이브 경고창.
        var warning = manager.CheckClosureSchedule(matchingId);
        Assert.Equal([AreaType.Corridor, AreaType.Junkyard], warning.WarningAreas);
        Assert.False(manager.CheckGlobalClosureSchedule(matchingId).HasTransition);
        Assert.False(manager.GetGlobalClosureClientState(matchingId).IsKnown);

        now = now.AddSeconds(15);
        var closure = manager.CheckClosureSchedule(matchingId);
        Assert.Equal([AreaType.Corridor, AreaType.Junkyard], closure.ClosedAreas);
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
