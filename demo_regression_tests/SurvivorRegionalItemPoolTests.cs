using game_server.services;
using Microsoft.Extensions.Logging.Abstractions;
using network.common;
using network.common.data;
using network.common.data.helpers;
using network.common.data.models;

namespace demo_regression_tests;

public sealed class SurvivorRegionalItemPoolTests
{
    public SurvivorRegionalItemPoolTests()
    {
        GameDataHelper.SetBasePath(FindNetworkBasePath());
        GameDataHelper.Initialize();
    }

    [Fact]
    public void RegionalPoolsMatchColorSupplyPlan()
    {
        var expected = new Dictionary<AreaType, int[]>
        {
            [AreaType.Classroom3] = [107000020],
            [AreaType.Classroom4] = [107000010],
            [AreaType.ExamRoom] = [107000010],
            [AreaType.BroadcastRoom] = [107000010],
            [AreaType.Classroom2] = [107000040, 107000030],
            [AreaType.Library] = [107000010],
            [AreaType.Gym] = [107000020, 107000010],
            [AreaType.Storage] = [107000020],
            [AreaType.Storage2] = [107000010, 107000030],
            [AreaType.Junkyard] = [107000030, 107000040],
            [AreaType.Junkyard2] = [107000030, 107000020],
            [AreaType.AdminOffice] = [107000040],
            [AreaType.StaffRoom] = [107000030],
            [AreaType.Ground] = [107000020, 107000040]
        };
        foreach (var (area, items) in expected)
            Assert.Equal(items.Order(), GameInteractableData.GetItemPoolByArea((int)area).Order());

        var naturalBattleItems = expected.Keys
            .SelectMany(area => GameInteractableData.GetItemPoolByArea((int)area))
            .Where(BattleItemCombatData.IsCombatItem)
            .ToArray();
        Assert.Equal(16, naturalBattleItems.Length);
        Assert.Equal(6, naturalBattleItems.Count(itemId => itemId == 107000010));
        Assert.Equal(5, naturalBattleItems.Count(itemId => itemId == 107000020));
        Assert.Equal(5, naturalBattleItems.Count(itemId => itemId == 107000030));
        Assert.Equal(4, expected.Keys.SelectMany(area => GameInteractableData.GetItemPoolByArea((int)area))
            .Count(itemId => itemId == 107000040));

        Assert.All(expected.Keys, area =>
            Assert.True(GameInteractableData.GetItemPoolByArea((int)area).Distinct().Count() <= 3,
                $"{area} exposes more than three natural orb types."));

        Assert.DoesNotContain(301000038, GameInteractableData.GetAllAreaItemPoolItems());
        Assert.DoesNotContain(301000039, GameInteractableData.GetAllAreaItemPoolItems());

        foreach (var corridor in new[]
                 {
                     AreaType.Corridor, AreaType.Corridor1F, AreaType.Corridor2F,
                     AreaType.Corridor3F, AreaType.Corridor4F
                 })
            Assert.Empty(GameInteractableData.GetItemPoolByArea((int)corridor));
    }

    [Fact(Skip = "#219 클론 맵 전환: 옛 학교 지형 전제 — 클론 데이터 스택(벽·연결·문) 완성 후 재작성")]
    public void EveryPlayableRegionStartsWithAtLeastOneOrb()
    {
        var playableAreas = GameMapData.GetAreas(MapId.School)
            .Select(region => region.AreaType)
            .Where(area => area != AreaType.None && !area.IsCorridor())
            .Distinct()
            .ToArray();

        Assert.Equal(20, playableAreas.Sum(area => GameInteractableData.GetItemPoolByArea((int)area).Count));
        Assert.All(playableAreas, area =>
            Assert.True(GameInteractableData.GetItemPoolByArea((int)area).Count >= 1, $"{area} starts empty."));
    }

