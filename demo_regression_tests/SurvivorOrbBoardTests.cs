using game_server.services;
using network.common.data;
using network.common.data.helpers;

namespace demo_regression_tests;

public sealed class SurvivorOrbBoardTests
{
    [Theory]
    [InlineData(107000010, SurvivorOrbColor.Red, 1)]
    [InlineData(107000011, SurvivorOrbColor.Red, 2)]
    [InlineData(107000012, SurvivorOrbColor.Red, 3)]
    [InlineData(107000020, SurvivorOrbColor.Green, 1)]
    [InlineData(107000021, SurvivorOrbColor.Green, 2)]
    [InlineData(107000022, SurvivorOrbColor.Green, 3)]
    [InlineData(107000030, SurvivorOrbColor.Blue, 1)]
    [InlineData(107000031, SurvivorOrbColor.Blue, 2)]
    [InlineData(107000032, SurvivorOrbColor.Blue, 3)]
    public void ColoredOrbIdsMapToStableColorAndTier(int itemId, SurvivorOrbColor expectedColor, int expectedTier)
    {
        Assert.True(SurvivorOrbData.TryGetColorAndTier(itemId, out var color, out int tier));
        Assert.Equal(expectedColor, color);
        Assert.Equal(expectedTier, tier);
    }

    [Theory]
    [InlineData(107000010, 107000010)]
    [InlineData(107000011, 107000011)]
    [InlineData(107000020, 107000020)]
    [InlineData(107000021, 107000021)]
    [InlineData(107000030, 107000030)]
    [InlineData(107000031, 107000031)]
    public void SameColorSameTierOrbMergeEvolvesToRandomNextTier(int inputA, int inputB)
    {
        var random = new Random(198);
        var outputs = new HashSet<int>();
        for (int i = 0; i < 50; i++)
        {
            Assert.True(SurvivorOrbData.TryGetRandomMergeOutput(inputA, inputB, random, out int output));
            Assert.True(SurvivorOrbData.TryGetColorAndTier(inputA, out _, out int inputTier));
            Assert.True(SurvivorOrbData.TryGetColorAndTier(output, out _, out int outputTier));
            Assert.Equal(inputTier + 1, outputTier);
            outputs.Add(output);
        }

        Assert.All(outputs, output => Assert.True(SurvivorOrbData.IsSurvivorOrb(output)));
        Assert.True(outputs.Count > 1);
    }

    [Theory]
    [InlineData(107000010, 107000020)]
    [InlineData(107000010, 107000011)]
    [InlineData(107000012, 107000012)]
    public void DifferentColorDifferentTierOrTierThreeCannotMerge(int inputA, int inputB)
    {
        Assert.False(SurvivorOrbData.CanMerge(inputA, inputB));
        Assert.False(SurvivorOrbData.TryGetRandomMergeOutput(inputA, inputB, new Random(1), out _));
    }

    [Fact]
    public void LegacyGuardianOrbDoesNotEnterColoredBoardRules()
    {
        Assert.False(SurvivorOrbData.TryGetColorAndTier(107000003, out var color, out int tier));
        Assert.Equal(SurvivorOrbColor.None, color);
        Assert.Equal(0, tier);
    }

    [Fact]
    public void ResonanceUsesEquippedColorAndAnyOtherTier()
    {
        Assert.True(SurvivorOrbData.TryGetActivePair(107000010, [107000021, 107000012], out var color,
            out int supportTier));
        Assert.Equal(SurvivorOrbColor.Red, color);
        Assert.Equal(3, supportTier);

        Assert.False(SurvivorOrbData.TryGetActivePair(107000010, [], out color, out supportTier));
        Assert.Equal(SurvivorOrbColor.Red, color);
        Assert.Equal(0, supportTier);
    }

    [Fact]
    public void FirstColoredOrbPickupAutoEquipsWithoutReplacingItOnLaterPickups()
    {
        InitializeBattleCombatData();
        var manager = new InGameInventoryManager();
        manager.Initialize();

        Assert.True(manager.TryAddItemWithCapacity(10, 100, 107000010, 6, out var firstOrb));
        Assert.Equal(firstOrb!.ItemUid, manager.GetEquippedBattleItem(10, 100)!.ItemUid);

        Assert.True(manager.TryAddItemWithCapacity(10, 100, 107000020, 6, out _));
        Assert.Equal(firstOrb.ItemUid, manager.GetEquippedBattleItem(10, 100)!.ItemUid);
    }

    [Fact]
    public void EquippedInputStaysEquippedAndResonanceRecomputesAfterRandomMerge()
    {
        InitializeBattleCombatData();
        var inventory = new PlayerInGameInventory(198);
        var equippedOrb = inventory.AddItem(107000010, forceSeparateStack: true);
        inventory.AddItem(107000010, forceSeparateStack: true);

        Assert.True(inventory.TryEquipBattleItem(equippedOrb.ItemUid, out _));
        Assert.True(inventory.TryGetActiveSurvivorOrbPair(out var color, out int supportTier));
        Assert.Equal(SurvivorOrbColor.Red, color);
        Assert.Equal(1, supportTier);

        Assert.True(inventory.TryCombineSurvivorOrbs(107000010, 107000010, new Random(1), out int output, out _));
        Assert.Equal(output, inventory.GetEquippedBattleItem()!.ItemId);
        Assert.False(inventory.TryGetActiveSurvivorOrbPair(out color, out supportTier));
    }

    [Fact]
    public async Task ConcurrentRandomMergeConsumesInputsOnlyOnce()
    {
        InitializeBattleCombatData();
        var manager = new InGameInventoryManager();
        manager.Initialize();
        manager.AddItem(10, 100, 107000010);
        manager.AddItem(10, 100, 107000010);

        var attempts = await Task.WhenAll(
            Task.Run(() => manager.TryCombineSurvivorOrbs(10, 100, 107000010, 107000010, new Random(1), out _, out _)),
            Task.Run(() => manager.TryCombineSurvivorOrbs(10, 100, 107000010, 107000010, new Random(2), out _, out _)));

        Assert.Single(attempts, success => success);
        Assert.Single(attempts, success => !success);
        Assert.Equal(1, manager.GetPlayerInventory(10, 100).GetAllItems().Sum(item => item.Count));
    }

    private static void InitializeBattleCombatData()
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);
        while (directory != null && !Directory.Exists(Path.Combine(directory.FullName, "network", "Common", "csv")))
            directory = directory.Parent;

        if (directory == null)
            throw new DirectoryNotFoundException("Could not locate repository root from test output path.");

        BattleItemCombatData.Initialize(CsvHelper.LoadCsv(Path.Combine(
            directory.FullName, "network", "Common", "csv", "battle_item_combat.csv")));
    }
}