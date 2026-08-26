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

        var first = MatchSpawnData.CreateAssignments(195004, playerIds);
        var second = MatchSpawnData.CreateAssignments(195004, playerIds.Reverse());

        Assert.Equal(8, first.Count);
        Assert.Equal(8, first.Values.Distinct().Count());
        Assert.Equal(
            MatchSpawnData.GetCorridorAnchors().OrderBy(cell => cell.X).ThenBy(cell => cell.Y),
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

        // #226 단계 B 5분 재정렬: 바깥 포드 → 중간 포드 → 쌍 구역 → 밴드 → 운동장 최종(=타이머 만료).
        // #229 순차 폐쇄: 웨이브가 구역 하나씩으로 쪼개져 4초 간격으로 앞당겨 배치된다 —
        // 5묶음(4+4+2+2+1) = 13개. 각 묶음의 마지막 구역은 원래 시각 그대로다.
        Assert.Empty(state.ClosedAreas);
        Assert.Equal(13, state.Waves.Count);
        Assert.All(state.Waves, wave => Assert.Single(wave.Areas));
        Assert.Equal(AreaType.Ground, state.ClosureOrder[^1]);
        // 원래 웨이브 시각은 모두 남아 있고, 종료 봉투(운동장 300초)는 밀리지 않는다.
        var closureTimes = state.Waves.Select(wave => wave.ClosureAtSeconds).ToList();
        foreach (int original in new[] { 100, 150, 200, 250, 300 })
            Assert.Contains(original, closureTimes);
        Assert.Equal(300, closureTimes.Max());
        Assert.Equal(88, closureTimes.Min());
        // 첫 묶음의 네 구역이 88~100초에 하나씩 배치된다 (순서는 매치마다 섞인다).
        Assert.Equal(
            new[] { AreaType.Classroom4, AreaType.Classroom3, AreaType.Storage2, AreaType.Classroom2 }
                .OrderBy(area => area),
            state.Waves.Where(wave => wave.ClosureAtSeconds <= 100)
                .SelectMany(wave => wave.Areas).OrderBy(area => area));
        Assert.Equal(
            [AreaType.Ground],
            state.Waves[^1].Areas);
    }

    // #272 자기장 파생 웨이브: 실전 폐쇄 시간표가 자기장(보행 거리 필드)에서 나온다 —
    // 깔때기 순서·종료 봉투·오염 0(압박은 자기장 경사 전담)을 잠근다.
    [Fact]
    public void BuildSwarmFieldWaves_DerivesFunnelScheduleEndingAtMatchExpiry()
    {
        double hold = Config.SWARM_FIELD_HOLD_SECONDS;
        double shrink = Config.SWARM_MATCH_DURATION_SECONDS - hold;

        var waves = AreaClosureManager.BuildSwarmFieldWaves(hold, shrink);

        // 맵 전 구역이 정확히 한 번씩 닫힌다.
        var mapAreas = GameMapData.GetAreas(MapId.School)
            .Select(region => region.AreaType)
            .Distinct()
            .OrderBy(area => area)
            .ToList();
        var waveAreas = waves.SelectMany(wave => wave.Areas).ToList();
        Assert.Equal(waveAreas.Count, waveAreas.Distinct().Count());
        Assert.Equal(mapAreas, waveAreas.OrderBy(area => area));

        // 오름차순 스케줄, 유예(HOLD) 동안은 폐쇄 없음, 운동장 최종 폐쇄 = 타이머 만료.
        Assert.Equal(waves.Select(wave => wave.ClosureAtSeconds).OrderBy(t => t),
            waves.Select(wave => wave.ClosureAtSeconds));
        Assert.True(waves[0].ClosureAtSeconds > hold);
        Assert.Equal([AreaType.Ground], waves[^1].Areas);
        Assert.Equal(Config.SWARM_MATCH_DURATION_SECONDS, waves[^1].ClosureAtSeconds);

        // 폐쇄 구역 틱 오염은 0 — 오염은 자기장 초과 거리 비례가 전담한다.
        Assert.All(waves, wave => Assert.Equal(0, wave.ClosedAreaCorruptionPerSecond));
    }

    [Fact]
    public void InitializeMatching_WithSwarmFieldWavesKeepsDerivedScheduleUnstaggered()
    {
        var now = new DateTime(2026, 8, 26, 0, 0, 0, DateTimeKind.Utc);
        var manager = CreateManager(() => now);
        var waves = AreaClosureManager.BuildSwarmFieldWaves(
            Config.SWARM_FIELD_HOLD_SECONDS,
            Config.SWARM_MATCH_DURATION_SECONDS - Config.SWARM_FIELD_HOLD_SECONDS);

        var state = manager.InitializeMatching(272001, wavesOverride: waves);

        // wavesOverride는 셔플·스태거 없이 그대로 쓴다 — 파생 시각이 곧 폐쇄 시각이다.
        Assert.Equal(waves.Select(wave => wave.ClosureAtSeconds),
            state.Waves.Select(wave => wave.ClosureAtSeconds));
        Assert.Equal(AreaType.Ground, state.ClosureOrder[^1]);
        Assert.Empty(state.ClosedAreas);
    }

    [Fact(Skip = "#219 클론 전환: 레거시 페이즈 머신·구역 폐쇄 — 클론은 M3 젬 헌트 타이머로 대체, 부활 시 재작성")]
    public void InitializeMatching_WithStartingRoomsClosesCorridorBeforeFirstPhaseTick()
    {
        var now = new DateTime(2026, 8, 4, 0, 0, 0, DateTimeKind.Utc);
        var manager = CreateManager(() => now);
        var startingRooms = MatchSpawnData.GetPhaseRoomCandidates();

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
    // #229 순차 폐쇄: 한 웨이브의 구역들이 한꺼번에 닫히지 않고 4초 간격으로 하나씩 닫힌다.
    // 마지막 구역은 원래 웨이브 시각(100초) 그대로라 종료 봉투는 밀리지 않는다.
    // 순서는 매치마다 섞이므로 "어떤 방"이 아니라 "몇 개씩, 언제"를 검사한다.
    public void CheckClosureSchedule_ClosesWaveAreasOneByOneEndingAtTheOriginalTime()
    {
        var now = new DateTime(2026, 7, 20, 0, 0, 0, DateTimeKind.Utc);
        var manager = CreateManager(() => now);
        const long matchingId = 195002;
        manager.InitializeMatching(matchingId);

        var firstWave = new[]
        {
            AreaType.Classroom4, AreaType.Classroom3, AreaType.Storage2, AreaType.Classroom2
        };
        var closedAt = new Dictionary<AreaType, int>();
        for (int second = 1; second <= 100; second++)
        {
            now = now.AddSeconds(1);
            foreach (var area in manager.CheckClosureSchedule(matchingId).ClosedAreas)
                closedAt[area] = second;
        }

        // 네 구역 전부 100초까지 닫힌다.
        Assert.Equal(firstWave.OrderBy(area => area), closedAt.Keys.OrderBy(area => area));
        // 한꺼번에 닫히지 않는다 — 서로 다른 시각이 최소 3개는 나온다.
        Assert.True(closedAt.Values.Distinct().Count() >= 3,
            $"동시 폐쇄로 되돌아갔다: {string.Join(",", closedAt.Select(pair => $"{pair.Key}@{pair.Value}"))}");
        // 마지막은 원래 웨이브 시각. 가장 이른 것도 4초 간격 안에 있다.
        Assert.Equal(100, closedAt.Values.Max());
        Assert.Equal(88, closedAt.Values.Min());
        Assert.False(manager.IsAreaClosed(matchingId, AreaType.Corridor));
    }

    [Fact]
    public void EnvironmentalDamage_UsesLatestClosedWaveRateAndAddsOvertime()
    {
        var now = new DateTime(2026, 7, 20, 0, 0, 0, DateTimeKind.Utc);
        var manager = CreateManager(() => now);
        const long matchingId = 195003;
        manager.InitializeMatching(matchingId);

        // #222 5방 사망 조정(08-10 2차): 웨이브 초당 오염 17/20/23/26/29 → 5초 틱 기준 검증.
        now = now.AddSeconds(100);
        manager.CheckClosureSchedule(matchingId);
        Assert.Equal(85, manager.GetEnvironmentalCorruptionDelta(matchingId, AreaType.Classroom4));
        Assert.Equal(0, manager.GetEnvironmentalCorruptionDelta(matchingId, AreaType.Ground));

        now = now.AddSeconds(150); // 4:10 — 운동장 전 웨이브(밴드까지)가 모두 닫힌 시점.
        manager.CheckClosureSchedule(matchingId);
        Assert.Equal(130, manager.GetEnvironmentalCorruptionDelta(matchingId, AreaType.Classroom4));
        Assert.Equal(0, manager.GetEnvironmentalCorruptionDelta(matchingId, AreaType.Ground));

        now = now.AddSeconds(50); // 5:00 — 운동장 최종 폐쇄 + 오버타임 개시(+2/초).
        manager.CheckClosureSchedule(matchingId);
        Assert.Equal(155, manager.GetEnvironmentalCorruptionDelta(matchingId, AreaType.Classroom4));
        Assert.Equal(155, manager.GetEnvironmentalCorruptionDelta(matchingId, AreaType.Ground));
        Assert.Equal(155, manager.GetEnvironmentalCorruptionDelta(matchingId, AreaType.Corridor));
    }

    [Fact]
    public void ClosureAndOvertimeReachTheFiveToSevenMinuteTerminationEnvelope()
    {
        var now = new DateTime(2026, 7, 24, 0, 0, 0, DateTimeKind.Utc);
        var manager = CreateManager(() => now);
        const long matchingId = 198502;
        var state = manager.InitializeMatching(matchingId);

        Assert.Equal(300, state.Waves[^1].ClosureAtSeconds);
        now = now.AddSeconds(300);
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

        now = now.AddSeconds(220);
        manager.CheckClosureSchedule(matchingId);

        // #229 순차 폐쇄: 밴드 두 구역이 4초 간격으로 하나씩 닫힌다(246초·250초).
        // 전역 폐쇄가 꺼져 있다는 계약만 검사하므로 "둘 다 닫혔는가"로 본다.
        var bandClosed = new List<AreaType>();
        for (int second = 0; second < 60; second++)
        {
            now = now.AddSeconds(1);
            bandClosed.AddRange(manager.CheckClosureSchedule(matchingId).ClosedAreas);
            Assert.False(manager.CheckGlobalClosureSchedule(matchingId).HasTransition);
            Assert.False(manager.GetGlobalClosureClientState(matchingId).IsKnown);
        }

        Assert.Contains(AreaType.Corridor, bandClosed);
        Assert.Contains(AreaType.Junkyard, bandClosed);
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
