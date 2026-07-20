using game_server.services;
using Microsoft.Extensions.Logging.Abstractions;
using network.common;
using network.common.data;
using network.common.data.helpers;

namespace demo_regression_tests;

public sealed class SurvivorRegionalItemPoolTests
{
    public SurvivorRegionalItemPoolTests()
    {
        GameDataHelper.SetBasePath(FindNetworkBasePath());
        GameDataHelper.Initialize();
    }

    [Fact]
    public void RegionalPoolsMatchRecorderOnlyBaseline()
    {
        var expected = new Dictionary<AreaType, int[]>
        {
            [AreaType.Classroom3] = [107000003, 107000003, 201000008, 201000011, 201000011],
            [AreaType.Classroom4] = [107000003, 201000008, 201000011, 201000008, 201000011],
            [AreaType.ExamRoom] = [107000003, 107000003, 201000011, 201000008],
            [AreaType.BroadcastRoom] = [107000003, 107000003, 201000011],
            [AreaType.Classroom2] = [201000008, 201000008, 201000018, 201000011],
            [AreaType.Library] = [107000003, 107000003, 201000008, 201000008, 201000008, 201000011, 201000011, 201000011],
            [AreaType.Gym] = [107000003, 107000003, 107000003, 201000008, 201000011, 201000008],
            [AreaType.Storage] = [107000003, 107000003, 201000008, 201000011],
            [AreaType.Storage2] = [107000003, 107000003, 201000011, 201000008],
            [AreaType.Junkyard] = [107000003, 201000008, 201000011],
            [AreaType.Junkyard2] = [107000003, 201000008, 201000011],
            [AreaType.AdminOffice] = [107000003, 201000011, 201000008, 201000011, 201000008],
            [AreaType.StaffRoom] = [107000003, 201000011, 201000011, 201000011, 201000008, 201000008, 201000008],
            [AreaType.Ground] = [107000003, 201000008, 201000011]
        };

        foreach (var (area, items) in expected)
            Assert.Equal(items.Order(), GameInteractableData.GetItemPoolByArea((int)area).Order());

        var naturalBattleItems = expected.Keys
            .SelectMany(area => GameInteractableData.GetItemPoolByArea((int)area))
            .Where(BattleItemCombatData.IsCombatItem)
            .ToArray();
        Assert.Equal(21, naturalBattleItems.Length);
        Assert.All(naturalBattleItems, itemId => Assert.Equal(107000003, itemId));

        Assert.DoesNotContain(301000038, GameInteractableData.GetAllAreaItemPoolItems());
        Assert.DoesNotContain(301000039, GameInteractableData.GetAllAreaItemPoolItems());

        foreach (var corridor in new[]
                 {
                     AreaType.Corridor, AreaType.Corridor1F, AreaType.Corridor2F,
                     AreaType.Corridor3F, AreaType.Corridor4F
                 })
            Assert.Empty(GameInteractableData.GetItemPoolByArea((int)corridor));
    }

    [Fact]
    public void RegionalStockCoversEveryActiveExploreMarkerAtLeastOnce()
    {
        var activeMarkerCounts = new Dictionary<AreaType, int>
        {
            [AreaType.Junkyard] = 2,
            [AreaType.AdminOffice] = 5,
            [AreaType.StaffRoom] = 7,
            [AreaType.Gym] = 6,
            [AreaType.Storage] = 3,
            [AreaType.Storage2] = 3,
            [AreaType.Junkyard2] = 3,
            [AreaType.Classroom2] = 4,
            [AreaType.Library] = 8,
            [AreaType.Classroom3] = 5,
            [AreaType.ExamRoom] = 4,
            [AreaType.Classroom4] = 5,
            [AreaType.BroadcastRoom] = 3,
            [AreaType.Ground] = 3,
        };

        foreach (var (area, markerCount) in activeMarkerCounts)
        {
            int stockCount = GameInteractableData.GetItemPoolByArea((int)area).Count;
            Assert.True(
                stockCount >= markerCount,
                $"{area}: stock={stockCount}, active markers={markerCount}");
        }
    }

    [Fact]
    public void ExploreConsumesExactlyOneAndNeverRegeneratesWithinMatch()
    {
        var manager = new AreaItemStockManager();
        manager.InitializeMatching(19301);

        for (int i = 0; i < 5; i++)
        {
            Assert.True(manager.TryConsumeDrops(19301, (int)AreaType.Classroom3, 1, out var drop));
            Assert.Single(drop);
        }

        Assert.False(manager.TryConsumeDrops(19301, (int)AreaType.Classroom3, 1, out var exhausted));
        Assert.Empty(exhausted);

        manager.InitializeMatching(19301);
        Assert.Equal(0, manager.GetRemainingCount(19301, (int)AreaType.Classroom3));
        Assert.Equal(5, manager.GetRemainingCount(19302, (int)AreaType.Classroom3));
    }

    [Fact]
    public void SuccessfulExploreReturnsOneItemAndExhaustedExploreReturnsNone()
    {
        var manager = new AreaItemStockManager();
        const long matchId = 19308;

        for (int i = 0; i < 5; i++)
        {
            Assert.True(manager.TryConsumeDrop(matchId, (int)AreaType.Classroom3, out int itemId));
            Assert.NotEqual(0, itemId);
        }

        Assert.False(manager.TryConsumeDrop(matchId, (int)AreaType.Classroom3, out int exhaustedItemId));
        Assert.Equal(0, exhaustedItemId);
    }

    [Fact]
    public void FirstHumanExploreDropsCompassThenReturnsToRegionalStock()
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
        RngCollectCore.ClearMatching(matchId);

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

            Assert.Equal(107000015, Assert.Single(first.DroppedItemIds));
            Assert.Equal(107000003, Assert.Single(second.DroppedItemIds));
            Assert.Equal(107000015, Assert.Single(otherPlayerFirst.DroppedItemIds));
            Assert.Equal(initialStock - 1,
                areaStockManager.GetRemainingCount(matchId, (int)AreaType.Classroom3));
        }
        finally
        {
            RngCollectCore.ClearMatching(matchId);
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
        Assert.Equal(5, manager.GetRemainingCount(matchId, (int)AreaType.Classroom3));
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

        Assert.Equal(5, results.Sum(items => items.Count));
        Assert.Equal(0, manager.GetRemainingCount(matchId, (int)AreaType.Classroom3));
        Assert.Equal(5, results.Count(items => items.Count == 1));
        Assert.Equal(3, results.Count(items => items.Count == 0));
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
        var itemIds = removed.SelectMany(item => Enumerable.Repeat(item.ItemId, item.Count)).ToList();
        var spawned = ground.SpawnItems(19307, AreaType.Corridor, 3f, 4f, itemIds);

        Assert.Equal(3, spawned.Count);
        Assert.Equal(new[] { 107000003, 201000011, 201000011 }, spawned.Select(item => item.ItemId).Order());
        Assert.Equal(3, ground.GetSnapshot(19307, AreaType.Corridor).Count);
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
    [InlineData(201000008, 100, 0, GroundItemPickupDisposition.Store, 0, 15)]
    [InlineData(201000008, 100, 1, GroundItemPickupDisposition.Store, 0, 15)]
    [InlineData(201000018, 100, 30, GroundItemPickupDisposition.Store, 0, 35)]
    [InlineData(201000011, 100, 30, GroundItemPickupDisposition.Store, 15, 0)]
    [InlineData(201000011, 99, 30, GroundItemPickupDisposition.Store, 15, 0)]
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
