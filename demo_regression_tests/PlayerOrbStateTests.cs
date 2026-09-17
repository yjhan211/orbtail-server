using game_server.players;

namespace demo_regression_tests;

public sealed class PlayerOrbStateTests
{
    public PlayerOrbStateTests()
    {
        // 오브 판별에 필요한 데이터를 직접 준비해 다른 테스트의 실행 순서에 의존하지 않는다.
        TestGameData.EnsureBattleItemCombatLoaded();
    }

    [Fact]
    public void HighestOrbTierFollowsTheWholeInventory()
    {
        var inventory = new PlayerOrbState();
        Assert.Equal(0, inventory.GetHighestOrbTier());
        var first = inventory.AddOrb(107000010);
        var highest = inventory.AddOrb(107000032);
        Assert.Equal(3, inventory.GetHighestOrbTier());
        Assert.True(inventory.TryRemoveOrb(highest.ItemUid, 1, out _));
        Assert.Equal(1, inventory.GetHighestOrbTier());
        Assert.True(inventory.TryRemoveOrb(first.ItemUid, 1, out _));
        Assert.Equal(0, inventory.GetHighestOrbTier());
    }

    [Fact]
    // 저장소 불변식: 오브만, 항목당 한 개. 조회 계층이 Count·티어를 재검사하지 않는 근거다.
    public void NonOrbItemsAreRejected()
    {
        var inventory = new PlayerOrbState();
        Assert.Throws<ArgumentException>(() => inventory.AddOrb(401000005));
        Assert.False(inventory.TryAddOrbWithCapacity(401000005, 8, out _));
        Assert.Empty(inventory.GetOrderedOrbs());
        Assert.Equal(0, inventory.OrbCount);
        Assert.Equal(0, inventory.GetHighestOrbTier());
    }

    [Fact]
    public void CapacityAndRemovalApplyToIndividualOrbs()
    {
        var orbs = new PlayerOrbState();
        Assert.True(orbs.TryAddOrbWithCapacity(107000010, 1, out var orb));
        Assert.False(orbs.TryAddOrbWithCapacity(107000010, 1, out _));
        Assert.True(orbs.TryRemoveOrb(orb!.ItemUid, 1, out var removed));
        Assert.Equal(0, removed!.Count);
        Assert.False(orbs.TryRemoveOrb(orb.ItemUid, 1, out _));
        Assert.True(orbs.TryAddOrbWithCapacity(107000010, 1, out var next));
        Assert.NotEqual(orb.ItemUid, next!.ItemUid);
    }
}
