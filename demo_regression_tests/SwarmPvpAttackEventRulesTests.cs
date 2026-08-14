using game_server.services;
using network.common.data;

namespace demo_regression_tests;

public sealed class SwarmPvpAttackEventRulesTests
{
    [Theory]
    [InlineData(SurvivorOrbColor.Red, 1, 8)]
    [InlineData(SurvivorOrbColor.Red, 2, 14)]
    [InlineData(SurvivorOrbColor.Red, 3, 20)]
    [InlineData(SurvivorOrbColor.Green, 1, 5)]
    [InlineData(SurvivorOrbColor.Green, 2, 9)]
    [InlineData(SurvivorOrbColor.Green, 3, 13)]
    [InlineData(SurvivorOrbColor.Blue, 1, 10)]
    [InlineData(SurvivorOrbColor.Blue, 2, 17)]
    [InlineData(SurvivorOrbColor.Blue, 3, 24)]
    public void PerOrbDamageMatchesStageSixTable(
        SurvivorOrbColor color,
        int tier,
        int expected)
    {
        Assert.Equal(expected, SwarmPvpAttackEventRules.GetPerOrbEventDamage(color, tier));
    }

    [Fact]
    public void DamageCapsAreAppliedPerAttributeEvent()
    {
        Assert.Equal(105, SwarmPvpAttackEventRules.CapDamage(SurvivorOrbColor.Red, 999));
        Assert.Equal(105, SwarmPvpAttackEventRules.CapDamage(SurvivorOrbColor.Green, 999));
        Assert.Equal(126, SwarmPvpAttackEventRules.CapDamage(SurvivorOrbColor.Blue, 999));
    }

    [Fact]
    public void TierDoesNotChangeEventFrequency()
    {
        Assert.Equal(2.4d, SwarmPvpAttackEventRules.GetIntervalSeconds(SurvivorOrbColor.Red));
        Assert.Equal(1.6d, SwarmPvpAttackEventRules.GetIntervalSeconds(SurvivorOrbColor.Green));
        Assert.Equal(2.8d, SwarmPvpAttackEventRules.GetIntervalSeconds(SurvivorOrbColor.Blue));
    }

    [Theory]
    [InlineData(1, 2.6f)]
    [InlineData(2, 3.0f)]
    [InlineData(3, 3.4f)]
    public void WaveRadiusUsesHighestParticipatingTier(int tier, float expected)
    {
        Assert.Equal(expected, SwarmPvpAttackEventRules.GetWaveRadius(tier));
    }

    [Fact]
    public void ProjectilePresentationNeverExceedsTwelveVisuals()
    {
        Assert.Equal(12,
            SwarmPvpAttackEventRules.GetVisualProjectileCount(SurvivorOrbColor.Red, 30));
        Assert.Equal(12,
            SwarmPvpAttackEventRules.GetVisualProjectileCount(SurvivorOrbColor.Green, 30));
        Assert.Equal(6,
            SwarmPvpAttackEventRules.GetVisualProjectileCount(SurvivorOrbColor.Green, 2));
    }
}