    [Fact]
    public void MonsterSummonEconomyDisablesNaturalExploreLootWithoutDeletingLegacyPools()
    {
        var manager = new AreaItemStockManager(new ZeroRandom(), naturalExploreLootEnabled: false);
        const long matchId = 20201;
        manager.InitializeMatching(matchId);

        Assert.False(manager.HasRemaining(matchId, (int)AreaType.Classroom3));
        Assert.Equal(0, manager.GetRemainingCount(matchId, (int)AreaType.Classroom3));
        Assert.False(manager.TryConsumeDrop(matchId, (int)AreaType.Classroom3, out int itemId));
        Assert.Equal(0, itemId);
        Assert.Empty(manager.GetRemainingSnapshot(matchId, (int)AreaType.Classroom3));

        var publicState = manager.GetPublicDepletionSnapshot(matchId)
            .Single(state => state.AreaType == AreaType.Classroom3);
        Assert.True(publicState.IsDepleted);
        Assert.Empty(publicState.AvailableOrbColors);

        Assert.NotEmpty(GameInteractableData.GetItemPoolByArea((int)AreaType.Classroom3));
    }

    [Fact]
    public void ExploreConsumesExactlyOneAndNeverRegeneratesWithinMatch()
    {
        var manager = new AreaItemStockManager();
        manager.InitializeMatching(19301);
        int initialStock = manager.GetRemainingCount(19301, (int)AreaType.Classroom3);
        Assert.Equal(1, initialStock);

        for (int i = 0; i < initialStock; i++)
        {
            Assert.True(manager.TryConsumeDrops(19301, (int)AreaType.Classroom3, 1, out var drop));
            Assert.Single(drop);
        }

        Assert.False(manager.TryConsumeDrops(19301, (int)AreaType.Classroom3, 1, out var exhausted));
        Assert.Empty(exhausted);

        manager.InitializeMatching(19301);
        Assert.Equal(0, manager.GetRemainingCount(19301, (int)AreaType.Classroom3));
        Assert.Equal(initialStock, manager.GetRemainingCount(19302, (int)AreaType.Classroom3));
    }
    [Fact]
    public void PublicStockSnapshotExposesCurrentOrbColors()
    {
        var manager = new AreaItemStockManager(new ZeroRandom());
        const long matchId = 19307;
        manager.InitializeMatching(matchId);

        var initial = manager.GetPublicDepletionSnapshot(matchId)
            .Single(state => state.AreaType == AreaType.Classroom3);
        Assert.False(initial.IsDepleted);
        Assert.Equal([SurvivorOrbColor.Green], initial.AvailableOrbColors);

        Assert.True(manager.TryConsumeDrop(matchId, (int)AreaType.Classroom3, out int itemId));
        Assert.Equal(107000020, itemId);
        var depleted = manager.GetPublicDepletionSnapshot(matchId)
            .Single(state => state.AreaType == AreaType.Classroom3);
        Assert.True(depleted.IsDepleted);
        Assert.Empty(depleted.AvailableOrbColors);
    }
    [Fact]
    public void PublicStockSnapshotIncludesRecoveryOrbAvailability()
    {
        var manager = new AreaItemStockManager(new ZeroRandom());
        const long matchId = 19312;
        manager.InitializeMatching(matchId);

        var initial = manager.GetPublicDepletionSnapshot(matchId)
            .Single(state => state.AreaType == AreaType.Classroom2);
        Assert.Equal([SurvivorOrbColor.Blue, SurvivorOrbColor.Recovery], initial.AvailableOrbColors.Order());

        Assert.True(manager.TryConsumeDrop(matchId, (int)AreaType.Classroom2, out int itemId));
        Assert.Equal(107000040, itemId);
        var afterRecovery = manager.GetPublicDepletionSnapshot(matchId)
            .Single(state => state.AreaType == AreaType.Classroom2);
        Assert.Equal([SurvivorOrbColor.Blue], afterRecovery.AvailableOrbColors);
    }

