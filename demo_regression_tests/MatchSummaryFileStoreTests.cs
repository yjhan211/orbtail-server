using game_server.services;

namespace demo_regression_tests;

public sealed class MatchSummaryFileStoreTests : IDisposable
{
    private readonly string _directory = Path.Combine(
        Path.GetTempPath(), $"manitto-match-summary-tests-{Guid.NewGuid():N}");

    [Fact]
    public void Save_CanBeReadByANewStoreInstance()
    {
        long now = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
        var store = new MatchSummaryFileStore(_directory, 5);
        var events = new List<GameEventEntry>
        {
            new() { Seq = 1, TimestampUnixMs = now, Type = "MATCH_STARTED", MatchSeed = 7 },
            new()
            {
                Seq = 2,
                TimestampUnixMs = now + 1_000,
                Type = "SUMMON_STONE_AWARDED",
                PlayerId = -101,
                IsBot = true,
                SummonStoneDelta = 3
            },
            new()
            {
                Seq = 3,
                TimestampUnixMs = now + 2_000,
                Type = "MATCH_ENDED",
                WinnerPlayerId = 10,
                EndReason = "last_survivor",
                FinalPlayerStats =
                [
                    new MatchFinalPlayerStats(10, 1, 120, 2, 400, 20),
                    new MatchFinalPlayerStats(-101, 2, 110, 1, 250, 0)
                ]
            }
        };

        store.Save(206001, "last_survivor", 10, events);
        var restartedStore = new MatchSummaryFileStore(_directory, 5);
        var summary = restartedStore.Read(206001);

        Assert.NotNull(summary);
        Assert.Equal("last_survivor", summary!.EndReason);
        Assert.Equal(10, summary.WinnerPlayerId);
        Assert.Equal(3, summary.Events.Count);
        Assert.False(Assert.Single(summary.Participants, player => player.PlayerId == 10).IsBot);
        var bot = Assert.Single(summary.Participants, player => player.PlayerId == -101);
        Assert.True(bot.IsBot);
        Assert.Equal(3, bot.SummonStonesEarned);
    }

    [Fact]
    public void Save_BuildsSwarmCutCrackAndGrowthMetrics()
    {
        // #226 F 계측: 절단·크랙·성장 카드·물폭탄이 요약 지표로 집계된다.
        long now = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
        var store = new MatchSummaryFileStore(_directory, 5);
        var events = new List<GameEventEntry>
        {
            new() { Seq = 1, TimestampUnixMs = now, Type = "MATCH_STARTED" },
            new()
            {
                Seq = 2, TimestampUnixMs = now + 10_000, Type = "ORB_GROWTH_CARD_SELECTED",
                PlayerId = 10, CardRole = "multiply", CardGrade = 1,
                GrowthBaseCost = 3, GrowthScoreSurcharge = 0, GrowthFinalCost = 3,
                GrowthSuccessCountBefore = 0, OrbCountBefore = 3
            },
            new()
            {
                Seq = 3, TimestampUnixMs = now + 40_000, Type = "ORB_GROWTH_CARD_SELECTED",
                PlayerId = 10, CardRole = "armor", CardGrade = 1,
                GrowthBaseCost = 3, GrowthScoreSurcharge = 1, GrowthFinalCost = 4,
                GrowthSuccessCountBefore = 1, OrbCountBefore = 7
            },
            new()
            {
                Seq = 4, TimestampUnixMs = now + 50_000, Type = "ORB_CRACK_ADVANCED",
                PlayerId = -101, IsBot = true, TargetPlayerId = 10, CrackCount = 1, RequiredHits = 2
            },
            new()
            {
                Seq = 5, TimestampUnixMs = now + 60_000, Type = "ORB_SUFFIX_CUT",
                PlayerId = -101, IsBot = true, TargetPlayerId = 10,
                TailOrdinal = 2, DestroyedOrbCount = 5
            },
            new()
            {
                Seq = 6, TimestampUnixMs = now + 70_000, Type = "SURVIVOR_HIT",
                PlayerId = 10, TargetPlayerId = -101, Damage = 105, DamageSourceType = "wave_bomb"
            }
        };

        var summary = store.Save(206003, "last_survivor", 10, events);

        Assert.Equal(1, summary.Metrics.TrailCutCount);
        Assert.Equal(5, summary.Metrics.TrailCutOrbsDestroyed);
        Assert.Equal(1, summary.Metrics.CrackAdvancedCount);
        Assert.Equal(1, summary.Metrics.WaveBombHitCount);
        Assert.Equal(2, summary.Metrics.GrowthSelectedCount);
        Assert.Equal(1, summary.Metrics.GrowthSelectedCounts["multiply"]);
        Assert.Equal(1, summary.Metrics.GrowthSelectedCounts["armor"]);
        Assert.Equal(30d, summary.Metrics.GrowthSelectIntervalMedianSeconds);

        var player = Assert.Single(summary.Participants, participant => participant.PlayerId == 10);
        Assert.Equal(2, player.GrowthSelectedCount);
        Assert.Equal(0, player.TrailCutsDealt);
        Assert.Equal(5, player.OrbsLostToCut);
        var bot = Assert.Single(summary.Participants, participant => participant.PlayerId == -101);
        Assert.Equal(1, bot.TrailCutsDealt);
    }

