using game_server.services;
using Microsoft.Extensions.Logging.Abstractions;
using network.common;
using network.common.data.models;

namespace demo_regression_tests;

public sealed class MatchOwnedBotsTests
{
    [Fact]
    public void MonstersAreIsolatedAndReleasedWithTheirMatch()
    {
        var store = new MatchRuntimeStore(NullLogger.Instance, monsterSpawnEnabled: false);
        var first = store.GetOrCreate(1);
        var second = store.GetOrCreate(2);
        Assert.NotSame(first.Monsters, second.Monsters);
        Assert.False(first.Monsters.MonsterSpawnEnabled);
        Assert.True(first.Monsters.InitializeMatching(1, 10, DateTime.UtcNow));
        Assert.False(first.Monsters.InitializeMatching(2, 20, DateTime.UtcNow));
        Assert.False(second.Monsters.HasMatching(2));
        Assert.True(second.Monsters.InitializeMatching(2, 20, DateTime.UtcNow));
        using (store.Enter(first)) first.TryMarkTerminal();
        Assert.False(first.Monsters.HasMatching(1));
        Assert.True(second.Monsters.HasMatching(2));
    }

    [Fact]
    public void BotsAreIsolatedAndReleasedWithTheirMatch()
    {
        var store = new MatchRuntimeStore(NullLogger.Instance);
        var first = store.GetOrCreate(1);
        var second = store.GetOrCreate(2);
        first.Bots.RegisterBots(1, Config.SWARM_MATCH_MAP, [], new Dictionary<long, Cell>());
        first.Bots.GetBots(1).Add(new BotPlayerState { PlayerId = -1 });
        Assert.NotSame(first.Bots, second.Bots);
        Assert.Single(first.Bots.GetBots(1));
        Assert.Empty(second.Bots.GetBots(2));
        Assert.Empty(first.Bots.GetBots(2));
        Assert.Throws<InvalidOperationException>(() => first.Bots.RegisterBots(2, Config.SWARM_MATCH_MAP, [], new Dictionary<long, Cell>()));
        using (store.Enter(first)) first.TryMarkTerminal();
        Assert.Null(store.Get(1));
        Assert.Empty(first.Bots.GetBots(1));
        Assert.Same(second, store.Get(2));
    }
}
