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

    // 공명 보너스는 그 색이 판의 과반일 때만 붙고, 크기는 오브 수로 정한다(티어 무관).
    [Fact]
    public void SunPassiveRequiresMajorityAndCountsLivingOrbsWithoutTierWeight()
    {
        Assert.Equal(1f, OrbData.GetSunPveAttackMultiplier([]));
        Assert.Equal(1f, OrbData.GetSunPveAttackMultiplier(Items(107000012))); // 오브 하나는 과반이 아니다
        Assert.Equal(1f, OrbData.GetSunPveAttackMultiplier(Items(107000010, 107000020))); // 1:1은 과반이 아니다
        Assert.Equal(1.20f, OrbData.GetSunPveAttackMultiplier(Items(107000010, 107000012)));
        Assert.Equal(1.20f, OrbData.GetSunPveAttackMultiplier(Items(107000010, 107000012, 107000020)));
        Assert.Equal(1.40f, OrbData.GetSunPveAttackMultiplier(
            Items(107000010, 107000010, 107000010, 107000010, 107000010, 107000010, 107000010)));
    }

    [Fact]
    public void WindPassiveRequiresMajorityAndCountsLivingOrbsWithoutTierWeight()
    {
        Assert.Equal(1f, OrbData.GetWindMoveSpeedMultiplier([]));
        Assert.Equal(1f, OrbData.GetWindMoveSpeedMultiplier(Items(107000022)));
        Assert.Equal(1f, OrbData.GetWindMoveSpeedMultiplier(Items(107000020, 107000010)));
        Assert.Equal(1.08f, OrbData.GetWindMoveSpeedMultiplier(Items(107000020, 107000022)));
        Assert.Equal(1.08f, OrbData.GetWindMoveSpeedMultiplier(Items(107000020, 107000022, 107000030)));
        Assert.Equal(1.14f, OrbData.GetWindMoveSpeedMultiplier(
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
        Assert.True(OrbData.TryGetOrbItemId(groupId, groupTier, out int roundTripItemId));
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
        Assert.Equal(OrbGroupIds.None, OrbData.GetOrbGroupId(itemId));
        Assert.False(OrbData.IsOrbItem(itemId));
    }

    [Theory]
    [InlineData(0, 1)]
    [InlineData(int.MaxValue, 1)]
    [InlineData(OrbGroupIds.Sun, 0)]
    [InlineData(OrbGroupIds.Sun, 4)]
    public void UnknownGroupOrTierDoesNotProduceAnItem(int orbGroupId, int tier)
    {
        Assert.False(OrbData.TryGetOrbItemId(orbGroupId, tier, out int itemId));
        Assert.Equal(0, itemId);
    }

    [Fact]
    public void ResonanceUsesEquippedColorAndAnyOtherTier()
    {
        Assert.True(OrbData.TryGetActivePair(107000010, [107000021, 107000012], out var color,
            out int supportTier));
        Assert.Equal(OrbGroupIds.Sun, color);
        Assert.Equal(3, supportTier);

        Assert.False(OrbData.TryGetActivePair(107000010, [], out color, out supportTier));
        Assert.Equal(OrbGroupIds.Sun, color);
        Assert.Equal(0, supportTier);
    }

    [Fact]
    public void MultiplePairsStillResolveOnlyTheEquippedColorAndNoCrossColorFallback()
    {
        Assert.True(OrbData.TryGetActivePair(107000010,
            [107000011, 107000020, 107000021, 107000030, 107000031], out var color, out int supportTier));
        Assert.Equal(OrbGroupIds.Sun, color);
        Assert.Equal(2, supportTier);

        Assert.False(OrbData.TryGetActivePair(107000010,
            [107000020, 107000021, 107000030, 107000031], out color, out supportTier));
        Assert.Equal(OrbGroupIds.Sun, color);
        Assert.Equal(0, supportTier);
    }

    [Fact]
    public void BoardResonanceDoesNotRequireAnEquippedOrb()
    {
        Assert.True(OrbData.TryGetActivePair(
            [107000010, 107000020, 107000021],
            out var color,
            out int supportTier));
        Assert.Equal(OrbGroupIds.Wind, color);
        Assert.Equal(2, supportTier);

        Assert.False(OrbData.HasActivePair(
            [107000010, 107000020, 107000021],
            OrbGroupIds.Sun,
            out _));
        Assert.True(OrbData.HasActivePair(
            [107000010, 107000020, 107000021],
            OrbGroupIds.Wind,
            out supportTier));
        Assert.Equal(2, supportTier);
    }

    [Fact]
    public void BoardCanActivateMultipleResonanceColorsAtOnce()
    {
        int[] board = [107000010, 107000011, 107000020, 107000021, 107000030, 107000031];

        Assert.True(OrbData.HasActivePair(board, OrbGroupIds.Sun, out int redTier));
        Assert.True(OrbData.HasActivePair(board, OrbGroupIds.Wind, out int greenTier));
        Assert.True(OrbData.HasActivePair(board, OrbGroupIds.Wave, out int blueTier));
        Assert.Equal(2, redTier);
        Assert.Equal(2, greenTier);
        Assert.Equal(2, blueTier);
    }

    [Fact]
    public void InventoryResonanceUsesAllOwnedOrbs()
    {
        var inventory = new PlayerOrbState();
        inventory.AddOrb(107000030);
        inventory.AddOrb(107000032);

        Assert.True(OrbData.TryGetActivePair(inventory.GetOrderedOrbs().Select(item => item.ItemId), out var color, out int supportTier));
        Assert.Equal(OrbGroupIds.Wave, color);
        Assert.Equal(3, supportTier);
    }

    [Fact]
    public void PickedUpOrbsJoinTheTailInUidOrderWithoutEquipmentSelection()
    {
        var inventory = new PlayerOrbState();
        Assert.True(inventory.TryAddOrbWithCapacity(107000010, 6, out var first));
        Assert.True(inventory.TryAddOrbWithCapacity(107000020, 6, out var second));
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
        var reconnectedInventory = new PlayerOrbState();
        Assert.True(reconnectedInventory.TryAddOrbWithCapacity(107000010, 6, out _));
        Assert.True(reconnectedInventory.TryAddOrbWithCapacity(107000010, 6, out _));
        Assert.True(reconnectedInventory.TryAddOrbWithCapacity(107000020, 6, out _));
        Assert.True(reconnectedInventory.TryAddOrbWithCapacity(107000020, 6, out _));

        Assert.True(OrbData.TryGetActivePair(reconnectedInventory.GetOrderedOrbs().Select(item => item.ItemId), out var color, out int supportTier));
        Assert.Equal(OrbGroupIds.Sun, color);
        Assert.Equal(1, supportTier);
    }


    private static IReadOnlyList<InGameItemInfo> Items(params int[] itemIds) =>
        itemIds.Select((itemId, index) => new InGameItemInfo
        {
            ItemUid = index + 1,
            ItemId = itemId,
            Count = 1
        }).ToList();
}