    [Fact]
    public void Save_IsIdempotentForTheSameMatchingId()
    {
        var store = new MatchSummaryFileStore(_directory, 5);
        long now = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();

        var first = store.Save(206002, "last_survivor", 1,
            [new GameEventEntry { Seq = 1, TimestampUnixMs = now, Type = "MATCH_ENDED" }]);
        var duplicate = store.Save(206002, "last_human_left", 0,
            [new GameEventEntry { Seq = 2, TimestampUnixMs = now + 1_000, Type = "MATCH_ABANDONED" }]);

        Assert.Equal(first.EndReason, duplicate.EndReason);
        Assert.Equal("last_survivor", store.Read(206002)!.EndReason);
    }

    [Fact]
    public void Save_PrunesOldSummariesToTheConfiguredLimit()
    {
        var store = new MatchSummaryFileStore(_directory, 2);
        long now = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();

        for (long matchingId = 1; matchingId <= 3; matchingId++)
        {
            store.Save(matchingId, "test", 0,
                [new GameEventEntry { Seq = matchingId, TimestampUnixMs = now + matchingId, Type = "MATCH_ENDED" }]);
            Thread.Sleep(20);
        }

        var recent = store.ListRecent(10);
        Assert.Equal(2, recent.Count);
        Assert.DoesNotContain(recent, summary => summary.MatchingId == 1);
    }

    [Fact]
    public void FullPersistenceKeepsEarlyEventsBeyondTheLiveFiveThousandEventWindow()
    {
        const long matchingId = 210001;
        var log = TestGameEventLogs.Create();
        log.BeginMatch(matchingId, 210);
        for (int index = 0; index < 5_100; index++)
            log.LogSystem(matchingId, $"event-{index}");

        var recent = log.GetRecent(matchingId);
        var complete = log.GetForPersistence(matchingId);

        Assert.Equal(5_000, recent.Count);
        Assert.DoesNotContain(recent, entry => entry.Type == "MATCH_STARTED");
        Assert.Equal(5_101, complete.Count);
        Assert.Equal("MATCH_STARTED", complete[0].Type);

        var store = new MatchSummaryFileStore(_directory, 5);
        var summary = store.Save(matchingId, "test", 0, complete);
        var persisted = store.ReadRawEvents(matchingId, 6_000);

        Assert.Equal(5_101, summary.RawEventCount);
        Assert.Equal(500, summary.Events.Count);
        Assert.Equal(5_101, persisted.Count);
        Assert.Contains(persisted, entry => entry.Type == "MATCH_STARTED");
    }

