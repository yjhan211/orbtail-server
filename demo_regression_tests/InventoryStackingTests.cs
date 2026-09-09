using game_server.matches.items;
using network.common;

namespace demo_regression_tests;

public sealed class InventoryStackingTests
{
    public InventoryStackingTests() => TestGameData.EnsureBattleItemCombatLoaded();
    [Fact]
    public void OrbsKeepSeparateSlotsWithoutRecipes()
    {
        var inventory = new PlayerInGameInventory(193);
        var first = inventory.AddItem(107000010);
        var second = inventory.AddItem(107000010);
        Assert.NotEqual(first.ItemUid, second.ItemUid);
        Assert.Equal(2, inventory.GetAllItems().Count);
        Assert.All(inventory.GetAllItems(), item => Assert.Equal(1, item.Count));
    }

    [Theory]
    [InlineData(201000008)]
    [InlineData(201000011)]
    [InlineData(107000040)]
    public void RecoveryItemsStackAndConsumeWithoutCombining(int itemId)
    {
        var inventory = new PlayerInGameInventory(194);
        var first = inventory.AddItem(itemId);
        var second = inventory.AddItem(itemId);
        Assert.Equal(first.ItemUid, second.ItemUid);
        Assert.Equal(2, Assert.Single(inventory.GetAllItems()).Count);
        Assert.True(inventory.TryRemoveOneByItemId(itemId, out var remaining));
        Assert.Equal(1, remaining!.Count);
        Assert.Equal(itemId, remaining.ItemId);
    }

    [Fact]
    public void CombinationProtocolsAreNotExposed()
    {
        Assert.DoesNotContain("C_TO_G_COMBINE_ITEMS", Enum.GetNames<Protocol>());
        Assert.DoesNotContain("G_TO_C_ITEMS_COMBINED", Enum.GetNames<Protocol>());
    }
}
