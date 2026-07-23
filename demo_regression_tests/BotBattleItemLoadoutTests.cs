using game_server.services;
using network.common.data;
using network.common.data.helpers;

namespace demo_regression_tests;

public sealed class BotBattleItemLoadoutTests
{
    public BotBattleItemLoadoutTests()
    {
        GameDataHelper.SetBasePath(Path.Combine(FindRepositoryRoot(), "network"));
        GameDataHelper.Initialize();
    }

    [Fact]
    public void BotCombinesT1PairAndEquipsTheProducedT2()
    {
        const long matchingId = 194;
        const long botPlayerId = -19401;
        var inventory = new InGameInventoryManager();
        inventory.Initialize();
        inventory.AddItem(matchingId, botPlayerId, 107000003);
        inventory.AddItem(matchingId, botPlayerId, 107000003);

        var result = BotBattleItemLoadout.CombineAndEquip(inventory, matchingId, botPlayerId, new Random(194));

        Assert.Single(result.CombinedItemIds);
        Assert.Equal(107000004, result.EquippedItemId);
        Assert.Equal(result.EquippedItemId, result.CombinedItemIds[0]);
        Assert.Equal(result.EquippedItemId, inventory.GetEquippedBattleItem(matchingId, botPlayerId)!.ItemId);
        Assert.Equal(0, inventory.GetPlayerInventory(matchingId, botPlayerId).GetItemCount(107000003));
    }

    [Fact]
    public void BotCombinesT2PairAndEquipsT3()
    {
        const long matchingId = 194;
        const long botPlayerId = -19402;
        var inventory = new InGameInventoryManager();
        inventory.Initialize();
        inventory.AddItem(matchingId, botPlayerId, 107000004);
        inventory.AddItem(matchingId, botPlayerId, 107000004);

        var result = BotBattleItemLoadout.CombineAndEquip(inventory, matchingId, botPlayerId, new Random(194));

        Assert.Equal(new[] { 107000006 }, result.CombinedItemIds);
        Assert.Equal(107000006, result.EquippedItemId);
        Assert.Equal(107000006, inventory.GetEquippedBattleItem(matchingId, botPlayerId)!.ItemId);
    }

    [Fact]
    public void BotRandomlyEvolvesSurvivorOrbsAndPivotsToTheBestAvailableOrb()
    {
        const long matchingId = 198;
        const long botPlayerId = -19801;
        var inventory = new InGameInventoryManager();
        inventory.Initialize();

        inventory.AddItem(matchingId, botPlayerId, 107000010);
        inventory.AddItem(matchingId, botPlayerId, 107000010);

        var result = BotBattleItemLoadout.CombineAndEquip(inventory, matchingId, botPlayerId, new Random(198));

        int output = Assert.Single(result.CombinedItemIds);
        Assert.True(SurvivorOrbData.TryGetColorAndTier(output, out _, out int tier));
        Assert.Equal(2, tier);
        Assert.Equal(output, result.EquippedItemId);
        Assert.Equal(output, inventory.GetEquippedBattleItem(matchingId, botPlayerId)!.ItemId);
    }
    private static string FindRepositoryRoot()
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);
        while (directory != null)
        {
            if (Directory.Exists(Path.Combine(directory.FullName, "network", "Common", "csv")))
                return directory.FullName;
            directory = directory.Parent;
        }

        throw new DirectoryNotFoundException("Could not locate repository root from test output path.");
    }
}