    [Fact]
    public void SaveBuildsIssue210PacingContentionAndSourceMetrics()
    {
        const long matchingId = 210002;
        long startedAt = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
        var events = new List<GameEventEntry>
        {
            new() { Seq = 1, TimestampUnixMs = startedAt, Type = "MATCH_STARTED" },
            new() { Seq = 2, TimestampUnixMs = startedAt + 1_000, Type = "SPAWN_ASSIGNMENT", PlayerId = 1, Area = "S2Library1" },
            new() { Seq = 3, TimestampUnixMs = startedAt + 2_000, Type = "SPAWN_ASSIGNMENT", PlayerId = 2, Area = "S2Library1" },
            new() { Seq = 4, TimestampUnixMs = startedAt + 60_000, Type = "SURVIVOR_FIRST_T2", PlayerId = 1 },
            new() { Seq = 5, TimestampUnixMs = startedAt + 100_000, Type = "SURVIVOR_FIRST_T3", PlayerId = 1 },
            new()
            {
                Seq = 6, TimestampUnixMs = startedAt + 150_000, Type = "SURVIVOR_ORB_BOARD_FULL",
                PlayerId = 1, ElapsedMilliseconds = 150_000
            },
            new()
            {
                Seq = 7, TimestampUnixMs = startedAt + 160_000, Type = "SUMMON_STONE_AWARDED",
                PlayerId = 1, Area = "S2Library1", Outcome = "core", SummonStoneDelta = 6
            },
            new()
            {
                Seq = 8, TimestampUnixMs = startedAt + 170_000, Type = "SUMMON_STONE_AWARDED",
                PlayerId = 1, Area = "S2Corridor1", Outcome = "normal", SummonStoneDelta = 1
            },
            new()
            {
                Seq = 9, TimestampUnixMs = startedAt + 180_000, Type = "ELIMINATE",
                PlayerId = 2, TargetPlayerId = 2, ActorPlayerId = 1, DamageSourceType = "pvp",
                Outcome = "HEALTH_ZERO"
            },
            new()
            {
                Seq = 10, TimestampUnixMs = startedAt + 200_000, Type = "ELIMINATE",
                PlayerId = 1, TargetPlayerId = 1, DamageSourceType = "closure",
                IsAreaClosureElimination = true, Outcome = "HEALTH_ZERO"
            },
            new()
            {
                Seq = 11, TimestampUnixMs = startedAt + 300_000, Type = "MATCH_ENDED",
                WinnerPlayerId = 1, EndReason = "test"
            }
        };

        var store = new MatchSummaryFileStore(_directory, 5);
        var summary = store.Save(matchingId, "test", 1, events);

        Assert.Equal(60_000, summary.Metrics.FirstTier2ElapsedMilliseconds);
        Assert.Equal(100_000, summary.Metrics.FirstTier3ElapsedMilliseconds);
        Assert.Equal(1, summary.Metrics.PvpEliminationCount);
        Assert.Equal(1, summary.Metrics.EliminationCounts["closure"]);
        Assert.Equal(6, summary.Metrics.SummonStoneSources["room"]);
        Assert.Equal(1, summary.Metrics.SummonStoneSources["corridor"]);
        Assert.Equal(6, summary.Metrics.SummonStoneSources["core"]);
        Assert.Equal(1, summary.Metrics.ContestedAreaEntryCount);
        var library = Assert.Single(summary.Metrics.AreaContention, metric => metric.Area == "S2Library1");
        Assert.Equal(2, library.MaxConcurrentPlayers);
        Assert.Equal(2, library.UniqueVisitorCount);

        var player = Assert.Single(summary.Participants, participant => participant.PlayerId == 1);
        Assert.Equal(6, player.RoomSummonStonesEarned);
        Assert.Equal(1, player.CorridorSummonStonesEarned);
        Assert.Equal(6, player.CoreSummonStonesEarned);
    }

