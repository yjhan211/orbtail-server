using game_server.services;

namespace demo_regression_tests;

public sealed class MatchSettlementResolverTests
{
    [Fact]
    public void Resolve_PrefersLowerPreDamageCorruption()
    {
        var result = MatchSettlementResolver.Resolve(195001, new[]
        {
            new MatchSettlementCandidate(1, 70, 500),
            new MatchSettlementCandidate(2, 60, 10)
        });

        Assert.Equal(2, result.BestToWorst[0].PlayerId);
        Assert.Equal("pre_damage_corruption", result.DecisiveCriterion);
    }

    [Fact]
    public void Resolve_PrefersHigherPvpDamageWhenCorruptionMatches()
    {
        var result = MatchSettlementResolver.Resolve(195002, new[]
        {
            new MatchSettlementCandidate(1, 80, 100),
            new MatchSettlementCandidate(2, 80, 200)
        });

        Assert.Equal(2, result.BestToWorst[0].PlayerId);
        Assert.Equal("cumulative_pvp_damage", result.DecisiveCriterion);
    }

    [Fact]
    public void Resolve_UsesStableMatchSeedPriorityAsFinalTieBreaker()
    {
        var candidates = new[]
        {
            new MatchSettlementCandidate(11, 90, 300),
            new MatchSettlementCandidate(12, 90, 300),
            new MatchSettlementCandidate(13, 90, 300)
        };

        var first = MatchSettlementResolver.Resolve(195003, candidates);
        var second = MatchSettlementResolver.Resolve(195003, candidates.Reverse());

        Assert.Equal("match_seed_priority", first.DecisiveCriterion);
        Assert.Equal(
            first.BestToWorst.Select(candidate => candidate.PlayerId),
            second.BestToWorst.Select(candidate => candidate.PlayerId));
        Assert.Equal(3, first.BestToWorst.Select(candidate => candidate.PlayerId).Distinct().Count());
    }
}
