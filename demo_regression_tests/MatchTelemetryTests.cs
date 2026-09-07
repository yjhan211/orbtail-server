using game_server.services;
using network.common;
using network.common.data;
using network.common.data.helpers;
using network.common.data.models;

namespace demo_regression_tests;

public sealed class MatchTelemetryTests
{
    public MatchTelemetryTests()
    {
        InitializeBattleCombatData();
    }

    [Fact]
    public void MatchTelemetrySurvivesCleanupWithSeedExploreRecoveryAndFinalStats()
    {
        const long matchingId = 195001;
        var log = TestGameEventLogs.Create();
        int seed = MatchSpawnData.GetDeterministicSeed(matchingId);
        var anchor = MatchSpawnData.GetCorridorAnchors()[0];

        log.BeginMatch(matchingId, seed);
        log.LogSpawnAssignment(
            matchingId, 101, seed, MatchSpawnData.GetAnchorIndex(anchor),
            anchor.X, anchor.Y, "Corridor1F", isBot: false);
        log.LogExploreStart(matchingId, 101, 77, "Library", isBot: false);
        log.RecordRecovery(matchingId, 101, 15);
        log.LogRecoveryUse(matchingId, 101, 201000008, 15, "inventory_consumable", isBot: false);
        log.LogMatchEnded(
            matchingId,
            101,
            "last_survivor",
            "not_required",
            [new MatchFinalPlayerStats(101, 1, 301, 2, 45, 15)]);

        log.Clear(matchingId);

        var events = log.GetRecent(matchingId, 5_000);
        Assert.Contains(events, entry =>
            entry.Type == "MATCH_STARTED" && entry.MatchSeed == seed);
        Assert.Contains(events, entry =>
            entry.Type == "SPAWN_ASSIGNMENT" && entry.SpawnAnchorIndex == 1);
        Assert.Contains(events, entry =>
            entry.Type == "RECOVERY_USED" && entry.RecoveryAmount == 15);
        var ended = Assert.Single(events, entry => entry.Type == "MATCH_ENDED");
        Assert.Equal(101, ended.WinnerPlayerId);
        Assert.Equal("last_survivor", ended.EndReason);
        Assert.Equal(45, Assert.Single(ended.FinalPlayerStats!).TotalDamageDealt);
    }

    [Fact]
    public void ClosureWarningTracksAdditionalExploreExitAndReentry()
    {
        const long matchingId = 195002;
        var log = TestGameEventLogs.Create();

        log.LogClosureWarningSnapshot(
            matchingId,
            202,
            ["Library"],
            "Library",
            health: 40,
            inventorySlotsUsed: 5,
            inventorySlotCapacity: 6,
            closureAtUnixMs: DateTimeOffset.UtcNow.AddSeconds(15).ToUnixTimeMilliseconds(),
            isBot: false);
        log.LogExploreStart(matchingId, 202, 88, "Library", isBot: false);
        log.LogMove(matchingId, 202, "Library", "Corridor1F", isBot: false);
        log.LogMove(matchingId, 202, "Corridor1F", "Library", isBot: false);

        var events = log.GetRecent(matchingId, 5_000);
        var snapshot = Assert.Single(events, entry => entry.Type == "CLOSURE_WARNING_SNAPSHOT");
        Assert.Equal(40, snapshot.Health);
        Assert.Equal(5, snapshot.InventorySlotsUsed);

        var exit = Assert.Single(events, entry => entry.Type == "CLOSURE_WARNING_EXIT");
        Assert.Equal(1, exit.AdditionalExploreCount);
        Assert.NotNull(exit.ExitedAtUnixMs);

        var reentry = Assert.Single(events, entry => entry.Type == "CLOSURE_WARNING_REENTRY");
        Assert.Equal(1, reentry.AdditionalExploreCount);
        Assert.NotNull(reentry.ReenteredAtUnixMs);
    }

