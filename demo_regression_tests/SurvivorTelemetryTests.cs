using game_server.services;
using network.common.data;
using network.common.data.models;

namespace demo_regression_tests;

public sealed class SurvivorTelemetryTests
{
    [Fact]
    public void MatchTelemetrySurvivesCleanupWithSeedExploreRecoveryAndFinalStats()
    {
        const long matchingId = 195001;
        var log = new GameEventLogManager();
        int seed = SurvivorRoyaleSpawnData.GetDeterministicSeed(matchingId);
        var anchor = SurvivorRoyaleSpawnData.GetCorridorAnchors()[0];

        log.BeginMatch(matchingId, seed);
        log.LogSpawnAssignment(
            matchingId, 101, seed, SurvivorRoyaleSpawnData.GetAnchorIndex(anchor),
            anchor.X, anchor.Y, "Corridor1F", isBot: false);
        log.LogExploreStart(matchingId, 101, 77, "Library", isBot: false);
        log.LogExploreCompleted(matchingId, 101, 77, "Library", [107000003], 6, isBot: false);
        log.RecordSurvivorRecovery(matchingId, 101, 15);
        log.LogRecoveryUse(matchingId, 101, 201000008, 15, "inventory_consumable", isBot: false);
        log.LogMatchEnded(
            matchingId,
            101,
            "last_survivor",
            "not_required",
            [new SurvivorFinalPlayerStats(101, 1, 301, 2, 45, 15)]);

        log.Clear(matchingId);

        var events = log.GetRecent(matchingId, 5_000);
        Assert.Contains(events, entry =>
            entry.Type == "MATCH_STARTED" && entry.MatchSeed == seed);
        Assert.Contains(events, entry =>
            entry.Type == "SPAWN_ASSIGNMENT" && entry.SpawnAnchorIndex == 1);
        Assert.Contains(events, entry =>
            entry.Type == "EXPLORE_COMPLETED" &&
            entry.GeneratedItemIds!.SequenceEqual([107000003]) &&
            entry.AreaRemainingStock == 6);
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
        var log = new GameEventLogManager();

        log.LogClosureWarningSnapshot(
            matchingId,
            202,
            ["Library"],
            "Library",
            corruption: 40,
            inventorySlotsUsed: 5,
            inventorySlotCapacity: 6,
            areaRemainingStock: 3,
            closureAtUnixMs: DateTimeOffset.UtcNow.AddSeconds(15).ToUnixTimeMilliseconds(),
            isBot: false);
        log.LogExploreStart(matchingId, 202, 88, "Library", isBot: false);
        log.LogMove(matchingId, 202, "Library", "Corridor1F", isBot: false);
        log.LogMove(matchingId, 202, "Corridor1F", "Library", isBot: false);

        var events = log.GetRecent(matchingId, 5_000);
        var snapshot = Assert.Single(events, entry => entry.Type == "CLOSURE_WARNING_SNAPSHOT");
        Assert.Equal(40, snapshot.Corruption);
        Assert.Equal(5, snapshot.InventorySlotsUsed);
        Assert.Equal(3, snapshot.AreaRemainingStock);

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
        var log = new GameEventLogManager();

        log.LogSurvivorTargetAcquired(matchingId, 301, 302, "Gym", 107000003, 107000004, false, now);
        log.LogSurvivorTierReached(matchingId, 301, 107000004, 2, false, now.AddSeconds(1));
        log.LogSurvivorTierReached(matchingId, 301, 107000005, 3, false, now.AddSeconds(2));
        log.LogSurvivorHit(matchingId, 301, 302, 107000005, 20, true, false, now.AddSeconds(3));
        log.LogOvertimeStageChanged(matchingId, 2, 2);
        log.LogMatchEnded(matchingId, 301, "overtime_settlement", "survival>kills>damage>recovery",
            [new SurvivorFinalPlayerStats(301, 1, 330, 1, 20, 0)]);

        var events = log.GetRecent(matchingId, 5_000);
        Assert.Contains(events, entry => entry.Type == "SURVIVOR_ENCOUNTER_START" && entry.IsFirstMilestone == true);
        Assert.Contains(events, entry => entry.Type == "SURVIVOR_FIRST_T2");
        Assert.Contains(events, entry => entry.Type == "SURVIVOR_FIRST_T3");
        Assert.Contains(events, entry => entry.Type == "SURVIVOR_FIRST_ELIMINATION");
        Assert.Contains(events, entry =>
            entry.Type == "OVERTIME_STAGE_CHANGED" && entry.OvertimeStage == 2);
        Assert.Contains(events, entry =>
            entry.Type == "MATCH_ENDED" &&
            entry.TieBreakCriterion == "survival>kills>damage>recovery");
    }
}
