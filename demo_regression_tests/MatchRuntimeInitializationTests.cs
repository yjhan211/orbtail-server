using game_server.services;
using Microsoft.Extensions.Logging.Abstractions;
using network.common;

namespace demo_regression_tests;

public sealed class MatchRuntimeInitializationTests
{
    public MatchRuntimeInitializationTests() => TestGameData.EnsureBattleItemCombatLoaded();

    [Fact]
    public void MonsterResolversAreReadyWithoutServerInitialization()
    {
        var store = new MatchRuntimeStore(NullLogger.Instance);
        var first = store.GetOrCreate(940001);
        var second = store.GetOrCreate(940002);
        Assert.NotNull(first.Monsters.IsAreaClosedResolver);
        Assert.NotNull(first.Monsters.IsGameplayActiveResolver);
        Assert.NotNull(first.Monsters.IsPlayerOrblessResolver);
        Assert.Equal(first.Closures.IsAreaClosed(AreaType.S2Ground),
            first.Monsters.IsAreaClosedResolver(first.MatchingId, AreaType.S2Ground));
        try
        {
            Assert.False(first.Monsters.IsGameplayActiveResolver(first.MatchingId));
            MatchStartGate.RegisterBotOnlyMatch(first.MatchingId);
            Assert.True(first.Monsters.IsGameplayActiveResolver(first.MatchingId));
            Assert.False(second.Monsters.IsGameplayActiveResolver!(second.MatchingId));
        }
        finally
        {
            MatchStartGate.RemoveMatching(first.MatchingId);
            MatchStartGate.RemoveMatching(second.MatchingId);
        }
    }

    [Theory]
    [InlineData(107000010, true)]
    [InlineData(107000040, true)]
    [InlineData(-1, false)]
    public void OrbOwnershipUsesOnlyOwnMatchInventory(int itemId, bool isOrb)
    {
        var store = new MatchRuntimeStore(NullLogger.Instance);
        var first = store.GetOrCreate(940003);
        var second = store.GetOrCreate(940004);
        const long playerId = 10;
        Assert.False(first.HasAnySquadOrb(playerId));
        var item = first.Inventory.AddItem(playerId, itemId);
        Assert.Equal(isOrb, first.HasAnySquadOrb(playerId));
        Assert.Equal(!isOrb, first.Monsters.IsPlayerOrblessResolver!(first.MatchingId, playerId));
        Assert.False(second.HasAnySquadOrb(playerId));
        Assert.True(second.Monsters.IsPlayerOrblessResolver!(second.MatchingId, playerId));
        item.Count = 0;
        Assert.False(first.HasAnySquadOrb(playerId));
    }
}
