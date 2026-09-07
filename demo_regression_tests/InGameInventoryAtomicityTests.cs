using game_server.services;
using network.common.data;
using network.common.data.helpers;

namespace demo_regression_tests;

public sealed class InGameInventoryAtomicityTests
{
    private const int SunOrbT1 = 107000010;
    private const int Bandage = 201000008;
    private const int BandageRecipeId = 193031;

    [Fact]
    public async Task ConcurrentQuantityOneConsumptionCannotOverdraw()
    {
        var manager = MatchTestServices.Inventory();
        manager.AddItem(playerId: 100, itemId: 401000005, count: 1);

        var attempts = await Task.WhenAll(
            Task.Run(() => manager.TryRemoveOneByItemId(100, 401000005, out _)),
            Task.Run(() => manager.TryRemoveOneByItemId(100, 401000005, out _)));

        Assert.Single(attempts, success => success);
        Assert.Single(attempts, success => !success);
        Assert.Equal(0, manager.GetPlayerInventory(100).GetItemCount(401000005));
    }

    [Fact]
    public void QuantityOneConsumptionLeavesRemainingCount()
    {
        var manager = MatchTestServices.Inventory();
        manager.AddItem(playerId: 100, itemId: 401000005, count: 2);

        Assert.True(manager.TryRemoveOneByItemId(100, 401000005, out var updated));
        Assert.NotNull(updated);
        Assert.Equal(1, updated.Count);
        Assert.Equal(1, manager.GetPlayerInventory(100).GetItemCount(401000005));
    }

    [Fact]
    public void EquippedBattleItemIsStoredAndClearedWithInventoryRemoval()
    {
        InitializeBattleCombatData();
        var manager = MatchTestServices.Inventory();
        var item = manager.AddItem(playerId: 100, itemId: 107000003);

        Assert.True(manager.TryEquipBattleItem(100, item.ItemUid, out var equipped));
        Assert.Equal(item.ItemUid, equipped!.ItemUid);
        Assert.Equal(107000003, manager.GetEquippedBattleItem(100)!.ItemId);

        Assert.True(manager.TryRemoveItem(100, item.ItemUid, 1, out _));
        Assert.Null(manager.GetEquippedBattleItem(100));
    }

    [Fact]
    public void CombiningAnEquippedBattleItemKeepsTheOutputEquipped()
    {
        InitializeBattleCombatData();
        var manager = MatchTestServices.Inventory();
        var firstItem = manager.AddItem(playerId: 100, itemId: 107000003);
        manager.AddItem(playerId: 100, itemId: 107000003);

        Assert.True(manager.TryEquipBattleItem(100, firstItem.ItemUid, out _));
        Assert.True(manager.TryCombineItems(100, [107000003, 107000003], 107000004, out var changedItems));

        var outputItem = Assert.Single(changedItems, item => item.ItemId == 107000004);
        var equippedItem = manager.GetEquippedBattleItem(100);
        Assert.NotNull(equippedItem);
        Assert.Equal(outputItem.ItemUid, equippedItem!.ItemUid);
    }

    [Fact]
    public void NonCombatItemCannotBecomeEquippedBattleItem()
    {
        InitializeBattleCombatData();
        var manager = MatchTestServices.Inventory();
        var item = manager.AddItem(playerId: 100, itemId: 401000005);

        Assert.False(manager.TryEquipBattleItem(100, item.ItemUid, out _));
        Assert.Null(manager.GetEquippedBattleItem(100));
    }

    [Fact]
    public void RandomRecipeWithMissingMaterialDoesNotDraw()
    {
        var manager = MatchTestServices.Inventory();
        manager.AddItem(playerId: 100, itemId: Bandage);
        BattleItemRecipe recipe = LoadBattleItemRecipe(BandageRecipeId);
        var random = new CountingRandom();

        bool combined = manager.TryCombineRandomRecipe(
            playerId: 100,
            [recipe],
            random,
            out BattleItemRecipe? selectedRecipe,
            out var changedItems);

        Assert.False(combined);
        Assert.Null(selectedRecipe);
        Assert.Empty(changedItems);
        Assert.Equal(0, random.DrawCount);
        Assert.Equal(1, manager.GetPlayerInventory(100).GetItemCount(Bandage));
    }

    [Fact]
    public void OrbMergeWithMissingMaterialDoesNotDraw()
    {
        InitializeBattleCombatData();
        var manager = MatchTestServices.Inventory();
        manager.AddItem(playerId: 100, itemId: SunOrbT1);
        var random = new CountingRandom();

        bool combined = manager.TryCombineOrbs(
            playerId: 100,
            SunOrbT1,
            SunOrbT1,
            random,
            out int outputItemId,
            out var changedItems);

        Assert.False(combined);
        Assert.Equal(0, outputItemId);
        Assert.Empty(changedItems);
        Assert.Equal(0, random.DrawCount);
        Assert.Equal(1, manager.GetPlayerInventory(100).GetItemCount(SunOrbT1));
    }

    private static void InitializeBattleCombatData()
    {
        BattleItemCombatData.Initialize(CsvHelper.LoadCsv(Path.Combine(
            FindRepositoryRoot(), "network", "Common", "csv", "battle_item_combat.csv")));
    }

    private static BattleItemRecipe LoadBattleItemRecipe(int recipeId)
    {
        return Assert.Single(CsvHelper.LoadCsv(Path.Combine(
                FindRepositoryRoot(), "network", "Common", "csv", "battle_item_recipe.csv"))
            .Where(row => row["recipe_id"] == recipeId.ToString())
            .Select(BattleItemRecipe.CreateFromData));
    }

    private static string FindRepositoryRoot()
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);
        while (directory != null &&
               !Directory.Exists(Path.Combine(directory.FullName, "network", "Common", "csv")))
        {
            directory = directory.Parent;
        }

        return directory?.FullName ??
               throw new DirectoryNotFoundException("Could not locate repository root from test output path.");
    }

    private sealed class CountingRandom : Random
    {
        private int _drawCount;

        public int DrawCount => Volatile.Read(ref _drawCount);

        public override int Next(int maxValue)
        {
            Interlocked.Increment(ref _drawCount);
            return 0;
        }
    }
}
