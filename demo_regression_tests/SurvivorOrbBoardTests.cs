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
    [InlineData(107000040, 1, 2)]
    [InlineData(107000041, 2, 5)]
    [InlineData(107000042, 3, 10)]
    public void RecoveryOrbTierDefinesFiveSecondRecoveryAmount(
        int itemId,
        int expectedTier,
        int expectedRecovery)
    {
        Assert.Equal(5f, SurvivorOrbData.RecoveryTickSeconds);
        Assert.True(SurvivorOrbData.TryGetRecoveryTier(itemId, out int tier));
        Assert.Equal(expectedTier, tier);
        Assert.Equal(expectedRecovery, SurvivorOrbData.GetRecoveryAmount(itemId));
        Assert.False(SurvivorOrbData.IsSurvivorOrb(itemId));
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
    [InlineData(107000040, 107000041)]
    [InlineData(107000041, 107000042)]
    public void SameTierRecoveryOrbsMergeToTheNextRecoveryTier(int input, int expectedOutput)
    {
        Assert.True(SurvivorOrbData.CanMerge(input, input));
        Assert.True(SurvivorOrbData.TryGetRandomMergeOutput(input, input, new Random(198), out int output));
        Assert.Equal(expectedOutput, output);
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

    [Theory]
    [InlineData(0, 0)]
    [InlineData(1, 1)]
    [InlineData(2, 1)]
    [InlineData(3, 3)]
    [InlineData(4, 3)]
    [InlineData(5, 5)]
    [InlineData(6, 5)]
    public void SunResonanceUsesOneThreeFiveBoardStages(int sunCount, int expectedStage)
    {
        Assert.Equal(expectedStage, SurvivorOrbData.GetSunResonanceStage(sunCount));
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
    public void MultiplePairsStillResolveOnlyTheEquippedColorAndNoCrossColorFallback()
    {
        Assert.True(SurvivorOrbData.TryGetActivePair(107000010,
            [107000011, 107000020, 107000021, 107000030, 107000031], out var color, out int supportTier));
        Assert.Equal(SurvivorOrbColor.Red, color);
        Assert.Equal(2, supportTier);

        Assert.False(SurvivorOrbData.TryGetActivePair(107000010,
            [107000020, 107000021, 107000030, 107000031], out color, out supportTier));
        Assert.Equal(SurvivorOrbColor.Red, color);
        Assert.Equal(0, supportTier);
    }

    [Fact]
    public void BoardResonanceDoesNotRequireAnEquippedOrb()
    {
        Assert.True(SurvivorOrbData.TryGetActivePair(
            [107000010, 107000020, 107000021],
            out var color,
            out int supportTier));
        Assert.Equal(SurvivorOrbColor.Green, color);
        Assert.Equal(2, supportTier);

        Assert.False(SurvivorOrbData.HasActivePair(
            [107000010, 107000020, 107000021],
            SurvivorOrbColor.Red,
            out _));
        Assert.True(SurvivorOrbData.HasActivePair(
            [107000010, 107000020, 107000021],
            SurvivorOrbColor.Green,
            out supportTier));
        Assert.Equal(2, supportTier);
    }

    [Fact]
    public void BoardCanActivateMultipleResonanceColorsAtOnce()
    {
        int[] board = [107000010, 107000011, 107000020, 107000021, 107000030, 107000031];

        Assert.True(SurvivorOrbData.HasActivePair(board, SurvivorOrbColor.Red, out int redTier));
        Assert.True(SurvivorOrbData.HasActivePair(board, SurvivorOrbColor.Green, out int greenTier));
        Assert.True(SurvivorOrbData.HasActivePair(board, SurvivorOrbColor.Blue, out int blueTier));
        Assert.Equal(2, redTier);
        Assert.Equal(2, greenTier);
        Assert.Equal(2, blueTier);
    }

    [Fact]
    public void InventoryResonanceWorksBeforeAnyManualEquip()
    {
        var inventory = new PlayerInGameInventory(198);
        inventory.AddItem(107000030, forceSeparateStack: true);
        inventory.AddItem(107000032, forceSeparateStack: true);

        Assert.Null(inventory.GetEquippedBattleItem());
        Assert.True(inventory.TryGetActiveSurvivorOrbPair(out var color, out int supportTier));
        Assert.Equal(SurvivorOrbColor.Blue, color);
        Assert.Equal(3, supportTier);
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
    public void ReconnectRecomputesTheSameSingleResonanceFromTheAuthoritativeBoard()
    {
        InitializeBattleCombatData();
        var manager = new InGameInventoryManager();
        manager.Initialize();
        Assert.True(manager.TryAddItemWithCapacity(198, 101, 107000010, 6, out _));
        Assert.True(manager.TryAddItemWithCapacity(198, 101, 107000010, 6, out _));
        Assert.True(manager.TryAddItemWithCapacity(198, 101, 107000020, 6, out _));
        Assert.True(manager.TryAddItemWithCapacity(198, 101, 107000020, 6, out _));

        var reconnectedInventory = manager.GetPlayerInventory(198, 101);
        Assert.True(reconnectedInventory.TryGetActiveSurvivorOrbPair(out var color, out int supportTier));
        Assert.Equal(SurvivorOrbColor.Red, color);
        Assert.Equal(1, supportTier);
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

    [Fact]
    public void ReconnectingToTheSameMatchReadsTheSingleAuthoritativeMergeResult()
    {
        InitializeBattleCombatData();
        var manager = new InGameInventoryManager();
        manager.Initialize();
        manager.AddItem(198, 100, 107000010);
        manager.AddItem(198, 100, 107000010);

        Assert.True(manager.TryCombineSurvivorOrbs(198, 100, 107000010, 107000010,
            new Random(198), out int outputItemId, out _));

        // A new session retrieves the same matching/player inventory; it must not replay the merge.
        var reconnectedInventory = manager.GetPlayerInventory(198, 100);
        var output = Assert.Single(reconnectedInventory.GetAllItems());
        Assert.Equal(outputItemId, output.ItemId);
        Assert.Equal(1, output.Count);
        Assert.False(manager.TryCombineSurvivorOrbs(198, 100, 107000010, 107000010,
            new Random(199), out _, out _));
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