    [Fact]
    public void SuccessfulExploreReturnsOneItemAndExhaustedExploreReturnsNone()
    {
        var manager = new AreaItemStockManager();
        const long matchId = 19308;

        Assert.True(manager.TryConsumeDrop(matchId, (int)AreaType.Classroom3, out int itemId));
        Assert.NotEqual(0, itemId);
        Assert.False(manager.TryConsumeDrop(matchId, (int)AreaType.Classroom3, out int exhaustedItemId));
        Assert.Equal(0, exhaustedItemId);
    }
    [Fact]
    public void HumanExploresUseRegionalStockFromFirstDrop()
    {
        const long matchId = 195015;
        const long firstPlayerId = 1501;
        const long secondPlayerId = 1502;
        var info = GameInteractableData.GetByZone((int)AreaType.Classroom3)
            .First(candidate => candidate.InteractionType == InteractionType.RNG_COLLECT);
        var missionManager = new MissionManager(NullLogger.Instance);
        var inventoryManager = new InGameInventoryManager();
        var itemPoolManager = new ItemPoolManager();
        var areaStockManager = new AreaItemStockManager(new ZeroRandom());
        inventoryManager.Initialize();
        itemPoolManager.Initialize();
        areaStockManager.InitializeMatching(matchId);
        missionManager.InitializePlayer(matchId, firstPlayerId, JobTitle.SCIENCE_MEMBER);
        missionManager.InitializePlayer(matchId, secondPlayerId, JobTitle.SCIENCE_MEMBER);
        try
        {
            int initialStock = areaStockManager.GetRemainingCount(matchId, (int)AreaType.Classroom3);
            var first = RngCollectCore.Resolve(
                matchId, firstPlayerId, JobTitle.SCIENCE_MEMBER, info,
                missionManager, inventoryManager, itemPoolManager, areaStockManager, isBot: false);
            var second = RngCollectCore.Resolve(
                matchId, firstPlayerId, JobTitle.SCIENCE_MEMBER, info,
                missionManager, inventoryManager, itemPoolManager, areaStockManager, isBot: false);
            var otherPlayerFirst = RngCollectCore.Resolve(
                matchId, secondPlayerId, JobTitle.SCIENCE_MEMBER, info,
                missionManager, inventoryManager, itemPoolManager, areaStockManager, isBot: false);

            Assert.Equal(107000020, Assert.Single(first.DroppedItemIds));
            Assert.Empty(second.DroppedItemIds);
            Assert.Empty(otherPlayerFirst.DroppedItemIds);
            Assert.Equal(initialStock - 1,
                areaStockManager.GetRemainingCount(matchId, (int)AreaType.Classroom3));
        }
        finally
        {
            RngCollectCooldownStore.ClearMatching(matchId);
        }
    }

    [Fact]
    public void RemovingMatchStateAllowsFreshStockForReusedMatchId()
    {
        var manager = new AreaItemStockManager();
        const long matchId = 19309;

        while (manager.TryConsumeDrop(matchId, (int)AreaType.Classroom3, out _))
        {
        }

        Assert.Equal(0, manager.GetRemainingCount(matchId, (int)AreaType.Classroom3));
        manager.RemoveMatchingState(matchId);
        manager.InitializeMatching(matchId);
        Assert.Equal(1, manager.GetRemainingCount(matchId, (int)AreaType.Classroom3));
    }

    [Fact]
    public void CooldownIsSharedPerMatchAndInteractableButNotAcrossInteractables()
    {
        const long matchId = 19310;
        const int firstInteractId = 701000054;
        const int otherInteractId = 701000060;
        RngCollectCooldownStore.ClearMatching(matchId);

        try
        {
            Assert.Equal(30, RngCollectCooldownStore.DefaultCooldownSeconds);
            Assert.True(RngCollectCooldownStore.TryAcquireCooldown(
                matchId, firstInteractId, RngCollectCooldownStore.DefaultCooldownSeconds, out int firstRemaining));
            Assert.Equal(0, firstRemaining);

            Assert.False(RngCollectCooldownStore.TryAcquireCooldown(
                matchId, firstInteractId, RngCollectCooldownStore.DefaultCooldownSeconds, out int sharedRemaining));
            Assert.InRange(sharedRemaining, 1, RngCollectCooldownStore.DefaultCooldownSeconds);

            Assert.True(RngCollectCooldownStore.TryAcquireCooldown(
                matchId, otherInteractId, RngCollectCooldownStore.DefaultCooldownSeconds, out int otherRemaining));
            Assert.Equal(0, otherRemaining);
        }
        finally
        {
            RngCollectCooldownStore.ClearMatching(matchId);
        }
    }

