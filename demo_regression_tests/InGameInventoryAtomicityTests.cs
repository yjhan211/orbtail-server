using game_server.services;
using network.common.data;
using network.common.data.helpers;

namespace demo_regression_tests;

public sealed class InGameInventoryAtomicityTests
{
    public InGameInventoryAtomicityTests()
    {
        // 오브 판별에 필요한 데이터를 직접 준비해 다른 테스트의 실행 순서에 의존하지 않는다.
        InitializeBattleCombatData();
    }

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
    public void HighestOrbTierFollowsTheWholeInventory()
    {
        var manager = MatchTestServices.Inventory();
        Assert.Equal(0, manager.GetHighestOrbTier(100));
        var first = manager.AddItem(100, 107000010);
        var highest = manager.AddItem(100, 107000032);
        Assert.Equal(3, manager.GetHighestOrbTier(100));
        Assert.True(manager.TryRemoveItem(100, highest.ItemUid, 1, out _));
        Assert.Equal(1, manager.GetHighestOrbTier(100));
        Assert.True(manager.TryRemoveItem(100, first.ItemUid, 1, out _));
        Assert.Equal(0, manager.GetHighestOrbTier(100));
    }

    [Fact]
    public void NonOrbItemsDoNotAffectFinalOrbTier()
    {
        var manager = MatchTestServices.Inventory();
        manager.AddItem(100, 401000005);
        Assert.Empty(manager.GetPlayerInventory(100).GetOrderedOrbs());
        Assert.Equal(0, manager.GetHighestOrbTier(100));
    }

    private static void InitializeBattleCombatData()
    {
        BattleItemCombatData.Initialize(CsvHelper.LoadCsv(Path.Combine(
            FindRepositoryRoot(), "network", "Common", "csv", "battle_item_combat.csv")));
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

}