    [Fact]
    public void SaveCountsAfterimageKillsIndependentlyFromStoneAwards()
    {
        const long matchingId = 210006;
        long startedAt = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
        var events = new List<GameEventEntry>
        {
            new() { Seq = 1, TimestampUnixMs = startedAt, Type = "MATCH_STARTED" },
            new()
            {
                Seq = 2, TimestampUnixMs = startedAt + 1_000, Type = "AFTERIMAGE_KILLED",
                PlayerId = 1, Area = "S2Library1", Outcome = "core"
            },
            new()
            {
                Seq = 3, TimestampUnixMs = startedAt + 2_000, Type = "AFTERIMAGE_KILLED",
                PlayerId = 1, Area = "S2Classroom1", Outcome = "normal"
            },
            new()
            {
                Seq = 4, TimestampUnixMs = startedAt + 3_000, Type = "AFTERIMAGE_KILLED",
                PlayerId = 1, Area = "S2Corridor1", Outcome = "normal"
            },
            new()
            {
                Seq = 5, TimestampUnixMs = startedAt + 4_000, Type = "SUMMON_STONE_AWARDED",
                PlayerId = 1, Area = "S2Library1", Outcome = "core", SummonStoneDelta = 6
            },
            new()
            {
                Seq = 6, TimestampUnixMs = startedAt + 5_000, Type = "SUMMON_STONE_AWARDED",
                PlayerId = 1, Area = "S2Corridor1", Outcome = "pvp", SummonStoneDelta = 2
            },
            new()
            {
                Seq = 7, TimestampUnixMs = startedAt + 6_000, Type = "MATCH_ENDED",
                WinnerPlayerId = 1, EndReason = "test"
            }
        };

        var store = new MatchSummaryFileStore(_directory, 5);
        var summary = store.Save(matchingId, "test", 1, events);

        Assert.Equal(1, summary.Metrics.CoreKillCount);
        Assert.Equal(1, summary.Metrics.NormalKillCount);
        Assert.Equal(1, summary.Metrics.CorridorKillCount);
        Assert.Equal(6, summary.Metrics.SummonStoneSources["core"]);
        Assert.Equal(2, summary.Metrics.SummonStoneSources["corridor"]);
    }

    [Fact]
    public void SaveUsesMatchTimestampsForFirstPacingMetrics()
    {
        const long matchingId = 210005;
        long startedAt = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
        var events = new List<GameEventEntry>
        {
            new() { Seq = 1, TimestampUnixMs = startedAt, Type = "MATCH_STARTED" },
            new()
            {
                Seq = 2, TimestampUnixMs = startedAt + 25_000, Type = "SURVIVOR_ENCOUNTER_START",
                PlayerId = 1, ElapsedMilliseconds = 0
            },
            new()
            {
                Seq = 3, TimestampUnixMs = startedAt + 75_000, Type = "SURVIVOR_FIRST_ELIMINATION",
                PlayerId = 2, ElapsedMilliseconds = 500
            },
            new()
            {
                Seq = 4, TimestampUnixMs = startedAt + 100_000, Type = "MATCH_ENDED",
                WinnerPlayerId = 1, EndReason = "test"
            }
        };

        var store = new MatchSummaryFileStore(_directory, 5);
        var summary = store.Save(matchingId, "test", 1, events);

        Assert.Equal(75_000, summary.Metrics.FirstEliminationElapsedMilliseconds);
    }

    [Fact]
    public void CoreAfterimageKillRecordsFirstLastAndDamageContributors()
    {
        var log = TestGameEventLogs.Create();

        log.LogSwarmAfterimageKilled(
            210003,
            202101,
            "S2Library1",
            isCore: true,
            firstAttackerPlayerId: 11,
            lastAttackerPlayerId: 22,
            new Dictionary<long, int> { [11] = 30, [22] = 18 });

        var killed = Assert.Single(log.GetRecent(210003));
        Assert.Equal("AFTERIMAGE_KILLED", killed.Type);
        Assert.Equal(11, killed.FirstAttackerPlayerId);
        Assert.Equal(22, killed.LastAttackerPlayerId);
        Assert.Equal([11L, 22L], killed.MonsterDamageContributions!.Select(entry => entry.PlayerId));
        Assert.Equal([30, 18], killed.MonsterDamageContributions!.Select(entry => entry.Damage));
    }

