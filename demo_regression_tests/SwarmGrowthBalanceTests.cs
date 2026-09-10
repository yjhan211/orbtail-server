using game_server.items;
using game_server.players;
using network.common;

namespace demo_regression_tests;

public sealed class SwarmGrowthBalanceTests
{
    public SwarmGrowthBalanceTests()
    {
        TestGameData.EnsureBattleItemCombatLoaded();
    }
    [Theory]
    [InlineData(0, 5)]
    [InlineData(1, 7)]
    [InlineData(2, 9)]
    [InlineData(7, 19)]
    [InlineData(8, 21)]
    [InlineData(20, 21)]
    public void GrowthCostUsesFivePlusTwoNWithTwentyOneCap(int successCount, int expectedCost)
    {
        Assert.Equal(expectedCost, Math.Min(Config.SWARM_GROWTH_COST_CAP, Config.GetSwarmGrowthBaseCost(successCount)));
    }

    [Theory]
    [InlineData(35, 4)]
    [InlineData(48, 5)]
    [InlineData(57, 6)]
    [InlineData(79, 7)]
    [InlineData(94, 8)]
    [InlineData(121, 9)]
    public void FiveMinuteStoneIncomeProducesExpectedChoiceCount(int earnedStones, int expectedChoices)
    {
        int budget = PlayerOrbGrowthService.InitialSummonStoneCount + earnedStones;
        int choices = 0;
        while (budget >= Math.Min(Config.SWARM_GROWTH_COST_CAP, Config.GetSwarmGrowthBaseCost(choices)))
        {
            budget -= Math.Min(Config.SWARM_GROWTH_COST_CAP, Config.GetSwarmGrowthBaseCost(choices));
            choices++;
        }

        Assert.Equal(expectedChoices, choices);
    }
}