    [Fact]
    public async Task ConcurrentExploresCannotConsumeMoreThanRegionalStock()
    {
        var manager = new AreaItemStockManager();
        const long matchId = 19303;
        manager.InitializeMatching(matchId);

        var results = await Task.WhenAll(Enumerable.Range(0, 8)
            .Select(_ => Task.Run(() => Consume(manager, matchId, AreaType.Classroom3))));

        Assert.Equal(1, results.Sum(items => items.Count));
        Assert.Equal(0, manager.GetRemainingCount(matchId, (int)AreaType.Classroom3));
        Assert.Equal(1, results.Count(items => items.Count == 1));
        Assert.Equal(7, results.Count(items => items.Count == 0));
    }

    [Fact]
    public void ClosureWarningReplenishesFourDistinctSafeRegionsOnce()
    {
        var manager = new AreaItemStockManager(new ZeroRandom());
        const long matchId = 19311;
        manager.InitializeMatching(matchId);

        var added = manager.ReplenishForClosureWarning(
            matchId,
            90_000,
            [AreaType.ExamRoom, AreaType.BroadcastRoom, AreaType.Classroom2],
            []);

        Assert.Equal(4, added.Count);
        Assert.Equal(4, added.Select(entry => entry.AreaType).Distinct().Count());
        Assert.Equal(
            [107000010, 107000020, 107000030, 107000040],
            added.Select(entry => entry.ItemId).Order());
        Assert.Empty(manager.ReplenishForClosureWarning(
            matchId,
            90_000,
            [AreaType.ExamRoom, AreaType.BroadcastRoom, AreaType.Classroom2],
            []));
    }
    [Fact]
    public void ClosureWarningNeverExpandsAnAreaBeyondThreeOrbTypes()
    {
        var manager = new AreaItemStockManager(new ZeroRandom());
        const long matchId = 19313;
        manager.InitializeMatching(matchId);

        var protectedAreas = Enum.GetValues<AreaType>()
            .Where(area => area != AreaType.None && !area.IsCorridor())
            .Except([AreaType.StaffRoom, AreaType.Gym, AreaType.Library, AreaType.Ground])
            .ToArray();
        var added = manager.ReplenishForClosureWarning(matchId, 90_001, protectedAreas, []);

        Assert.Equal(4, added.Count);
        foreach (var area in added.Select(entry => entry.AreaType))
        {
            var colors = manager.GetPublicDepletionSnapshot(matchId)
                .Single(state => state.AreaType == area)
                .AvailableOrbColors;
            Assert.True(colors.Distinct().Count() <= 3, $"{area} exceeded the three-type cap.");
        }
    }

    [Fact]
    public async Task ConcurrentPickupAllowsOnlyOneWinnerAndPersistsForReconnect()
    {
        var manager = new GroundItemManager();
        const long matchId = 19304;
        manager.InitializeMatching(matchId);
        var spawned = Assert.Single(manager.SpawnItems(matchId, AreaType.Classroom3, 10f, 20f, [107000003]));

        var attempts = await Task.WhenAll(
            Task.Run(() => manager.TryClaim(matchId, spawned.GroundItemUid, 1001, AreaType.Classroom3,
                spawned.PositionX, spawned.PositionY, _ => true, out _)),
            Task.Run(() => manager.TryClaim(matchId, spawned.GroundItemUid, 1001, AreaType.Classroom3,
                spawned.PositionX, spawned.PositionY, _ => true, out _)));

        Assert.Single(attempts, result => result == GroundItemClaimStatus.Success);
        Assert.Single(attempts, result => result == GroundItemClaimStatus.NotFound);
        Assert.Empty(manager.GetSnapshot(matchId, AreaType.Classroom3));

        var remaining = manager.SpawnItems(matchId, AreaType.Classroom3, 1f, 2f, [201000011]);
        manager.InitializeMatching(matchId);
        Assert.Equal(remaining.Select(item => item.GroundItemUid),
            manager.GetSnapshot(matchId, AreaType.Classroom3).Select(item => item.GroundItemUid));
        Assert.Empty(manager.GetSnapshot(matchId + 1, AreaType.Classroom3));
    }

