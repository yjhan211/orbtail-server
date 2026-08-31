using game_server.services;
using network.common;
using network.common.data.helpers;

namespace demo_regression_tests;

public sealed class EliminationInventoryDropperTests
{
    private const int HopeOrbT1 = 107000010;
    private const int ForgetOrbT1 = 107000020;
    private const int CannedCoffee = 201000011;

    public EliminationInventoryDropperTests()
    {
        GameDataHelper.SetBasePath(FindNetworkBasePath());
        GameDataHelper.Initialize();
    }

    [Theory]
    [InlineData(7001)]
    [InlineData(-7001)]
    public void PlayersAndBotsUseTheSameEliminationScatterPolicy(long playerId)
    {
        const long matchingId = 99001;
        var inventory = new InGameInventoryManager();
        var groundItems = new GroundItemManager();
        inventory.Initialize();
        inventory.AddItem(matchingId, playerId, HopeOrbT1);
        inventory.AddItem(matchingId, playerId, ForgetOrbT1);
        inventory.AddItem(matchingId, playerId, CannedCoffee);

        var result = EliminationInventoryDropper.DropAll(
            inventory,
            groundItems,
            matchingId,
            playerId,
            AreaType.S2Classroom1,
            0f,
            0f);

        Assert.Equal(3, result.RemovedItems.Count);
        Assert.Equal(
            new[] { HopeOrbT1, ForgetOrbT1 },
            result.DroppedItemIds.OrderBy(id => id).ToArray());
        Assert.Equal(
            new[] { HopeOrbT1, ForgetOrbT1 },
            result.SpawnedItems.Select(item => item.ItemId).OrderBy(id => id).ToArray());
        Assert.Empty(inventory.GetAllItems(matchingId, playerId));
    }

    [Fact]
    public void RepeatedEliminationDropDoesNotSpawnTheSameBoardTwice()
    {
        const long matchingId = 99002;
        const long botPlayerId = -7002;
        var inventory = new InGameInventoryManager();
        var groundItems = new GroundItemManager();
        inventory.Initialize();
        inventory.AddItem(matchingId, botPlayerId, HopeOrbT1);

        var first = EliminationInventoryDropper.DropAll(
            inventory,
            groundItems,
            matchingId,
            botPlayerId,
            AreaType.S2Classroom1,
            0f,
            0f);
        var duplicate = EliminationInventoryDropper.DropAll(
            inventory,
            groundItems,
            matchingId,
            botPlayerId,
            AreaType.S2Classroom1,
            0f,
            0f);

        Assert.Single(first.SpawnedItems);
        Assert.Empty(duplicate.RemovedItems);
        Assert.Empty(duplicate.SpawnedItems);
        Assert.Single(groundItems.GetSnapshot(matchingId, AreaType.S2Classroom1));
    }

    private static string FindNetworkBasePath()
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);
        while (directory != null)
        {
            string candidate = Path.Combine(directory.FullName, "network", "Common", "csv");
            if (Directory.Exists(candidate))
                return Path.Combine(directory.FullName, "network");
            directory = directory.Parent;
        }

        throw new DirectoryNotFoundException("Could not find network/Common/csv.");
    }
}
