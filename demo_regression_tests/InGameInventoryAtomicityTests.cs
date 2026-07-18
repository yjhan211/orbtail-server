using game_server.services;
using network.common.data;
using network.common.data.helpers;

namespace demo_regression_tests;

public sealed class InGameInventoryAtomicityTests
{
    [Fact]
    public async Task ConcurrentQuantityOneConsumptionCannotOverdraw()
    {
        var manager = new InGameInventoryManager();
        manager.Initialize();
        manager.AddItem(matchingId: 10, playerId: 100, itemId: 401000005, count: 1);

        var attempts = await Task.WhenAll(
            Task.Run(() => manager.TryRemoveOneByItemId(10, 100, 401000005, out _)),
            Task.Run(() => manager.TryRemoveOneByItemId(10, 100, 401000005, out _)));

        Assert.Single(attempts, success => success);
        Assert.Single(attempts, success => !success);
        Assert.Equal(0, manager.GetPlayerInventory(10, 100).GetItemCount(401000005));
    }

    [Fact]
    public void QuantityOneConsumptionLeavesRemainingCount()
    {
        var manager = new InGameInventoryManager();
        manager.Initialize();
        manager.AddItem(matchingId: 10, playerId: 100, itemId: 401000005, count: 2);

        Assert.True(manager.TryRemoveOneByItemId(10, 100, 401000005, out var updated));
        Assert.NotNull(updated);
        Assert.Equal(1, updated.Count);
        Assert.Equal(1, manager.GetPlayerInventory(10, 100).GetItemCount(401000005));
    }

    [Fact]
    public void EquippedBattleItemIsStoredAndClearedWithInventoryRemoval()
    {
        InitializeBattleCombatData();
        var manager = new InGameInventoryManager();
        manager.Initialize();
        var item = manager.AddItem(matchingId: 10, playerId: 100, itemId: 107000003);

        Assert.True(manager.TryEquipBattleItem(10, 100, item.ItemUid, out var equipped));
        Assert.Equal(item.ItemUid, equipped!.ItemUid);
        Assert.Equal(107000003, manager.GetEquippedBattleItem(10, 100)!.ItemId);

        Assert.True(manager.TryRemoveItem(10, 100, item.ItemUid, 1, out _));
        Assert.Null(manager.GetEquippedBattleItem(10, 100));
    }

    [Fact]
    public void NonCombatItemCannotBecomeEquippedBattleItem()
    {
        InitializeBattleCombatData();
        var manager = new InGameInventoryManager();
        manager.Initialize();
        var item = manager.AddItem(matchingId: 10, playerId: 100, itemId: 401000005);

        Assert.False(manager.TryEquipBattleItem(10, 100, item.ItemUid, out _));
        Assert.Null(manager.GetEquippedBattleItem(10, 100));
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