    [Fact]
    public void SummonStonePickupAllowsMovementReplicationTolerance()
    {
        var manager = new GroundItemManager();
        const long matchId = 19313;
        var item = Assert.Single(manager.SpawnItems(
            matchId,
            AreaType.Corridor,
            5f,
            5f,
            [Config.SUMMON_STONE_GROUND_ITEM_ID]));

        Assert.Equal(GroundItemClaimStatus.Success,
            manager.TryClaim(
                matchId,
                item.GroundItemUid,
                7001,
                AreaType.Corridor,
                item.PositionX + 1.5f,
                item.PositionY,
                _ => true,
                out _));
    }

    [Fact]
    public void DroppedItemCannotBeReclaimedUntilOwnerLeavesRadius()
    {
        var manager = new GroundItemManager();
        const long matchId = 19306;
        const long ownerId = 6001;
        var item = Assert.Single(manager.SpawnItems(matchId, AreaType.Corridor, 5f, 5f, [201000011], ownerId));

        Assert.Equal(GroundItemClaimStatus.SourceBlocked,
            manager.TryClaim(matchId, item.GroundItemUid, ownerId, AreaType.Corridor,
                item.PositionX, item.PositionY, _ => true, out _));

        manager.ReleaseSourcePickupBlocks(matchId, ownerId, AreaType.Corridor, 8f, 5f);
        Assert.Equal(GroundItemClaimStatus.Success,
            manager.TryClaim(matchId, item.GroundItemUid, ownerId, AreaType.Corridor,
                item.PositionX, item.PositionY, _ => true, out _));
    }

    [Fact]
    public void EliminatedInventoryCanBeMovedToCorridorGroundPool()
    {
        var inventory = new InGameInventoryManager();
        inventory.Initialize();
        inventory.AddItem(19307, 7001, 107000003, 1);
        inventory.AddItem(19307, 7001, 201000011, 2);

        var removed = inventory.TakeAllItems(19307, 7001);
        Assert.Empty(inventory.GetAllItems(19307, 7001));

        var ground = new GroundItemManager();
        var itemIds = removed
            .SelectMany(item => Enumerable.Repeat(item.ItemId, item.Count))
            .Where(GroundItemPickupPolicy.ShouldDropOnElimination)
            .ToList();
        var spawned = ground.SpawnItems(19307, AreaType.Corridor, 3f, 4f, itemIds);

        Assert.Single(spawned);
        Assert.Equal(107000003, spawned[0].ItemId);
        Assert.Single(ground.GetSnapshot(19307, AreaType.Corridor));
    }
    [Fact]
    public void EliminationScatterSeparatesLootBeyondOneAutoPickupCircleWhenTheAreaAllowsIt()
    {
        var region = GameMapData.GetAreas(MapId.School)
            .Where(area => area.AreaType != AreaType.None)
            .OrderByDescending(area => (area.End.X - area.Start.X + 1) * (area.End.Y - area.Start.Y + 1))
            .First();
        var center = new Cell((region.Start.X + region.End.X) / 2, (region.Start.Y + region.End.Y) / 2);
        float originX = (center.X - center.Y) / 2f;
        float originY = (center.X + center.Y) / 4f;
        var ground = new GroundItemManager();

        var spawned = ground.SpawnItems(19308, region.AreaType, originX, originY,
            [107000010, 107000020, 107000030], layout: GroundItemSpawnLayout.EliminationScatter);

        Assert.Equal(3, spawned.Count);
        Assert.All(spawned, item =>
        {
            float dx = item.PositionX - originX;
            float dy = item.PositionY - originY;
            Assert.True(MathF.Sqrt(dx * dx + dy * dy) >= GroundItemManager.PickupRadius * 2f);
        });
    }
    [Fact]
    public void InventoryCapacityDoesNotReplaceExistingItems()
    {
        var manager = new InGameInventoryManager();
        manager.Initialize();
        for (int i = 0; i < 6; i++)
            Assert.True(manager.TryAddItemWithCapacity(19305, 1, 107000003 + i, 6, out _));

        Assert.False(manager.TryAddItemWithCapacity(19305, 1, 301000039, 6, out _));
        Assert.Equal(6, manager.GetAllItems(19305, 1).Count);
        Assert.DoesNotContain(manager.GetAllItems(19305, 1), item => item.ItemId == 301000039);
    }

