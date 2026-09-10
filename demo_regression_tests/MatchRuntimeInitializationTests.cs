using game_server.field;
using game_server.items;
using game_server.matches;
using game_server.monsters;
using Microsoft.Extensions.Logging.Abstractions;
using network.common;

namespace demo_regression_tests;

public sealed class MatchRuntimeInitializationTests
{
    public MatchRuntimeInitializationTests() => TestGameData.EnsureBattleItemCombatLoaded();

    [Fact]
    public void MonsterDependenciesAreRequiredAtConstruction()
    {
        var closures = new AreaClosureManager(940001, NullLogger.Instance);
        var inventory = new InGameInventoryManager(940001, NullLogger.Instance);
        Assert.Throws<ArgumentNullException>(() => new SwarmMonsterDirector(940001, null!, inventory));
        Assert.Throws<ArgumentNullException>(() => new SwarmMonsterDirector(940001, closures, null!));
    }

    [Theory]
    [InlineData(107000010, true)]
    [InlineData(107000040, true)]
    [InlineData(-1, false)]
    public void OrbOwnershipUsesOnlyOwnMatchInventory(int itemId, bool isOrb)
    {
        var store = TestGameSessionServices.CreateMatchRuntimeStore(NullLogger.Instance);
        var first = store.GetOrCreate(940003);
        var second = store.GetOrCreate(940004);
        const long playerId = 10;
        Assert.False(first.Inventory.HasAnySquadOrb(playerId));
        var item = first.Inventory.AddItem(playerId, itemId);
        Assert.Equal(isOrb, first.Inventory.HasAnySquadOrb(playerId));
        Assert.Equal(!isOrb, first.Monsters.IsPlayerOrbless(playerId));
        Assert.False(second.Inventory.HasAnySquadOrb(playerId));
        Assert.True(second.Monsters.IsPlayerOrbless(playerId));
        Assert.Equal(isOrb ? 1 : 0, first.Inventory.GetOrbScore(playerId).OrbCount);
        Assert.Equal((0, 0), second.Inventory.GetOrbScore(playerId));
        item.Count = 0;
        Assert.Equal((0, 0), first.Inventory.GetOrbScore(playerId));
        Assert.False(first.Inventory.HasAnySquadOrb(playerId));
        Assert.True(first.Monsters.IsPlayerOrbless(playerId));
    }
}