    [Fact]
    public void SaveBuildsBotPerformanceMetrics()
    {
        const long matchingId = 214001;
        long startedAt = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
        var events = new List<GameEventEntry>
        {
            new() { Seq = 1, TimestampUnixMs = startedAt, Type = "MATCH_STARTED" },
            new()
            {
                Seq = 6, TimestampUnixMs = startedAt + 4_000,
                Type = "SURVIVOR_BOT_MOVEMENT_TICK_PERFORMANCE",
                BotMovementTickP50Milliseconds = 20,
                BotMovementTickP95Milliseconds = 55,
                BotMovementTickP99Milliseconds = 80,
                BotMovementSnapshotP95Milliseconds = 4,
                BotMovementPlanningP95Milliseconds = 40,
                BotMovementWalkingP95Milliseconds = 2,
                BotMovementBroadcastP95Milliseconds = 6,
                BotMovementTickSampleCount = 200,
                BotMovementTickSkipCount = 5,
                BotMovementMaxConsecutiveSkipCount = 2
            },
            new()
            {
                Seq = 7, TimestampUnixMs = startedAt + 5_000,
                Type = "SURVIVOR_BOT_MOVEMENT_TICK_PERFORMANCE",
                BotMovementTickP50Milliseconds = 25,
                BotMovementTickP95Milliseconds = 60,
                BotMovementTickP99Milliseconds = 100,
                BotMovementSnapshotP95Milliseconds = 5,
                BotMovementPlanningP95Milliseconds = 45,
                BotMovementWalkingP95Milliseconds = 3,
                BotMovementBroadcastP95Milliseconds = 8,
                BotMovementTickSampleCount = 200,
                BotMovementTickSkipCount = 15,
                BotMovementMaxConsecutiveSkipCount = 4
            },
            new()
            {
                Seq = 8, TimestampUnixMs = startedAt + 6_000,
                Type = "AFTERIMAGE_KILLED", Area = "S2Library1", Outcome = "reinforcement"
            },
            new()
            {
                Seq = 9, TimestampUnixMs = startedAt + 7_000,
                Type = "MATCH_ENDED", WinnerPlayerId = 1, EndReason = "test"
            }
        };

        var summary = new MatchSummaryFileStore(_directory, 5)
            .Save(matchingId, "test", 1, events);

        Assert.Equal(1, summary.Metrics.ReinforcementKillCount);
        Assert.Equal(25, summary.Metrics.BotMovementTickP50Milliseconds);
        Assert.Equal(60, summary.Metrics.BotMovementTickP95Milliseconds);
        Assert.Equal(100, summary.Metrics.BotMovementTickP99Milliseconds);
        Assert.Equal(5, summary.Metrics.BotMovementSnapshotP95Milliseconds);
        Assert.Equal(45, summary.Metrics.BotMovementPlanningP95Milliseconds);
        Assert.Equal(3, summary.Metrics.BotMovementWalkingP95Milliseconds);
        Assert.Equal(8, summary.Metrics.BotMovementBroadcastP95Milliseconds);
        Assert.Equal(400, summary.Metrics.BotMovementTickSampleCount);
        Assert.Equal(20, summary.Metrics.BotMovementTickSkipCount);
        Assert.Equal(20d / 420d, summary.Metrics.BotMovementTickSkipRate, 6);
        Assert.Equal(4, summary.Metrics.BotMovementMaxConsecutiveSkipCount);
    }

    [Fact]
    public void FinalizationGate_AcceptsOnlyTheFirstCaller()
    {
        var events = TestGameEventLogs.Create();

        Assert.True(events.TryBeginFinalization(206003));
        Assert.False(events.TryBeginFinalization(206003));
    }

    public void Dispose()
    {
        if (Directory.Exists(_directory)) Directory.Delete(_directory, true);
    }
}