    [Fact]
    public void FullInventoryRejectsPickupAndLeavesGroundItemInPlace()
    {
        var inventory = new InGameInventoryManager();
        inventory.Initialize();
        const long matchId = 19311;
        const long playerId = 8101;
        for (int i = 0; i < 6; i++)
            Assert.True(inventory.TryAddItemWithCapacity(matchId, playerId, 107000003 + i, 6, out _));

        var ground = new GroundItemManager();
        var spawned = Assert.Single(ground.SpawnItems(
            matchId, AreaType.Classroom3, 4f, 5f, [301000039]));

        var status = ground.TryClaim(
            matchId,
            spawned.GroundItemUid,
            playerId,
            AreaType.Classroom3,
            spawned.PositionX,
            spawned.PositionY,
            item => inventory.TryAddItemWithCapacity(matchId, playerId, item.ItemId, 6, out _),
            out _);

        Assert.Equal(GroundItemClaimStatus.Rejected, status);
        Assert.Contains(
            ground.GetSnapshot(matchId, AreaType.Classroom3),
            item => item.GroundItemUid == spawned.GroundItemUid);
        Assert.Equal(6, inventory.GetAllItems(matchId, playerId).Count);
    }

    [Theory]
    [InlineData(201000008, 100, 0, GroundItemPickupDisposition.AutoUse, 0, 15)]
    [InlineData(201000008, 100, 1, GroundItemPickupDisposition.AutoUse, 0, 15)]
    [InlineData(201000018, 100, 30, GroundItemPickupDisposition.Store, 0, 35)]
    [InlineData(201000011, 100, 30, GroundItemPickupDisposition.AutoUse, 15, 0)]
    [InlineData(201000011, 99, 30, GroundItemPickupDisposition.AutoUse, 15, 0)]
    [InlineData(301000039, 30, 30, GroundItemPickupDisposition.Store, 0, 0)]
    [InlineData(301000038, 30, 30, GroundItemPickupDisposition.Store, 0, 0)]
    public void ConsumablePickupPolicyMatchesSpecification(int itemId, int stamina, int corruption,
        GroundItemPickupDisposition expected, int expectedStaminaRecovery, int expectedCorruptionRecovery)
    {
        var actual = GroundItemPickupPolicy.Resolve(
            itemId,
            stamina,
            100,
            corruption,
            out int staminaRecovery,
            out int corruptionRecovery);
        Assert.Equal(expected, actual);
        Assert.Equal(expectedStaminaRecovery, staminaRecovery);
        Assert.Equal(expectedCorruptionRecovery, corruptionRecovery);
    }

    [Theory]
    [InlineData(201000008, false)]
    [InlineData(201000011, false)]
    [InlineData(201000018, true)]
    [InlineData(201000019, true)]
    [InlineData(201000020, true)]
    [InlineData(107000010, true)]
    public void EliminationDropPolicyExcludesOnlyImmediateUseConsumables(int itemId, bool expected)
    {
        Assert.Equal(expected, GroundItemPickupPolicy.ShouldDropOnElimination(itemId));
    }

