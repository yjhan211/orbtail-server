using game_server.matches;
using game_server.players;
using network.common;
using network.common.data;
using network.common.data.helpers;
using network.common.data.models;

namespace demo_regression_tests;

public sealed class OrbBoardTests
{
    public OrbBoardTests()
    {
        TestGameData.EnsureBattleItemCombatLoaded();
    }

    // 주기는 전 티어 0.8초 고정 (2026-08-24 #268): 티어 주기 단축은 오브 개수 증가와 겹치는
    // 이중 가속이라 퇴역 — 티어 값어치는 발당 피해(12/21/30)와 사거리만 진다. 기저 0.8초는
    // #229 상향값(시작 오브 하나로 구역 보충 초당 1.33마리를 열 수 있는 최소 박자) 그대로다.
    [Theory]
    [InlineData(107000010, 12, 0.8f)]
    [InlineData(107000011, 21, 0.8f)]
    [InlineData(107000012, 30, 0.8f)]
    [InlineData(107000020, 12, 0.8f)]
    [InlineData(107000021, 21, 0.8f)]
    [InlineData(107000022, 30, 0.8f)]
    public void SunAndWindUseTheSameTierAttackTable(int itemId, int damage, float interval)
    {
        Assert.Equal(damage, OrbData.GetSwarmPveAttackDamage(itemId));
        Assert.Equal(interval, BattleItemCombatData.Get(itemId)!.AttackIntervalSeconds);
    }

    [Fact]
    public void SunPassiveCountsLivingOrbsWithoutTierWeight()
    {
        Assert.Equal(1f, OrbData.GetSunPveAttackMultiplier([]));
        Assert.Equal(1.15f, OrbData.GetSunPveAttackMultiplier(Items(107000012)));
        Assert.Equal(1.20f, OrbData.GetSunPveAttackMultiplier(Items(107000010, 107000012)));
        Assert.Equal(1.40f, OrbData.GetSunPveAttackMultiplier(
            Items(107000010, 107000010, 107000010, 107000010, 107000010, 107000010, 107000010)));
    }

    [Fact]
    public void WindPassiveCountsLivingOrbsWithoutTierWeight()
    {
        Assert.Equal(1f, OrbData.GetWindMoveSpeedMultiplier([]));
        Assert.Equal(1.06f, OrbData.GetWindMoveSpeedMultiplier(Items(107000022)));
        Assert.Equal(1.08f, OrbData.GetWindMoveSpeedMultiplier(Items(107000020, 107000022)));
        Assert.Equal(1.14f, OrbData.GetWindMoveSpeedMultiplier(
            Items(107000020, 107000020, 107000020, 107000020, 107000020, 107000020)));
    }

    [Theory]
    [InlineData(107000010, 10700001, OrbColor.Red, 1)]
    [InlineData(107000011, 10700001, OrbColor.Red, 2)]
    [InlineData(107000012, 10700001, OrbColor.Red, 3)]
    [InlineData(107000020, 10700002, OrbColor.Green, 1)]
    [InlineData(107000021, 10700002, OrbColor.Green, 2)]
    [InlineData(107000022, 10700002, OrbColor.Green, 3)]
    [InlineData(107000030, 10700003, OrbColor.Blue, 1)]
    [InlineData(107000031, 10700003, OrbColor.Blue, 2)]
    [InlineData(107000032, 10700003, OrbColor.Blue, 3)]
    public void ColoredOrbIdsMapToStableGroupColorAndTier(
        int itemId,
        int expectedGroupId,
        OrbColor expectedColor,
        int expectedTier)
    {
        Assert.True(OrbData.TryGetColorAndTier(itemId, out var color, out int tier));
        Assert.True(OrbData.TryGetOrbGroupAndTier(itemId, out int groupId, out int groupTier));
        Assert.True(OrbData.TryGetOrbItemId(groupId, groupTier, out int roundTripItemId));
        Assert.Equal(expectedGroupId, groupId);
        Assert.Equal(expectedColor, color);
        Assert.Equal(expectedTier, tier);
        Assert.Equal(expectedTier, groupTier);
        Assert.Equal(itemId, roundTripItemId);
    }
    [Theory]
    [InlineData(107000040, 1, 5)]
    [InlineData(107000041, 2, 10)]
    [InlineData(107000042, 3, 20)]
    public void RecoveryOrbTierDefinesFiveSecondRecoveryAmount(
        int itemId,
        int expectedTier,
        int expectedRecovery)
    {
        Assert.Equal(5f, OrbData.RecoveryTickSeconds);
        Assert.True(OrbData.TryGetRecoveryTier(itemId, out int tier));
        Assert.Equal(expectedTier, tier);
        Assert.Equal(expectedRecovery, OrbData.GetRecoveryAmount(itemId));
        Assert.False(OrbData.IsOrbItem(itemId));
    }




    [Fact]
    public void LegacyGuardianOrbDoesNotEnterColoredBoardRules()
    {
        Assert.False(OrbData.TryGetColorAndTier(107000003, out var color, out int tier));
        Assert.Equal(OrbColor.None, color);
        Assert.Equal(0, tier);
    }

