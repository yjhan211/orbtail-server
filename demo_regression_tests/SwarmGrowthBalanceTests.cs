using game_server.services;
using network.common;

namespace demo_regression_tests;

public sealed class SwarmGrowthBalanceTests
{
    [Theory]
    [InlineData(0, 5)]
    [InlineData(1, 7)]
    [InlineData(2, 9)]
    [InlineData(7, 19)]
    [InlineData(8, 21)]
    [InlineData(20, 21)]
    public void GrowthCostUsesFivePlusTwoNWithTwentyOneCap(int successCount, int expectedCost)
    {
        Assert.Equal(expectedCost, Config.GetSwarmGrowthCardCost(successCount, orbCount: 3));
    }

    [Theory]
    [InlineData(1)]
    [InlineData(6)]
    [InlineData(12)]
    [InlineData(30)]
    public void OrbCountDoesNotAddGrowthCost(int orbCount)
    {
        Assert.Equal(0, Config.GetSwarmGrowthScoreSurcharge(orbCount));
        Assert.Equal(11, Config.GetSwarmGrowthCardCost(growthSuccessCount: 3, orbCount));
    }

    [Fact]
    public void EmptyBoardKeepsThreeStoneRebuildPath()
    {
        Assert.Equal(3, Config.GetSwarmGrowthCardCost(growthSuccessCount: 20, orbCount: 0));
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
        int budget = SummonStoneManager.InitialSummonStoneCount + earnedStones;
        int choices = 0;
        while (budget >= Config.GetSwarmGrowthCardCost(choices, orbCount: 3))
        {
            budget -= Config.GetSwarmGrowthCardCost(choices, orbCount: 3);
            choices++;
        }

        Assert.Equal(expectedChoices, choices);
    }
}
