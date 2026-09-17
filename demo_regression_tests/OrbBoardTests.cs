using game_server.matches;
using game_server.players;
using network.common;
using network.common.data;
using network.common.data.models;

namespace demo_regression_tests;

public sealed class OrbBoardTests
{
    public OrbBoardTests()
    {
        TestGameData.EnsureBattleItemCombatLoaded();
    }

    // 티어 값어치는 발당 피해(12/21/30)만 진다 — 주기는 계열별 swarm_config 키가 정하고 티어와 무관하다.
    [Theory]
    [InlineData(107000010, 12)]
    [InlineData(107000011, 21)]
    [InlineData(107000012, 30)]
    [InlineData(107000020, 12)]
    [InlineData(107000021, 21)]
    [InlineData(107000022, 30)]
    public void SunAndWindUseTheSameTierAttackTable(int itemId, int damage)
    {
        Assert.Equal(damage, OrbData.GetAttackDamage(itemId));
    }

    // 공명 보너스는 그 색이 판의 과반일 때만 붙고, 크기는 오브 수로 정한다(티어 무관).
    [Fact]
    public void SunPassiveRequiresMajorityAndCountsLivingOrbsWithoutTierWeight()
    {
        Assert.Equal(1f, OrbData.GetAttackMultiplier([]));
        Assert.Equal(1f, OrbData.GetAttackMultiplier(Items(107000012))); // 오브 하나는 과반이 아니다
        Assert.Equal(1f, OrbData.GetAttackMultiplier(Items(107000010, 107000020))); // 1:1은 과반이 아니다
        Assert.Equal(1.20f, OrbData.GetAttackMultiplier(Items(107000010, 107000012)));
        Assert.Equal(1.20f, OrbData.GetAttackMultiplier(Items(107000010, 107000012, 107000020)));
        Assert.Equal(1.40f, OrbData.GetAttackMultiplier(
            Items(107000010, 107000010, 107000010, 107000010, 107000010, 107000010, 107000010)));
    }

    [Fact]
    public void WindPassiveRequiresMajorityAndCountsLivingOrbsWithoutTierWeight()
    {
        Assert.Equal(1f, OrbData.GetMoveSpeedMultiplier([]));
        Assert.Equal(1f, OrbData.GetMoveSpeedMultiplier(Items(107000022)));
        Assert.Equal(1f, OrbData.GetMoveSpeedMultiplier(Items(107000020, 107000010)));
        Assert.Equal(1.08f, OrbData.GetMoveSpeedMultiplier(Items(107000020, 107000022)));
        Assert.Equal(1.08f, OrbData.GetMoveSpeedMultiplier(Items(107000020, 107000022, 107000030)));
        Assert.Equal(1.14f, OrbData.GetMoveSpeedMultiplier(
            Items(107000020, 107000020, 107000020, 107000020, 107000020, 107000020)));
    }

    [Theory]
    [InlineData(107000010, OrbGroupIds.Sun, 1)]
    [InlineData(107000011, OrbGroupIds.Sun, 2)]
    [InlineData(107000012, OrbGroupIds.Sun, 3)]
    [InlineData(107000020, OrbGroupIds.Wind, 1)]
    [InlineData(107000021, OrbGroupIds.Wind, 2)]
    [InlineData(107000022, OrbGroupIds.Wind, 3)]
    [InlineData(107000030, OrbGroupIds.Wave, 1)]
    [InlineData(107000031, OrbGroupIds.Wave, 2)]
    [InlineData(107000032, OrbGroupIds.Wave, 3)]
    public void OrbIdsMapToCsvGroupAndTier(
        int itemId,
        int expectedGroupId,
        int expectedTier)
    {
        Assert.True(OrbData.TryGetOrbGroupAndTier(itemId, out int groupId, out int groupTier));
        Assert.True(OrbData.TryGetItemId(groupId, groupTier, out int roundTripItemId));
        Assert.Equal(expectedGroupId, groupId);
        Assert.Equal(expectedTier, groupTier);
        Assert.Equal(itemId, roundTripItemId);
    }




    [Theory]
    [InlineData(107000003)]
    [InlineData(107000004)]
    [InlineData(107000006)]
    [InlineData(0)]
    [InlineData(107000019)]
    public void LegacyOrUnknownItemDoesNotEnterOrbGroupRules(int itemId)
    {
        Assert.False(OrbData.TryGetOrbGroupAndTier(itemId, out var color, out int tier));
        Assert.Equal(OrbGroupIds.None, color);
        Assert.Equal(0, tier);
        Assert.False(OrbData.IsOrbItem(itemId));
    }

    [Theory]
    [InlineData(0, 1)]
    [InlineData(int.MaxValue, 1)]
    [InlineData(OrbGroupIds.Sun, 0)]
    [InlineData(OrbGroupIds.Sun, 4)]
    public void UnknownGroupOrTierDoesNotProduceAnItem(int orbGroupId, int tier)
    {
        Assert.False(OrbData.TryGetItemId(orbGroupId, tier, out int itemId));
        Assert.Equal(0, itemId);
    }

    [Fact]
    public void InventoryResonanceUsesAllOwnedOrbs()
    {
        var inventory = new PlayerOrbState();
        inventory.AddOrb(107000030);
        inventory.AddOrb(107000032);

        Assert.True(OrbData.IsResonating(inventory.GetOrderedOrbs(), OrbGroupIds.Wave));
    }

    [Fact]
    public void PickedUpOrbsJoinTheTailInUidOrderWithoutEquipmentSelection()
    {
        var inventory = new PlayerOrbState();
        Assert.True(inventory.TryAddOrbWithCapacity(107000010, 6, out var first));
        Assert.True(inventory.TryAddOrbWithCapacity(107000020, 6, out var second));
        Assert.Equal(new[] { first!.ItemUid, second!.ItemUid },
            inventory.GetOrderedOrbs().Select(item => item.ItemUid));
        Assert.True(inventory.TryReplaceOrb(first.ItemUid, 107000011, out _));
        Assert.Equal(first.ItemUid, inventory.GetOrderedOrbs()[0].ItemUid);
    }


    [Fact]
    public void TiedBoardHasNoResonanceAfterReconnect()
    {
        var reconnectedInventory = new PlayerOrbState();
        Assert.True(reconnectedInventory.TryAddOrbWithCapacity(107000010, 6, out _));
        Assert.True(reconnectedInventory.TryAddOrbWithCapacity(107000010, 6, out _));
        Assert.True(reconnectedInventory.TryAddOrbWithCapacity(107000020, 6, out _));
        Assert.True(reconnectedInventory.TryAddOrbWithCapacity(107000020, 6, out _));

        Assert.False(OrbData.IsResonating(reconnectedInventory.GetOrderedOrbs(), OrbGroupIds.Sun));
        Assert.False(OrbData.IsResonating(reconnectedInventory.GetOrderedOrbs(), OrbGroupIds.Wind));
    }


    private static IReadOnlyList<InGameItemInfo> Items(params int[] itemIds) =>
        itemIds.Select((itemId, index) => new InGameItemInfo
        {
            ItemUid = index + 1,
            ItemId = itemId,
            Count = 1
        }).ToList();
}