    [Fact]
    public void MilestoneAndEndEventsExposeCombatAndTieBreakEvidence()
    {
        const long matchingId = 195003;
        var now = DateTimeOffset.UtcNow;
        var log = TestGameEventLogs.Create();

        log.LogTierReached(matchingId, 301, 107000004, 2, false, now.AddSeconds(1));
        log.LogTierReached(matchingId, 301, 107000005, 3, false, now.AddSeconds(2));
        log.LogHit(matchingId, 301, 302, 107000005, 20, true, false, now.AddSeconds(3));
        log.LogOvertimeStageChanged(matchingId, 2, 2);
        log.LogMatchEnded(matchingId, 301, "overtime_settlement", "survival>kills>damage>recovery",
            [new MatchFinalPlayerStats(301, 1, 330, 1, 20, 0)]);

        var events = log.GetRecent(matchingId, 5_000);
        Assert.Contains(events, entry => entry.Type == "SURVIVOR_FIRST_T2");
        Assert.Contains(events, entry => entry.Type == "SURVIVOR_FIRST_T3");
        Assert.Contains(events, entry => entry.Type == "SURVIVOR_FIRST_ELIMINATION");
        Assert.Contains(events, entry =>
            entry.Type == "OVERTIME_STAGE_CHANGED" && entry.OvertimeStage == 2);
        Assert.Contains(events, entry =>
            entry.Type == "MATCH_ENDED" &&
            entry.TieBreakCriterion == "survival>kills>damage>recovery");
    }

    [Fact]
    public void AfterimageHitTelemetryCapturesTargetKindHealthAndLethalOutcome()
    {
        const long matchingId = 195004;
        var log = TestGameEventLogs.Create();
        var now = DateTimeOffset.UtcNow;

        log.LogSwarmAfterimageHit(
            matchingId,
            monsterId: 202108,
            targetPlayerId: 401,
            area: "Library",
            damage: 7,
            healthBefore: 6,
            healthAfter: 0,
            isLethal: true,
            isBot: false,
            occurredAt: now);

        var hit = Assert.Single(log.GetRecent(matchingId));
        Assert.Equal("AFTERIMAGE_HIT", hit.Type);
        Assert.Equal(202108, hit.MonsterId);
        Assert.Equal("emotion_afterimage", hit.DamageSourceType);
        Assert.Equal(401, hit.TargetPlayerId);
        Assert.Equal("Library", hit.Area);
        Assert.Equal(7, hit.Damage);
        Assert.Equal(6, hit.HealthBefore);
        Assert.Equal(0, hit.HealthAfter);
        Assert.Equal("eliminated", hit.Outcome);
        Assert.False(hit.IsBot);
    }
    [Fact]
    public void OrbBoardTelemetryCapturesTransitionsMergeWindowsColorRatesAndVolleyTargets()
    {
        const long matchingId = 198401;
        const long playerId = 401;
        var log = TestGameEventLogs.Create();
        var redPair = new[]
        {
            new InGameItemInfo { ItemId = 107000010, Count = 1 },
            new InGameItemInfo { ItemId = 107000010, Count = 1 }
        };

        log.LogOrbBoardTransition(matchingId, playerId, redPair, 107000010, "Library", "pickup", false);
        Thread.Sleep(10);
        log.LogOrbBoardTransition(matchingId, playerId,
            [new InGameItemInfo { ItemId = 107000031, Count = 1 }], 107000031, "Gym", "merge", false);
        var volley = new ProximityCombatAttack(playerId, 402, AreaType.S2Gym1, 107000020, 6, 0.2f, 1f, 3);
        log.LogOrbAttackTargets(matchingId, [volley], [volley],
            new Dictionary<long, OrbColor> { [playerId] = OrbColor.Green });
        log.LogPelletPickupOutcome(matchingId, playerId, 201000008, 15, 15, "effective", false);
        log.LogPelletPickupOutcome(matchingId, playerId, 201000008, 15, 0, "wasted", false);
        log.LogPelletPickupOutcome(matchingId, playerId, 201000008, 15, 0, "denied_reserved", false);
        log.LogMatchEnded(matchingId, playerId, "last_survivor", "not_required",
            [new MatchFinalPlayerStats(playerId, 1, 30, 0, 0, 0)]);

        var events = log.GetRecent(matchingId, 500);
        var merge = Assert.Single(events, entry => entry.Type == "SURVIVOR_ORB_BOARD_STATE" && entry.Outcome == "merge");
        Assert.Equal([107000010, 107000010], merge.PreviousBoardItemIds);
        Assert.Equal("Blue", merge.EquippedColor);
        Assert.Equal("Red", merge.PreviousEquippedColor);
        Assert.False(merge.ResonanceActive ?? true);
        Assert.True(Assert.Single(events, entry => entry.Type == "SURVIVOR_ORB_MERGE_WINDOW_ENDED")
            .MergeCandidateDurationSeconds > 0d);
        Assert.Contains(events, entry => entry.Type == "SURVIVOR_ORB_RESONANCE_APPLIED" && entry.ResonanceProfile == "sun_single_target");
        Assert.Contains(events, entry => entry.Type == "SURVIVOR_ORB_RESONANCE_REMOVED" && entry.Outcome == "merge");
        var greenVolley = Assert.Single(events, entry => entry.Type == "SURVIVOR_ORB_ATTACK_TARGETS");
        Assert.Equal(3, greenVolley.CandidateTargetCount);
        Assert.Equal([402L], greenVolley.AttackTargetPlayerIds);
        Assert.Equal("wind_multi_target", greenVolley.ResonanceProfile);
        Assert.Contains(events, entry => entry.Type == "SURVIVOR_ORB_COLOR_SUMMARY" &&
            entry.ResonanceColor == "Red" && entry.DurationSeconds > 0d);
        Assert.Equal(1, Assert.Single(events, entry => entry.Type == "SURVIVOR_ORB_SUMMARY").ContributionDelta);
        Assert.Contains(events, entry => entry.Type == "PELLET_PICKUP_OUTCOME" && entry.Outcome == "effective" && entry.RecoveryAmount == 15);
        Assert.Contains(events, entry => entry.Type == "PELLET_PICKUP_OUTCOME" && entry.Outcome == "wasted" && entry.WastedRecoveryAmount == 15);
        Assert.Contains(events, entry => entry.Type == "PELLET_PICKUP_OUTCOME" && entry.Outcome == "denied_reserved");
    }

