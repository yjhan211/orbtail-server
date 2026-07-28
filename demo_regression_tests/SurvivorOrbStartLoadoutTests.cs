using game_server.services;
using network.common.data;

namespace demo_regression_tests;

public sealed class SurvivorOrbStartLoadoutTests
{
    [Fact]
    public void GrantsExactlyOneAttackOrbAndDoesNotDuplicateOnReconnect()
    {
        const long matchingId = 202;
        const long playerId = 20201;
        var inventory = new InGameInventoryManager();
        inventory.Initialize();

        int grantedItemId = SurvivorOrbStartLoadout.EnsureStartingOrb(inventory, matchingId, playerId);
        int reconnectGrant = SurvivorOrbStartLoadout.EnsureStartingOrb(inventory, matchingId, playerId);
        var playerInventory = inventory.GetPlayerInventory(matchingId, playerId);

        Assert.Contains(grantedItemId, new[] { 107000010, 107000020, 107000030 });
        Assert.Equal(0, reconnectGrant);
        Assert.Equal(1, new[] { 107000010, 107000020, 107000030 }
            .Sum(itemId => playerInventory.GetItemCount(itemId)));
    }

    [Fact]
    public void UsesTheSameOrbForTheSameMatchingAndPlayer()
    {
        const long matchingId = 203;
        const long playerId = 20301;
        var firstInventory = new InGameInventoryManager();
        firstInventory.Initialize();
        var secondInventory = new InGameInventoryManager();
        secondInventory.Initialize();

        int firstItemId = SurvivorOrbStartLoadout.EnsureStartingOrb(firstInventory, matchingId, playerId);
        int secondItemId = SurvivorOrbStartLoadout.EnsureStartingOrb(secondInventory, matchingId, playerId);

        Assert.Equal(firstItemId, secondItemId);
    }
}