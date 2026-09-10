using game_server.items;
using game_server.orbs;
using game_server.players;
using game_server.players.bots;
using Microsoft.Extensions.Logging.Abstractions;
using network.common;

namespace demo_regression_tests;

public sealed class BotGrowthTests
{
    [Fact]
    public void BotSummonUsesHumanCostAndCounter()
    {
        UserServerMatchingTestData.EnsureGameDataLoaded();
        var store = TestGameSessionServices.CreateMatchRuntimeStore(NullLogger.Instance);
        var match = store.GetOrCreate(949101);
        var logs = TestGameEventLogs.Create();
        var growth = new PlayerOrbGrowthService(store, logs, NullLogger<PlayerOrbGrowthService>.Instance);
        var bot = new BotPlayerState { PlayerId = -1 };
        using (match.Enter())
        {
            match.SummonStones.AddStones(-1, 100);
            match.SummonStones.AddStones(1, 100);
            for (int i = 0; i < 3; i++)
            {
                growth.ProcessBotOrbGrowth(match.MatchingId, [bot]);
                Assert.True(growth.Summon(match, 1, AreaType.None).Success);
                Assert.Equal(match.SummonStones.GetSnapshot(1), match.SummonStones.GetSnapshot(-1));
            }
            Assert.Equal(3, match.Inventory.GetOrbScore(-1).OrbCount);
            Assert.Empty(match.TrailCombat.OrbDurabilityBonus);
        }
    }

    [Theory]
    [InlineData(107000010, 107000011)]
    [InlineData(107000020, 107000021)]
    [InlineData(107000030, 107000031)]
    public void FullBotBoardUpgradesCurrentFamily(int itemId, int upgradedItemId)
    {
        UserServerMatchingTestData.EnsureGameDataLoaded();
        var store = TestGameSessionServices.CreateMatchRuntimeStore(NullLogger.Instance);
        var match = store.GetOrCreate(949102);
        var logs = TestGameEventLogs.Create();
        var growth = new PlayerOrbGrowthService(store, logs, NullLogger<PlayerOrbGrowthService>.Instance);
        var bot = new BotPlayerState { PlayerId = -1 };
        using (match.Enter())
        {
            for (int i = 0; i < Config.SWARM_ORB_CAPACITY; i++)
                Assert.True(match.Inventory.TryAddItemWithCapacity(-1, itemId, Config.SWARM_ORB_CAPACITY, out _));
            int cost = growth.GetNextOrbGrowthCost(match.MatchingId, -1);
            match.SummonStones.AddStones(-1, cost);
            growth.ProcessBotOrbGrowth(match.MatchingId, [bot]);
            Assert.Equal(0, match.SummonStones.GetSnapshot(-1).StoneCount);
            Assert.Single(match.Inventory.GetAllItems(-1), item => item.ItemId == upgradedItemId);
            Assert.Equal(Config.SWARM_ORB_CAPACITY, match.Inventory.GetOrbScore(-1).OrbCount);
            Assert.Empty(match.TrailCombat.OrbDurabilityBonus);
        }
    }

    [Fact]
    public void InsufficientStonesAndEliminatedBotDoNotGrow()
    {
        UserServerMatchingTestData.EnsureGameDataLoaded();
        var store = TestGameSessionServices.CreateMatchRuntimeStore(NullLogger.Instance);
        var match = store.GetOrCreate(949103);
        var logs = TestGameEventLogs.Create();
        var growth = new PlayerOrbGrowthService(store, logs, NullLogger<PlayerOrbGrowthService>.Instance);
        var bot = new BotPlayerState { PlayerId = -1 };
        using (match.Enter())
        {
            growth.ProcessBotOrbGrowth(match.MatchingId, [bot]);
            Assert.Empty(match.Inventory.GetAllItems(-1));
            match.SummonStones.AddStones(-1, 100);
            bot.Player.Status = PlayerMatchStatus.ELIMINATED;
            growth.ProcessBotOrbGrowth(match.MatchingId, [bot]);
            Assert.Empty(match.Inventory.GetAllItems(-1));
            Assert.Equal(100, match.SummonStones.GetSnapshot(-1).StoneCount);
        }
    }
}