    [Fact]
    public void OrbBoardTelemetryCapturesFirstPickupFullBoardAndBlockedPickup()
    {
        const long matchingId = 200401;
        const long playerId = 401;
        var log = TestGameEventLogs.Create();
        log.BeginMatch(matchingId, seed: 200);

        log.LogOrbBoardTransition(
            matchingId,
            playerId,
            [new InGameItemInfo { ItemId = 107000010, Count = 1 }],
            107000010,
            "Library",
            "pickup",
            false);
        var fullBoard = new[]
        {
            new InGameItemInfo { ItemId = 107000010, Count = 1 },
            new InGameItemInfo { ItemId = 107000020, Count = 1 },
            new InGameItemInfo { ItemId = 107000030, Count = 1 },
            new InGameItemInfo { ItemId = 107000040, Count = 1 },
            new InGameItemInfo { ItemId = 107000010, Count = 1 },
            new InGameItemInfo { ItemId = 107000020, Count = 1 }
        };
        log.LogOrbBoardTransition(matchingId, playerId, fullBoard, 107000010, "Gym", "pickup", false);
        log.LogOrbPickupBlockedFull(matchingId, playerId, 107000030, "Gym", fullBoard, false);

        var events = log.GetRecent(matchingId, 100);
        var first = Assert.Single(events, entry => entry.Type == "SURVIVOR_ORB_FIRST_PICKUP");
        Assert.Equal(1, first.InventorySlotsUsed);
        Assert.Equal(6, first.InventorySlotCapacity);
        var full = Assert.Single(events, entry => entry.Type == "SURVIVOR_ORB_BOARD_FULL");
        Assert.Equal(6, full.InventorySlotsUsed);
        Assert.NotNull(full.ElapsedMilliseconds);
        var blocked = Assert.Single(events, entry => entry.Type == "SURVIVOR_ORB_PICKUP_BLOCKED_FULL");
        Assert.Equal(107000030, blocked.ItemId);
        Assert.Equal(6, blocked.InventorySlotsUsed);
    }

    private static void InitializeBattleCombatData()
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);
        while (directory != null &&
               !Directory.Exists(Path.Combine(directory.FullName, "network", "Common", "csv")))
        {
            directory = directory.Parent;
        }

        if (directory == null)
            throw new DirectoryNotFoundException("Could not locate repository root from test output path.");

        BattleItemCombatData.Initialize(CsvHelper.LoadCsv(Path.Combine(
            directory.FullName, "network", "Common", "csv", "battle_item_combat.csv")));
    }
}
