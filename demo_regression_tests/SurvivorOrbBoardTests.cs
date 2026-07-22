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
    public void ColoredOrbIdsMapToStableColorAndTier(
        int itemId,
        SurvivorOrbColor expectedColor,
        int expectedTier)
    {
        Assert.True(SurvivorOrbData.TryGetColorAndTier(itemId, out var color, out int tier));
        Assert.Equal(expectedColor, color);
        Assert.Equal(expectedTier, tier);
    }

    [Fact]
    public void LegacyGuardianOrbDoesNotEnterColoredBoardRules()
    {
        Assert.False(SurvivorOrbData.TryGetColorAndTier(107000003, out var color, out int tier));
        Assert.Equal(SurvivorOrbColor.None, color);
        Assert.Equal(0, tier);
    }

    [Fact]
    public void PickupCannotDisableAnExistingActivePair()
    {
        int[] beforePickup = [107000030, 107000030];
        int[] afterPickup = [107000030, 107000030, 107000020];

        Assert.True(SurvivorOrbData.TryGetActivePair(107000030, beforePickup, out var beforeColor,
            out int beforeTier));
        Assert.True(SurvivorOrbData.TryGetActivePair(107000030, afterPickup, out var afterColor,
            out int afterTier));
        Assert.Equal((beforeColor, beforeTier), (afterColor, afterTier));
    }

    [Fact]
    public void HighestSameTierPairOfEquippedColorWinsWhenSeveralPairsExist()
    {
        int[] board = [107000010, 107000010, 107000011, 107000011, 107000020, 107000020];

        Assert.True(SurvivorOrbData.TryGetActivePair(107000010, board, out var color, out int pairTier));
        Assert.Equal(SurvivorOrbColor.Red, color);
        Assert.Equal(2, pairTier);
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
    public void ActivePairUsesEquippedColorAndRecomputesAfterMerge()
    {
        InitializeBattleCombatData();
        var inventory = new PlayerInGameInventory(198);
        var equippedOrb = inventory.AddItem(107000010);
        inventory.AddItem(107000010);
        inventory.AddItem(107000020);

        Assert.True(inventory.TryEquipBattleItem(equippedOrb.ItemUid, out _));
        Assert.True(inventory.TryGetActiveSurvivorOrbPair(out var color, out int pairTier));
        Assert.Equal(SurvivorOrbColor.Red, color);
        Assert.Equal(1, pairTier);

        Assert.True(inventory.TryCombineItems([107000010, 107000010], 107000011, out _));
        Assert.False(inventory.TryGetActiveSurvivorOrbPair(out color, out pairTier));
        Assert.Equal(SurvivorOrbColor.Red, color);
        Assert.Equal(0, pairTier);
    }

    [Fact]
    public void ActiveOrbColorsApplyOnlyTheirConfiguredCombatProfile()
    {
        Assert.Equal(18, SurvivorOrbData.GetCombatDamage(SurvivorOrbColor.Red, true, 10));
        Assert.Equal(10, SurvivorOrbData.GetCombatDamage(SurvivorOrbColor.Red, false, 10));
        Assert.Equal(1f, SurvivorOrbData.GetAttackIntervalSeconds(
            SurvivorOrbColor.Red, true, 0.8f), 4);

        Assert.Equal(3, SurvivorOrbData.WaveInitialBurstAttackCount);
        Assert.Equal(0.4f, SurvivorOrbData.WaveInitialBurstIntervalMultiplier);
        Assert.Equal(3f, SurvivorOrbData.WaveBurstRechargeSeconds);

        Assert.Equal(3, SurvivorOrbData.GetMaxTargets(SurvivorOrbColor.Green, true));
        Assert.Equal(0.5f, SurvivorOrbData.GetAdditionalTargetDamageMultiplier(
            SurvivorOrbColor.Green, true));
        Assert.Equal(1, SurvivorOrbData.GetMaxTargets(SurvivorOrbColor.Green, false));
        Assert.Equal(1f, SurvivorOrbData.GetAdditionalTargetDamageMultiplier(
            SurvivorOrbColor.Green, false));
    }

    private static void InitializeBattleCombatData()
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);
        while (directory != null &&
               !Directory.Exists(Path.Combine(directory.FullName, "network", "Common", "csv")))
        {
            directory = directory.Parent;
        }

        if (directory == null)
            throw new DirectoryNotFoundException("Could not locate repository root from test output path.");

        BattleItemCombatData.Initialize(CsvHelper.LoadCsv(Path.Combine(
            directory.FullName, "network", "Common", "csv", "battle_item_combat.csv")));
    }
}
