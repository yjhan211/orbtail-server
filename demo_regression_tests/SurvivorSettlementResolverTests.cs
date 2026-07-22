using game_server.services;

namespace demo_regression_tests;

public sealed class SurvivorSettlementResolverTests
{
    [Fact]
    public void Resolve_PrefersLowerPreDamageCorruption()
    {
        var result = SurvivorSettlementResolver.Resolve(195001, new[]
        {
            new SurvivorSettlementCandidate(1, 70, 500),
            new SurvivorSettlementCandidate(2, 60, 10)
        });

        Assert.Equal(2, result.BestToWorst[0].PlayerId);
        Assert.Equal("pre_damage_corruption", result.DecisiveCriterion);
    }

    [Fact]
    public void Resolve_PrefersHigherPvpDamageWhenCorruptionMatches()
    {
        var result = SurvivorSettlementResolver.Resolve(195002, new[]
        {
            new SurvivorSettlementCandidate(1, 80, 100),
            new SurvivorSettlementCandidate(2, 80, 200)
        });

        Assert.Equal(2, result.BestToWorst[0].PlayerId);
        Assert.Equal("cumulative_pvp_damage", result.DecisiveCriterion);
    }

    [Fact]
    public void Resolve_UsesStableMatchSeedPriorityAsFinalTieBreaker()
    {
        var candidates = new[]
        {
            new SurvivorSettlementCandidate(11, 90, 300),
            new SurvivorSettlementCandidate(12, 90, 300),
            new SurvivorSettlementCandidate(13, 90, 300)
        };

        var first = SurvivorSettlementResolver.Resolve(195003, candidates);
        var second = SurvivorSettlementResolver.Resolve(195003, candidates.Reverse());

        Assert.Equal("match_seed_priority", first.DecisiveCriterion);
        Assert.Equal(
            first.BestToWorst.Select(candidate => candidate.PlayerId),
            second.BestToWorst.Select(candidate => candidate.PlayerId));
        Assert.Equal(3, first.BestToWorst.Select(candidate => candidate.PlayerId).Distinct().Count());
    }
}
