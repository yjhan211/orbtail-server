using game_server.matches;
using game_server.players;
using network.common;

namespace demo_regression_tests;

public sealed class InventoryStackingTests
{
    public InventoryStackingTests() => TestGameData.EnsureBattleItemCombatLoaded();
    [Fact]
    public void OrbsKeepSeparateSlotsWithoutRecipes()
    {
        var inventory = new PlayerOrbCollection();
        var first = inventory.AddItem(107000010);
        var second = inventory.AddItem(107000010);
        Assert.NotEqual(first.ItemUid, second.ItemUid);
        Assert.Equal(2, inventory.GetAllItems().Count);
        Assert.All(inventory.GetAllItems(), item => Assert.Equal(1, item.Count));
    }

    [Fact]
    public void CombinationProtocolsAreNotExposed()
    {
        Assert.DoesNotContain("C_TO_G_COMBINE_ITEMS", Enum.GetNames<Protocol>());
        Assert.DoesNotContain("G_TO_C_ITEMS_COMBINED", Enum.GetNames<Protocol>());
    }
}
