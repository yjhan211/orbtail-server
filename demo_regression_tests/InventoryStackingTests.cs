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
        var inventory = new PlayerOrbState();
        var first = inventory.AddOrb(107000010);
        var second = inventory.AddOrb(107000010);
        Assert.NotEqual(first.ItemUid, second.ItemUid);
        Assert.Equal(2, inventory.GetAllOrbs().Count);
        Assert.All(inventory.GetAllOrbs(), item => Assert.Equal(1, item.Count));
    }

    [Fact]
    public void CombinationProtocolsAreNotExposed()
    {
        Assert.DoesNotContain("C_TO_G_COMBINE_ITEMS", Enum.GetNames<Protocol>());
        Assert.DoesNotContain("G_TO_C_ITEMS_COMBINED", Enum.GetNames<Protocol>());
    }
}