    [Fact]
    public void SpecialRewardPoolsExactlyMatchObjectActionReferences()
    {
        string csvRoot = Path.Combine(FindRepositoryRoot(), "network", "Common", "csv");
        var referencedPoolIds = CsvHelper.LoadCsv(Path.Combine(csvRoot, "object_action.csv"))
            .Where(row => row["result_type"] == "1")
            .Select(row => int.Parse(row["result_id"]))
            .ToHashSet();
        var definedPoolIds = CsvHelper.LoadCsv(Path.Combine(csvRoot, "interactable_item_pool.csv"))
            .Select(row => int.Parse(row["id"]))
            .ToHashSet();

        Assert.Equal(referencedPoolIds.Order(), definedPoolIds.Order());
    }

    [Fact]
    public void SurvivorExploreCsvMirrorsAreByteIdentical()
    {
        string repositoryRoot = FindRepositoryRoot();
        foreach (string fileName in new[]
                 {
                     "interactable_info.csv",
                     "area_item_pool.csv",
                     "interactable_item_pool.csv",
                     "object_action.csv"
                 })
        {
            byte[] canonical = File.ReadAllBytes(
                Path.Combine(repositoryRoot, "network", "Common", "csv", fileName));
            Assert.Equal(canonical, File.ReadAllBytes(Path.Combine(
                repositoryRoot, "client", "Assets", "Resources", "Common", "csv", fileName)));
            Assert.Equal(canonical, File.ReadAllBytes(Path.Combine(
                repositoryRoot, "client", "Assets", "StreamingAssets", "Common", "csv", fileName)));
        }
    }

    [Fact]
    public void EveryOrbColorHasAnOpenLegacySupplyRegionBeforeTheFinalRoomClosureWave()
    {
        var closure = new AreaClosureManager(NullLogger.Instance, new MatchingConfigService(null!, NullLogger.Instance));
        var state = closure.InitializeMatching(198501);
        var closed = new HashSet<AreaType>();
        var colors = new[] { SurvivorOrbColor.Red, SurvivorOrbColor.Green, SurvivorOrbColor.Blue };

        int finalRoomClosureWaveIndex = state.Waves.Count - 1;
        while (finalRoomClosureWaveIndex >= 0 &&
               state.Waves[finalRoomClosureWaveIndex].Areas.All(area => area.IsCorridor()))
            finalRoomClosureWaveIndex--;

        Assert.True(finalRoomClosureWaveIndex >= 0);
        for (int waveIndex = 0; waveIndex < finalRoomClosureWaveIndex; waveIndex++)
        {
            foreach (var area in state.Waves[waveIndex].Areas)
                closed.Add(area);

            foreach (var color in colors)
            {
                bool hasOpenSupply = GameMapData.GetAreas(MapId.School)
                    .Select(region => region.AreaType)
                    .Where(area => !closed.Contains(area))
                    .SelectMany(area => GameInteractableData.GetItemPoolByArea((int)area))
                    .Any(itemId => SurvivorOrbData.TryGetColorAndTier(itemId, out var itemColor, out _) && itemColor == color);
                Assert.True(hasOpenSupply, $"{color} has no open replacement region after wave {waveIndex + 1}.");
            }
        }
    }
    private static List<int> Consume(AreaItemStockManager manager, long matchId, AreaType area)
    {
        manager.TryConsumeDrops(matchId, (int)area, 1, out var items);
        return items;
    }

    private sealed class ZeroRandom : Random
    {
        public override int Next(int maxValue) => 0;
    }

    private static string FindNetworkBasePath()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir != null)
        {
            string candidate = Path.Combine(dir.FullName, "network", "Common", "csv");
            if (Directory.Exists(candidate)) return Path.Combine(dir.FullName, "network");
            dir = dir.Parent;
        }
        throw new DirectoryNotFoundException("Could not locate network/Common/csv.");
    }

    private static string FindRepositoryRoot()
    {
        return Directory.GetParent(FindNetworkBasePath())!.FullName;
    }
}