    [Fact]
    public void ResonanceUsesEquippedColorAndAnyOtherTier()
    {
        Assert.True(OrbData.TryGetActivePair(107000010, [107000021, 107000012], out var color,
            out int supportTier));
        Assert.Equal(OrbColor.Red, color);
        Assert.Equal(3, supportTier);

        Assert.False(OrbData.TryGetActivePair(107000010, [], out color, out supportTier));
        Assert.Equal(OrbColor.Red, color);
        Assert.Equal(0, supportTier);
    }

    [Fact]
    public void MultiplePairsStillResolveOnlyTheEquippedColorAndNoCrossColorFallback()
    {
        Assert.True(OrbData.TryGetActivePair(107000010,
            [107000011, 107000020, 107000021, 107000030, 107000031], out var color, out int supportTier));
        Assert.Equal(OrbColor.Red, color);
        Assert.Equal(2, supportTier);

        Assert.False(OrbData.TryGetActivePair(107000010,
            [107000020, 107000021, 107000030, 107000031], out color, out supportTier));
        Assert.Equal(OrbColor.Red, color);
        Assert.Equal(0, supportTier);
    }

    [Fact]
    public void BoardResonanceDoesNotRequireAnEquippedOrb()
    {
        Assert.True(OrbData.TryGetActivePair(
            [107000010, 107000020, 107000021],
            out var color,
            out int supportTier));
        Assert.Equal(OrbColor.Green, color);
        Assert.Equal(2, supportTier);

        Assert.False(OrbData.HasActivePair(
            [107000010, 107000020, 107000021],
            OrbColor.Red,
            out _));
        Assert.True(OrbData.HasActivePair(
            [107000010, 107000020, 107000021],
            OrbColor.Green,
            out supportTier));
        Assert.Equal(2, supportTier);
    }

    [Fact]
    public void BoardCanActivateMultipleResonanceColorsAtOnce()
    {
        int[] board = [107000010, 107000011, 107000020, 107000021, 107000030, 107000031];

        Assert.True(OrbData.HasActivePair(board, OrbColor.Red, out int redTier));
        Assert.True(OrbData.HasActivePair(board, OrbColor.Green, out int greenTier));
        Assert.True(OrbData.HasActivePair(board, OrbColor.Blue, out int blueTier));
        Assert.Equal(2, redTier);
        Assert.Equal(2, greenTier);
        Assert.Equal(2, blueTier);
    }

    [Fact]
    public void InventoryResonanceUsesAllOwnedOrbs()
    {
        var inventory = new PlayerOrbCollection();
        inventory.AddItem(107000030);
        inventory.AddItem(107000032);

        Assert.True(OrbData.TryGetActivePair(inventory.GetOrderedOrbs().Select(item => item.ItemId), out var color, out int supportTier));
        Assert.Equal(OrbColor.Blue, color);
        Assert.Equal(3, supportTier);
    }

    [Fact]
    public void PickedUpOrbsJoinTheTailInUidOrderWithoutEquipmentSelection()
    {
        InitializeBattleCombatData();
        var inventory = new PlayerOrbCollection();
        Assert.True(inventory.TryAddItemWithCapacity(107000010, 6, out var first));
        Assert.True(inventory.TryAddItemWithCapacity(107000020, 6, out var second));
        Assert.Equal(new[] { first!.ItemUid, second!.ItemUid },
            inventory.GetOrderedOrbs().Select(item => item.ItemUid));
        var actors = new List<ProximityCombatActor>();
        MatchCombatActorBuilder.AddOrbActors(actors, default, inventory);
        Assert.Equal(new[] { first.ItemUid, second.ItemUid }, actors.Select(actor => actor.WeaponItemUid));
        Assert.True(inventory.TryReplaceOrb(first.ItemUid, 107000011, out _));
        Assert.Equal(first.ItemUid, inventory.GetOrderedOrbs()[0].ItemUid);
    }


    [Fact]
    public void ReconnectRecomputesTheSameSingleResonanceFromTheAuthoritativeBoard()
    {
        InitializeBattleCombatData();
        var reconnectedInventory = new PlayerOrbCollection();
        Assert.True(reconnectedInventory.TryAddItemWithCapacity(107000010, 6, out _));
        Assert.True(reconnectedInventory.TryAddItemWithCapacity(107000010, 6, out _));
        Assert.True(reconnectedInventory.TryAddItemWithCapacity(107000020, 6, out _));
        Assert.True(reconnectedInventory.TryAddItemWithCapacity(107000020, 6, out _));

        Assert.True(OrbData.TryGetActivePair(reconnectedInventory.GetOrderedOrbs().Select(item => item.ItemId), out var color, out int supportTier));
        Assert.Equal(OrbColor.Red, color);
        Assert.Equal(1, supportTier);
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

    private static IReadOnlyList<InGameItemInfo> Items(params int[] itemIds) =>
        itemIds.Select((itemId, index) => new InGameItemInfo
        {
            ItemUid = index + 1,
            ItemId = itemId,
            Count = 1
        }).ToList();
}
