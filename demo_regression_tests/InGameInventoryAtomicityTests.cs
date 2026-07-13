using game_server.services;

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
}
