using game_server.services;
using network.common.data.models;

namespace demo_regression_tests;

public sealed class SwarmWaveBombRulesTests
{
    [Fact]
    public void NoMonsterTargetProducesNoBomb()
    {
        var plans = SwarmWaveBombRules.BuildPlans(
            [new SwarmWaveOrbContribution(0, 107000030)],
            [],
            1f);

        Assert.Empty(plans);
    }

    [Fact]
    public void WaveOrbsChooseDistinctTargetsBeforeRepeating()
    {
        SwarmWaveOrbContribution[] orbs =
        [
            new(0, 107000030),
            new(1, 107000031),
            new(2, 107000032)
        ];
        SwarmWaveBombTarget[] targets =
        [
            Target(1, 1f),
            Target(2, 2f),
            Target(3, 3f)
        ];

        var plans = SwarmWaveBombRules.BuildPlans(orbs, targets, 1f);

        Assert.Equal(3, plans.Count);
        Assert.Equal([1f, 2f, 3f], plans.Select(plan => plan.Position.X));
        Assert.Equal([12, 21, 30], plans.Select(plan => plan.Damage));
        Assert.Equal([1.8f, 2.2f, 2.6f], plans.Select(plan => plan.Radius));
    }

    [Fact]
    public void MoreThanSixWaveOrbsFoldIntoSixTelegraphs()
    {
        var orbs = Enumerable.Range(0, 8)
            .Select(index => new SwarmWaveOrbContribution(index, 107000030))
            .ToArray();
        var targets = Enumerable.Range(0, 8)
            .Select(index => Target(index + 1, index + 1))
            .ToArray();

        var plans = SwarmWaveBombRules.BuildPlans(orbs, targets, 1f);

        Assert.Equal(SwarmWaveBombRules.MaxVisibleTelegraphs, plans.Count);
        Assert.Equal(96, plans.Sum(plan => plan.Damage));
        Assert.Equal(24, plans[0].Damage);
        Assert.Equal(24, plans[1].Damage);
        Assert.All(plans.Skip(2), plan => Assert.Equal(12, plan.Damage));
    }

    [Fact]
    public void SunPassiveScalesWaveBombDamage()
    {
        var plans = SwarmWaveBombRules.BuildPlans(
            [new SwarmWaveOrbContribution(0, 107000030)],
            [Target(1, 1f)],
            1.5f);

        Assert.Equal(18, Assert.Single(plans).Damage);
    }

    private static SwarmWaveBombTarget Target(long id, float x) =>
        new(id, new Vector3f(x, 0f, 0f));
}
