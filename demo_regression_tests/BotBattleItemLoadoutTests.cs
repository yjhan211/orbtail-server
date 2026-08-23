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
    public void BotKeepsAnOrbPairEquippedWhenTheResonanceHoldIsActive()
    {
        const long matchingId = 199;
        const long botPlayerId = -19901;
        var inventory = new InGameInventoryManager();
        inventory.Initialize();
        inventory.AddItem(matchingId, botPlayerId, 107000010);
        inventory.AddItem(matchingId, botPlayerId, 107000010);

        var result = BotBattleItemLoadout.CombineAndEquip(
            inventory, matchingId, botPlayerId, new Random(199), allowSurvivorOrbMerges: false);

        Assert.Empty(result.SurvivorOrbMerges);
        Assert.Equal(2, inventory.GetPlayerInventory(matchingId, botPlayerId).GetItemCount(107000010));
        Assert.True(inventory.GetPlayerInventory(matchingId, botPlayerId)
            .TryGetActiveSurvivorOrbPair(out OrbColor color, out _));
        Assert.Equal(OrbColor.Red, color);
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
        Assert.True(OrbData.TryGetColorAndTier(output, out _, out int tier));
        Assert.Equal(2, tier);
        Assert.Equal(output, result.EquippedItemId);
        Assert.Equal(output, inventory.GetEquippedBattleItem(matchingId, botPlayerId)!.ItemId);
        var merge = Assert.Single(result.SurvivorOrbMerges);
        Assert.Equal(107000010, merge.InputItemId);
        Assert.Equal(output, merge.OutputItemId);
    }
    [Fact]
    public void BotDoesNotDestroyAnOrbWhileAValidMergeExists()
    {
        const long matchingId = 206;
        const long botPlayerId = -20601;
        var inventoryManager = CreateFullOrbBoard(
            matchingId,
            botPlayerId,
            107000010,
            107000010,
            107000021,
            107000032,
            107000040,
            107000012);

        var decision = BotBattleItemLoadout.SelectOverflowDestroyCandidate(
            inventoryManager.GetPlayerInventory(matchingId, botPlayerId),
            corruption: 0,
            maxCorruption: 420);

        Assert.Null(decision);
    }

    [Fact]
    public void BotDestroysRecoveryOrbFirstWhileHealthy()
    {
        const long matchingId = 206;
        const long botPlayerId = -20602;
        var inventoryManager = CreateFullOrbBoard(
            matchingId,
            botPlayerId,
            107000012,
            107000021,
            107000032,
            107000040,
            107000010,
            107000020);

        var decision = BotBattleItemLoadout.SelectOverflowDestroyCandidate(
            inventoryManager.GetPlayerInventory(matchingId, botPlayerId),
            corruption: 0,
            maxCorruption: 420);

        Assert.NotNull(decision);
        Assert.Equal(107000040, decision.ItemId);
        Assert.Equal(OrbColor.Recovery, decision.Color);
    }

    [Fact]
    public void BotPreservesRecoveryOrbWhenCorruptionIsHigh()
    {
        const long matchingId = 206;
        const long botPlayerId = -20603;
        var inventoryManager = CreateFullOrbBoard(
            matchingId,
            botPlayerId,
            107000012,
            107000021,
            107000032,
            107000040,
            107000010,
            107000020);

        var decision = BotBattleItemLoadout.SelectOverflowDestroyCandidate(
            inventoryManager.GetPlayerInventory(matchingId, botPlayerId),
            corruption: 380,
            maxCorruption: 420);

        Assert.NotNull(decision);
        Assert.NotEqual(OrbColor.Recovery, decision.Color);
    }

    [Fact]
    public void BotProtectsTheDominantResonanceColor()
    {
        const long matchingId = 206;
        const long botPlayerId = -20604;
        var inventoryManager = CreateFullOrbBoard(
            matchingId,
            botPlayerId,
            107000010,
            107000011,
            107000012,
            107000012,
            107000020,
            107000040);

        var decision = BotBattleItemLoadout.SelectOverflowDestroyCandidate(
            inventoryManager.GetPlayerInventory(matchingId, botPlayerId),
            corruption: 350,
            maxCorruption: 420);

        Assert.NotNull(decision);
        Assert.NotEqual(OrbColor.Red, decision.Color);
    }

    private static InGameInventoryManager CreateFullOrbBoard(
        long matchingId,
        long botPlayerId,
        params int[] itemIds)
    {
        var inventoryManager = new InGameInventoryManager();
        inventoryManager.Initialize();
        foreach (int itemId in itemIds)
        {
            Assert.True(inventoryManager.TryAddItemWithCapacity(
                matchingId,
                botPlayerId,
                itemId,
                6,
                out _));
        }

        return inventoryManager;
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
