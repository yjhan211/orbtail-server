using game_server.matches;
using game_server.players;

namespace server_tests;

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
}
