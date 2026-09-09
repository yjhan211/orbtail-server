using game_server.combat;
using game_server.logging;
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
    public void CapturedTelemetrySurvivesCleanupWithSeedExploreRecoveryAndFinalStats()
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

        var events = log.GetForPersistence(matchingId);
        log.Clear(matchingId);
        Assert.Empty(log.GetRecent(matchingId));
        Assert.Empty(log.GetForPersistence(matchingId));
        Assert.Contains(events, entry =>
            entry.Type == GameEventType.MatchStarted && entry.MatchSeed == seed);
        Assert.Contains(events, entry =>
            entry.Type == GameEventType.SpawnAssignment && entry.SpawnAnchorIndex == 1);
        Assert.Contains(events, entry =>
            entry.Type == GameEventType.RecoveryUsed && entry.RecoveryAmount == 15);
        var ended = Assert.Single(events, entry => entry.Type == GameEventType.MatchEnded);
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
        var snapshot = Assert.Single(events, entry => entry.Type == GameEventType.ClosureWarningSnapshot);
        Assert.Equal(40, snapshot.Health);
        Assert.Equal(5, snapshot.InventorySlotsUsed);

        var exit = Assert.Single(events, entry => entry.Type == GameEventType.ClosureWarningExit);
        Assert.Equal(1, exit.AdditionalExploreCount);
        Assert.NotNull(exit.ExitedAtUnixMs);

        var reentry = Assert.Single(events, entry => entry.Type == GameEventType.ClosureWarningReentry);
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
        Assert.Contains(events, entry => entry.Type == GameEventType.SurvivorFirstT2);
        Assert.Contains(events, entry => entry.Type == GameEventType.SurvivorFirstT3);
        Assert.Contains(events, entry => entry.Type == GameEventType.SurvivorFirstElimination);
        Assert.Contains(events, entry =>
            entry.Type == GameEventType.OvertimeStageChanged && entry.OvertimeStage == 2);
        Assert.Contains(events, entry =>
            entry.Type == GameEventType.MatchEnded &&
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
        Assert.Equal(GameEventType.AfterimageHit, hit.Type);
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
    public void InitialInventoryRecordsConnectionStateWithoutChangingItems()
    {
        var log = TestGameEventLogs.Create();
        var item = new InGameItemInfo { ItemId = 107000010, Count = 1 };

        log.LogInitialInventory(198400, 400, [item], item.ItemId, "Library");

        var entry = Assert.Single(log.GetRecent(198400, 100), entry => entry.Type == GameEventType.SurvivorOrbBoardState);
        Assert.Equal("connection_sync", entry.Outcome);
        Assert.Equal("Library", entry.Area);
        Assert.Equal(107000010, entry.WeaponItemId);
        Assert.Equal([107000010], entry.BoardItemIds);
        Assert.False(entry.IsBot);
        Assert.Equal(1, item.Count);
    }

    [Fact]
    public void OrbBoardTelemetryCapturesTransitionsColorRatesAndVolleyTargets()
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
            [new InGameItemInfo { ItemId = 107000031, Count = 1 }], 107000031, "Gym", "inventory_changed", false);
        var volley = new ProximityCombatAttack(playerId, 402, AreaType.S2Gym1, 107000020, 6, 0.2f, 1f, 3);
        log.LogOrbAttackTargets(matchingId, [volley], [volley],
            new Dictionary<long, OrbColor> { [playerId] = OrbColor.Green });
        log.LogPelletPickupOutcome(matchingId, playerId, 201000008, 15, 15, "effective", false);
        log.LogPelletPickupOutcome(matchingId, playerId, 201000008, 15, 0, "wasted", false);
        log.LogPelletPickupOutcome(matchingId, playerId, 201000008, 15, 0, "denied_reserved", false);
        log.LogMatchEnded(matchingId, playerId, "last_survivor", "not_required",
            [new MatchFinalPlayerStats(playerId, 1, 30, 0, 0, 0)]);

        var events = log.GetRecent(matchingId, 500);
        var transition = Assert.Single(events, entry => entry.Type == GameEventType.SurvivorOrbBoardState && entry.Outcome == "inventory_changed");
        Assert.Equal([107000010, 107000010], transition.PreviousBoardItemIds);
        Assert.Equal("Blue", transition.EquippedColor);
        Assert.Equal("Red", transition.PreviousEquippedColor);
        Assert.False(transition.ResonanceActive ?? true);
        Assert.Contains(events, entry => entry.Type == GameEventType.SurvivorOrbResonanceApplied && entry.ResonanceProfile == "sun_single_target");
        Assert.Contains(events, entry => entry.Type == GameEventType.SurvivorOrbResonanceRemoved && entry.Outcome == "inventory_changed");
        var greenVolley = Assert.Single(events, entry => entry.Type == GameEventType.SurvivorOrbAttackTargets);
        Assert.Equal(3, greenVolley.CandidateTargetCount);
        Assert.Equal([402L], greenVolley.AttackTargetPlayerIds);
        Assert.Equal("wind_multi_target", greenVolley.ResonanceProfile);
        Assert.Contains(events, entry => entry.Type == GameEventType.SurvivorOrbColorSummary &&
            entry.ResonanceColor == "Red" && entry.DurationSeconds > 0d);
        Assert.Equal(1, Assert.Single(events, entry => entry.Type == GameEventType.SurvivorOrbSummary).ContributionDelta);
        Assert.Contains(events, entry => entry.Type == GameEventType.PelletPickupOutcome && entry.Outcome == "effective" && entry.RecoveryAmount == 15);
        Assert.Contains(events, entry => entry.Type == GameEventType.PelletPickupOutcome && entry.Outcome == "wasted" && entry.WastedRecoveryAmount == 15);
        Assert.Contains(events, entry => entry.Type == GameEventType.PelletPickupOutcome && entry.Outcome == "denied_reserved");
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
        var first = Assert.Single(events, entry => entry.Type == GameEventType.SurvivorOrbFirstPickup);
        Assert.Equal(1, first.InventorySlotsUsed);
        Assert.Equal(6, first.InventorySlotCapacity);
        var full = Assert.Single(events, entry => entry.Type == GameEventType.SurvivorOrbBoardFull);
        Assert.Equal(6, full.InventorySlotsUsed);
        Assert.NotNull(full.ElapsedMilliseconds);
        var blocked = Assert.Single(events, entry => entry.Type == GameEventType.SurvivorOrbPickupBlockedFull);
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
