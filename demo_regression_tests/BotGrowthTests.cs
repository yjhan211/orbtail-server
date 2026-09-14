using game_server.matches;
using game_server.matches.monsters;
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
        var human = new Player { Profile = new network.common.data.models.PlayerInfo { PlayerId = 1 } };
        match.RegisterParticipant(human);
        var growth = new PlayerOrbGrowthService(NullLogger<PlayerOrbGrowthService>.Instance);
        var decisions = new BotBehaviorService(growth, new PlayerInteractionService(), NullLogger<BotBehaviorService>.Instance);
        var bot = new Bot { PlayerId = -1 };
        match.RegisterParticipant(bot.Player);
        using (match.Enter())
        {
            TestGameSessionServices.AddSummonStones(match, -1, 100);
            TestGameSessionServices.AddSummonStones(match, 1, 100);
            for (int i = 0; i < 3; i++)
            {
                decisions.ProcessOrbGrowth(match, [bot]);
                Assert.True(growth.Summon(match, human).Success);
                Assert.Equivalent(TestGameSessionServices.SummonStones(match, 1), TestGameSessionServices.SummonStones(match, -1));
            }
            Assert.Equal(3, TestGameSessionServices.Orbs(match, -1).GetOrbScore().OrbCount);
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
        var growth = new PlayerOrbGrowthService(NullLogger<PlayerOrbGrowthService>.Instance);
        var decisions = new BotBehaviorService(growth, new PlayerInteractionService(), NullLogger<BotBehaviorService>.Instance);
        var bot = new Bot { PlayerId = -1 };
        match.RegisterParticipant(bot.Player);
        using (match.Enter())
        {
            for (int i = 0; i < Config.SWARM_ORB_CAPACITY; i++)
                Assert.True(TestGameSessionServices.Orbs(match, -1).TryAddItemWithCapacity(itemId, Config.SWARM_ORB_CAPACITY, out _));
            int cost = growth.GetNextOrbGrowthCost(match, bot.Player);
            TestGameSessionServices.AddSummonStones(match, -1, cost);
            decisions.ProcessOrbGrowth(match, [bot]);
            Assert.Equal(0, TestGameSessionServices.SummonStones(match, -1).StoneCount);
            Assert.Single(TestGameSessionServices.Orbs(match, -1).GetAllItems(), item => item.ItemId == upgradedItemId);
            Assert.Equal(Config.SWARM_ORB_CAPACITY, TestGameSessionServices.Orbs(match, -1).GetOrbScore().OrbCount);
        }
    }

    [Fact]
    public void InsufficientStonesAndEliminatedBotDoNotGrow()
    {
        UserServerMatchingTestData.EnsureGameDataLoaded();
        var store = TestGameSessionServices.CreateMatchRuntimeStore(NullLogger.Instance);
        var match = store.GetOrCreate(949103);
        var growth = new PlayerOrbGrowthService(NullLogger<PlayerOrbGrowthService>.Instance);
        var decisions = new BotBehaviorService(growth, new PlayerInteractionService(), NullLogger<BotBehaviorService>.Instance);
        var bot = new Bot { PlayerId = -1 };
        match.RegisterParticipant(bot.Player);
        using (match.Enter())
        {
            decisions.ProcessOrbGrowth(match, [bot]);
            Assert.Empty(TestGameSessionServices.Orbs(match, -1).GetAllItems());
            TestGameSessionServices.AddSummonStones(match, -1, 100);
            bot.Player.Status = PlayerMatchStatus.ELIMINATED;
            decisions.ProcessOrbGrowth(match, [bot]);
            Assert.Empty(TestGameSessionServices.Orbs(match, -1).GetAllItems());
            Assert.Equal(100, TestGameSessionServices.SummonStones(match, -1).StoneCount);
        }
    }
}
