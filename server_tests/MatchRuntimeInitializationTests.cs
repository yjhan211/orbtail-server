using game_server.matches;
using game_server.matches.monsters;
using Microsoft.Extensions.Logging.Abstractions;
using network.common;

namespace server_tests;

public sealed class MatchRuntimeInitializationTests
{
    public MatchRuntimeInitializationTests() => TestGameData.EnsureBattleItemCombatLoaded();

    [Theory]
    [InlineData(107000010, true)]
    [InlineData(107000040, false)]
    [InlineData(-1, false)]
    public void OrbOwnershipUsesOnlyOwnMatchInventory(int itemId, bool isOrb)
    {
        var store = TestGameSessionServices.CreateMatchRuntimeStore(NullLogger.Instance);
        var first = store.GetOrCreate(940003);
        var second = store.GetOrCreate(940004);
        const long playerId = 10;
        Assert.False(TestGameSessionServices.Orbs(first, playerId).HasAnyOrb());
        Assert.Equal(isOrb, TestGameSessionServices.Orbs(first, playerId).TryAddOrbWithCapacity(itemId, 8, out var item));
        Assert.Equal(isOrb, TestGameSessionServices.Orbs(first, playerId).HasAnyOrb());
        Assert.Equal(!isOrb, !first.GetOrbs(playerId).HasAnyOrb());
        Assert.False(TestGameSessionServices.Orbs(second, playerId).HasAnyOrb());
        Assert.True(!second.GetOrbs(playerId).HasAnyOrb());
        Assert.Equal(isOrb ? 1 : 0, TestGameSessionServices.Orbs(first, playerId).GetOrbScore().OrbCount);
        Assert.Equal((0, 0), TestGameSessionServices.Orbs(second, playerId).GetOrbScore());
        if (item != null)
            TestGameSessionServices.Orbs(first, playerId).TryRemoveOrb(item.ItemUid, 1, out _);
        Assert.Equal((0, 0), TestGameSessionServices.Orbs(first, playerId).GetOrbScore());
        Assert.False(TestGameSessionServices.Orbs(first, playerId).HasAnyOrb());
        Assert.True(!first.GetOrbs(playerId).HasAnyOrb());
    }
}
