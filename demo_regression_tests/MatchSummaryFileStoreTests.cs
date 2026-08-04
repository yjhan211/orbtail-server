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
                    new SurvivorFinalPlayerStats(10, 1, 120, 2, 400, 20),
                    new SurvivorFinalPlayerStats(-101, 2, 110, 1, 250, 0)
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
    public void Save_CountsBotOrbMergesAlongsidePlayerMerges()
    {
        long now = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
        var store = new MatchSummaryFileStore(_directory, 5);
        var events = new List<GameEventEntry>
        {
            new() { Seq = 1, TimestampUnixMs = now, Type = "MATCH_STARTED" },
            new()
            {
                Seq = 2, TimestampUnixMs = now + 1_000, Type = "SURVIVOR_ORB_BOARD_STATE",
                PlayerId = 10, Outcome = "merge"
            },
            new()
            {
                Seq = 3, TimestampUnixMs = now + 2_000, Type = "SURVIVOR_ORB_BOARD_STATE",
                PlayerId = -101, IsBot = true, Outcome = "bot_merge"
            },
            new()
            {
                Seq = 4, TimestampUnixMs = now + 3_000, Type = "SURVIVOR_ORB_BOARD_STATE",
                PlayerId = -101, IsBot = true, Outcome = "bot_summon"
            }
        };

        var summary = store.Save(206003, "last_survivor", 10, events);

        Assert.Equal(1, Assert.Single(summary.Participants, player => player.PlayerId == 10).MergeCount);
        var bot = Assert.Single(summary.Participants, player => player.PlayerId == -101);
        Assert.Equal(1, bot.MergeCount);
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
        var log = new GameEventLogManager();
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
            new() { Seq = 2, TimestampUnixMs = startedAt + 1_000, Type = "SPAWN_ASSIGNMENT", PlayerId = 1, Area = "Library" },
            new() { Seq = 3, TimestampUnixMs = startedAt + 2_000, Type = "SPAWN_ASSIGNMENT", PlayerId = 2, Area = "Library" },
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
                PlayerId = 1, Area = "Library", Outcome = "core", SummonStoneDelta = 6
            },
            new()
            {
                Seq = 8, TimestampUnixMs = startedAt + 170_000, Type = "SUMMON_STONE_AWARDED",
                PlayerId = 1, Area = "Corridor", Outcome = "normal", SummonStoneDelta = 1
            },
            new()
            {
                Seq = 9, TimestampUnixMs = startedAt + 180_000, Type = "ELIMINATE",
                PlayerId = 2, TargetPlayerId = 2, ActorPlayerId = 1, DamageSourceType = "pvp",
                Outcome = "MENTAL_ZERO"
            },
            new()
            {
                Seq = 10, TimestampUnixMs = startedAt + 200_000, Type = "ELIMINATE",
                PlayerId = 1, TargetPlayerId = 1, DamageSourceType = "closure",
                IsAreaClosureElimination = true, Outcome = "MENTAL_ZERO"
            },
            new()
            {
                Seq = 11, TimestampUnixMs = startedAt + 210_000, Type = "SURVIVOR_REWARD_AREA_SNAPSHOT",
                PhaseIndex = 1, Outcome = "closure",
                RewardAreaStates = [new MonsterRewardAreaTelemetry("Library", 9, 1, 14)]
            },
            new()
            {
                Seq = 12, TimestampUnixMs = startedAt + 220_000, Type = "SURVIVOR_CORE_CONTESTED_ENTRY",
                PlayerId = 2, Area = "Library", MonsterId = 202101,
                CoreCurrentHealth = 24, CoreMaxHealth = 48, AlreadyPresentPlayerIds = [1]
            },
            new()
            {
                Seq = 13, TimestampUnixMs = startedAt + 230_000, Type = "SURVIVOR_PVP_PROJECTILE_LAUNCHED",
                PlayerId = 1, ActorPlayerId = 1, TargetPlayerId = 2, ProjectileId = 1, Outcome = "launched"
            },
            new()
            {
                Seq = 14, TimestampUnixMs = startedAt + 231_000, Type = "SURVIVOR_PVP_PROJECTILE_RESOLVED",
                PlayerId = 1, ActorPlayerId = 1, TargetPlayerId = 2, ProjectileId = 1,
                HitTargetCount = 1, Outcome = "hit"
            },
            new()
            {
                Seq = 15, TimestampUnixMs = startedAt + 240_000, Type = "SURVIVOR_PVP_PROJECTILE_LAUNCHED",
                PlayerId = 2, ActorPlayerId = 2, IsBot = true, TargetPlayerId = 1, ProjectileId = 2, Outcome = "launched"
            },
            new()
            {
                Seq = 16, TimestampUnixMs = startedAt + 241_000, Type = "SURVIVOR_PVP_PROJECTILE_RESOLVED",
                PlayerId = 2, ActorPlayerId = 2, IsBot = true, TargetPlayerId = 1, ProjectileId = 2,
                HitTargetCount = 0, Outcome = "dodged"
            },
            new()
            {
                Seq = 17, TimestampUnixMs = startedAt + 300_000, Type = "MATCH_ENDED",
                WinnerPlayerId = 1, EndReason = "test"
            }
        };

        var store = new MatchSummaryFileStore(_directory, 5);
        var summary = store.Save(matchingId, "test", 1, events);

        Assert.Equal(60_000, summary.Metrics.FirstTier2ElapsedMilliseconds);
        Assert.Equal(100_000, summary.Metrics.FirstTier3ElapsedMilliseconds);
        Assert.Equal(150_000, summary.Metrics.FirstBoardFullElapsedMilliseconds);
        Assert.Equal(1, summary.Metrics.PvpEliminationCount);
        Assert.Equal(2, summary.Metrics.PvpProjectileLaunchCount);
        Assert.Equal(2, summary.Metrics.PvpProjectileResolvedCount);
        Assert.Equal(0, summary.Metrics.PvpProjectileUnresolvedCount);
        Assert.Equal(1, summary.Metrics.PvpProjectileHitCount);
        Assert.Equal(1, summary.Metrics.PvpProjectileMissCount);
        Assert.Equal(0.5d, summary.Metrics.PvpProjectileHitRate);
        Assert.Equal(1, summary.Metrics.PvpProjectileOutcomeCounts["hit"]);
        Assert.Equal(1, summary.Metrics.PvpProjectileOutcomeCounts["dodged"]);
        Assert.Equal(1, summary.Metrics.HumanPvpProjectileMetrics.LaunchCount);
        Assert.Equal(1d, summary.Metrics.HumanPvpProjectileMetrics.HitRate);
        Assert.Equal(1, summary.Metrics.HumanPvpProjectileMetrics.OutcomeCounts["hit"]);
        Assert.Equal(1, summary.Metrics.BotPvpProjectileMetrics.LaunchCount);
        Assert.Equal(0d, summary.Metrics.BotPvpProjectileMetrics.HitRate);
        Assert.Equal(1, summary.Metrics.BotPvpProjectileMetrics.OutcomeCounts["dodged"]);
        Assert.Equal(1, summary.Metrics.EliminationCounts["closure"]);
        Assert.Equal(6, summary.Metrics.SummonStoneSources["room"]);
        Assert.Equal(1, summary.Metrics.SummonStoneSources["corridor"]);
        Assert.Equal(6, summary.Metrics.SummonStoneSources["core"]);
        Assert.Equal(1, summary.Metrics.ContestedAreaEntryCount);
        var library = Assert.Single(summary.Metrics.AreaContention, metric => metric.Area == "Library");
        Assert.Equal(2, library.MaxConcurrentPlayers);
        Assert.Equal(2, library.UniqueVisitorCount);
        var rewardSnapshot = Assert.Single(summary.Metrics.RewardAreaSnapshots);
        Assert.Equal(1, rewardSnapshot.PhaseIndex);
        Assert.Equal(14, Assert.Single(rewardSnapshot.Areas).RemainingSummonStoneReward);
        var coreEntry = Assert.Single(summary.Metrics.CoreContestedEntries);
        Assert.Equal(24, coreEntry.CoreCurrentHealth);
        Assert.Equal([1L], coreEntry.AlreadyPresentPlayerIds);

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
                PlayerId = 1, Area = "Library", Outcome = "core"
            },
            new()
            {
                Seq = 3, TimestampUnixMs = startedAt + 2_000, Type = "AFTERIMAGE_KILLED",
                PlayerId = 1, Area = "Classroom1", Outcome = "normal"
            },
            new()
            {
                Seq = 4, TimestampUnixMs = startedAt + 3_000, Type = "AFTERIMAGE_KILLED",
                PlayerId = 1, Area = "Corridor", Outcome = "normal"
            },
            new()
            {
                Seq = 5, TimestampUnixMs = startedAt + 4_000, Type = "SUMMON_STONE_AWARDED",
                PlayerId = 1, Area = "Library", Outcome = "core", SummonStoneDelta = 6
            },
            new()
            {
                Seq = 6, TimestampUnixMs = startedAt + 5_000, Type = "SUMMON_STONE_AWARDED",
                PlayerId = 1, Area = "Corridor", Outcome = "pvp", SummonStoneDelta = 2
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

        Assert.Equal(25_000, summary.Metrics.FirstEncounterElapsedMilliseconds);
        Assert.Equal(75_000, summary.Metrics.FirstEliminationElapsedMilliseconds);
    }

    [Fact]
    public void RewardAreaAndContestedCoreTelemetryAreDeduplicatedAndRecorded()
    {
        const long matchingId = 210004;
        var log = new GameEventLogManager();
        var rewardSnapshot = new MonsterRewardAreaSnapshot(
            0,
            [new MonsterRewardAreaState(network.common.AreaType.Library, 9, 1, 14)]);

        log.LogRewardAreaSnapshot(matchingId, rewardSnapshot, "initial");
        log.LogRewardAreaSnapshot(matchingId, rewardSnapshot, "reconnect");
        log.SetPlayerArea(matchingId, 11, "Library");
        log.LogMove(matchingId, 22, "Corridor", "Library", isBot: false);
        log.LogCoreContestedEntry(matchingId, 22, "Library", new network.common.data.models.MonsterRuntimeInfo
        {
            MonsterId = 202101,
            AreaType = network.common.AreaType.Library,
            IsAlive = true,
            IsCore = true,
            CurrentHealth = 24,
            MaxHealth = 48
        }, isBot: false);

        var events = log.GetRecent(matchingId);
        Assert.Single(events, entry => entry.Type == "SURVIVOR_REWARD_AREA_SNAPSHOT");
        var contested = Assert.Single(events, entry => entry.Type == "SURVIVOR_CORE_CONTESTED_ENTRY");
        Assert.Equal(24, contested.CoreCurrentHealth);
        Assert.Equal([11L], contested.AlreadyPresentPlayerIds);
    }

    [Fact]
    public void CoreAfterimageKillRecordsFirstLastAndDamageContributors()
    {
        var log = new GameEventLogManager();

        log.LogEmotionAfterimageKilled(
            210003,
            202101,
            "Library",
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
    public void SaveBuildsReinforcementDensityAndBotPerformanceMetrics()
    {
        const long matchingId = 214001;
        long startedAt = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
        var events = new List<GameEventEntry>
        {
            new() { Seq = 1, TimestampUnixMs = startedAt, Type = "MATCH_STARTED" },
            new()
            {
                Seq = 2, TimestampUnixMs = startedAt + 1_000,
                Type = "SURVIVOR_MONSTER_DENSITY_SAMPLE", Area = "Library",
                AliveMonsterCount = 0, GlobalAliveMonsterCount = 100,
                ReinforcementRemainingBudget = 4, HasAttackableMonster = false
            },
            new()
            {
                Seq = 3, TimestampUnixMs = startedAt + 2_000,
                Type = "SURVIVOR_MONSTER_DENSITY_SAMPLE", Area = "Library",
                AliveMonsterCount = 0, GlobalAliveMonsterCount = 99,
                ReinforcementRemainingBudget = 4, HasAttackableMonster = false
            },
            new()
            {
                Seq = 4, TimestampUnixMs = startedAt + 2_500,
                Type = "SURVIVOR_REINFORCEMENT_RELEASED", Area = "Library",
                PhaseIndex = 0, ReinforcementReleasedCount = 2,
                ReinforcementRemainingBudget = 2, AliveMonsterCount = 2
            },
            new()
            {
                Seq = 5, TimestampUnixMs = startedAt + 3_000,
                Type = "SURVIVOR_MONSTER_DENSITY_SAMPLE", Area = "Library",
                AliveMonsterCount = 2, GlobalAliveMonsterCount = 102,
                ReinforcementRemainingBudget = 2, HasAttackableMonster = true
            },
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
                Type = "AFTERIMAGE_KILLED", Area = "Library", Outcome = "reinforcement"
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
        Assert.Equal(2, summary.Metrics.ReinforcementReleasedCount);
        Assert.Equal(3, summary.Metrics.MonsterDensitySampleCount);
        Assert.Equal(1, summary.Metrics.MonsterContactSampleCount);
        Assert.Equal(1d / 3d, summary.Metrics.MonsterContactRatio, 6);
        Assert.Equal(102, summary.Metrics.MaxConcurrentAliveAfterimages);
        var library = Assert.Single(summary.Metrics.HotspotDensity);
        Assert.Equal(1, library.LongNoContactGapCount);
        Assert.Equal(2_000, library.LongestNoContactGapMilliseconds);
        Assert.Equal(2, library.ReinforcementReleasedCount);
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
        var events = new GameEventLogManager();

        Assert.True(events.TryBeginFinalization(206003));
        Assert.False(events.TryBeginFinalization(206003));
    }

    public void Dispose()
    {
        if (Directory.Exists(_directory)) Directory.Delete(_directory, true);
    }
}
