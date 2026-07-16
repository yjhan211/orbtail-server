using game_server.services;
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
    public void RegionalPoolsMatchIssue193Specification()
    {
        var expected = new Dictionary<AreaType, int[]>
        {
            [AreaType.Classroom3] = [107000003, 107000003, 201000008, 201000011],
            [AreaType.Classroom4] = [107000003, 301000038, 201000011],
            [AreaType.ExamRoom] = [107000003, 107000003, 301000039, 201000008],
            [AreaType.BroadcastRoom] = [107000011, 107000011, 301000039, 301000039, 201000011],
            [AreaType.Classroom2] = [201000008, 201000008, 201000018, 201000011],
            [AreaType.Library] = [107000003, 107000011, 301000038, 301000039],
            [AreaType.Gym] = [107000007, 107000007, 107000011, 201000008],
            [AreaType.Storage] = [107000007, 107000007, 301000038, 201000011],
            [AreaType.Storage2] = [107000011, 107000011, 201000011, 201000008],
            [AreaType.Junkyard] = [107000007, 201000008, 201000011],
            [AreaType.Junkyard2] = [107000011, 201000008],
            [AreaType.AdminOffice] = [107000007, 201000011, 201000008],
            [AreaType.StaffRoom] = [107000003, 201000011, 201000011, 301000039],
            [AreaType.Ground] = [107000007, 201000008, 201000011]
        };

        foreach (var (area, items) in expected)
            Assert.Equal(items.Order(), GameInteractableData.GetItemPoolByArea((int)area).Order());

        foreach (var corridor in new[]
                 {
                     AreaType.Corridor, AreaType.Corridor1F, AreaType.Corridor2F,
                     AreaType.Corridor3F, AreaType.Corridor4F
                 })
            Assert.Empty(GameInteractableData.GetItemPoolByArea((int)corridor));
    }

    [Fact]
    public void ExploreConsumesAtMostThreeAndNeverRegeneratesWithinMatch()
    {
        var manager = new AreaItemStockManager();
        manager.InitializeMatching(19301);

        Assert.True(manager.TryConsumeDrops(19301, (int)AreaType.Classroom3, 3, out var first));
        Assert.Equal(3, first.Count);
        Assert.True(manager.TryConsumeDrops(19301, (int)AreaType.Classroom3, 3, out var second));
        Assert.Single(second);
        Assert.False(manager.TryConsumeDrops(19301, (int)AreaType.Classroom3, 3, out var third));
        Assert.Empty(third);

        manager.InitializeMatching(19301);
        Assert.Equal(0, manager.GetRemainingCount(19301, (int)AreaType.Classroom3));
        Assert.Equal(4, manager.GetRemainingCount(19302, (int)AreaType.Classroom3));
    }

    [Fact]
    public async Task ConcurrentExploresCannotConsumeMoreThanRegionalStock()
    {
        var manager = new AreaItemStockManager();
        const long matchId = 19303;
        manager.InitializeMatching(matchId);

        var results = await Task.WhenAll(
            Task.Run(() => Consume(manager, matchId, AreaType.Classroom3)),
            Task.Run(() => Consume(manager, matchId, AreaType.Classroom3)));

        Assert.Equal(4, results.Sum(items => items.Count));
        Assert.Equal(0, manager.GetRemainingCount(matchId, (int)AreaType.Classroom3));
        Assert.All(results, items => Assert.InRange(items.Count, 1, 3));
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

    [Theory]
    [InlineData(201000008, 0, GroundItemPickupDisposition.LeaveOnGround, 15)]
    [InlineData(201000008, 1, GroundItemPickupDisposition.AutoUse, 15)]
    [InlineData(201000018, 30, GroundItemPickupDisposition.AutoUse, 35)]
    [InlineData(201000011, 30, GroundItemPickupDisposition.Store, 0)]
    [InlineData(301000039, 30, GroundItemPickupDisposition.Store, 0)]
    [InlineData(301000038, 30, GroundItemPickupDisposition.Store, 0)]
    public void ConsumablePickupPolicyMatchesSpecification(int itemId, int corruption,
        GroundItemPickupDisposition expected, int expectedRecovery)
    {
        var actual = GroundItemPickupPolicy.Resolve(itemId, corruption, out int recovery);
        Assert.Equal(expected, actual);
        Assert.Equal(expectedRecovery, recovery);
    }

    private static List<int> Consume(AreaItemStockManager manager, long matchId, AreaType area)
    {
        manager.TryConsumeDrops(matchId, (int)area, 3, out var items);
        return items;
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
}