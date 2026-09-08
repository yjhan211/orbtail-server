using game_server.matches;
using game_server.services;
using Microsoft.Extensions.Logging.Abstractions;
using network.common;
using network.common.data.models;

namespace demo_regression_tests;

public sealed class MatchOwnedBotsTests
{
    [Fact]
    public void BotMovementPlanIsBoundToItsMatchAndRejectsReleasedWork()
    {
        var store = TestGameSessionServices.CreateMatchRuntimeStore(NullLogger.Instance);
        var first = store.GetOrCreate(1);
        var second = store.GetOrCreate(2);
        var logs = new GameEventLogManager(id => store.GetOrNull(id)?.EventLog);
        Assert.NotSame(first.Bots, second.Bots);
        using (MatchRuntimeStore.Enter(first))
            Assert.Equal(1L, first.Bots.PrepareMovementTick(first.Closures, first.Inventory, first.GroundItems, first.SummonStones, first.Encounters, logs, [], (_, _) => default).MatchingId);
        using (MatchRuntimeStore.Enter(second))
            Assert.Equal(2L, second.Bots.PrepareMovementTick(second.Closures, second.Inventory, second.GroundItems, second.SummonStones, second.Encounters, logs, [], (_, _) => default).MatchingId);
        using (MatchRuntimeStore.Enter(first))
        {
            first.TryMarkEnded();
            var movementService = new BotMovementService(logs, NullLogger<BotMovementService>.Instance);
            Assert.Throws<InvalidOperationException>(() => movementService.Process(first, (_, _) => default));
        }
        Assert.Throws<InvalidOperationException>(() => first.Bots.PrepareMovementTick(first.Closures, first.Inventory, first.GroundItems, first.SummonStones, first.Encounters, logs, [], (_, _) => default));
        using (MatchRuntimeStore.Enter(second))
            Assert.Equal(2L, second.Bots.PrepareMovementTick(second.Closures, second.Inventory, second.GroundItems, second.SummonStones, second.Encounters, logs, [], (_, _) => default).MatchingId);
    }

    [Fact]
    public void MonstersAreIsolatedAndReleasedWithTheirMatch()
    {
        var store = TestGameSessionServices.CreateMatchRuntimeStore(NullLogger.Instance);
        var first = store.GetOrCreate(1);
        var second = store.GetOrCreate(2);
        Assert.NotSame(first.Monsters, second.Monsters);
        Assert.True(first.Monsters.InitializeMatching(1, 10, DateTime.UtcNow));
        Assert.False(first.Monsters.InitializeMatching(2, 20, DateTime.UtcNow));
        Assert.False(second.Monsters.HasMatching(2));
        Assert.True(second.Monsters.InitializeMatching(2, 20, DateTime.UtcNow));
        using (MatchRuntimeStore.Enter(first)) first.TryMarkEnded();
        Assert.False(first.Monsters.HasMatching(1));
        Assert.True(second.Monsters.HasMatching(2));
    }

    [Fact]
    public void BotsAreIsolatedAndReleasedWithTheirMatch()
    {
        var store = TestGameSessionServices.CreateMatchRuntimeStore(NullLogger.Instance);
        var first = store.GetOrCreate(1);
        var second = store.GetOrCreate(2);
        first.Bots.RegisterBots(1, Config.SWARM_MATCH_MAP, [], new Dictionary<long, Cell>());
        first.Bots.GetBots(1).Add(new BotPlayerState { PlayerId = -1 });
        Assert.NotSame(first.Bots, second.Bots);
        Assert.Single(first.Bots.GetBots(1));
        Assert.Empty(second.Bots.GetBots(2));
        Assert.Empty(first.Bots.GetBots(2));
        Assert.Throws<InvalidOperationException>(() => first.Bots.RegisterBots(2, Config.SWARM_MATCH_MAP, [], new Dictionary<long, Cell>()));
        using (MatchRuntimeStore.Enter(first)) first.TryMarkEnded();
        Assert.Null(store.GetOrNull(1));
        Assert.Empty(first.Bots.GetBots(1));
        Assert.Same(second, store.GetOrNull(2));
    }
}
