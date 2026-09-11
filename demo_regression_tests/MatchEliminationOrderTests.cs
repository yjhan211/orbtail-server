using game_server.matches;
using static game_server.matches.MatchFieldService;

namespace demo_regression_tests;

public sealed class MatchEliminationOrderTests
{
    [Fact]
    public void Resolve_PrefersHigherPreDamageHealth()
    {
        var result = MatchFieldService.ResolveEliminationOrder(new[]
        {
            new MatchSettlementCandidate(1, 60, 500, 12),
            new MatchSettlementCandidate(2, 70, 10, 40)
        });

        Assert.Equal(2, result.BestToWorst[0].PlayerId);
        Assert.Equal(MatchTieBreakCriterion.PreDamageHealth, result.DecisiveCriterion);
    }

    [Fact]
    public void Resolve_PrefersHigherPvpDamageWhenHealthMatches()
    {
        var result = MatchFieldService.ResolveEliminationOrder(new[]
        {
            new MatchSettlementCandidate(1, 80, 100, 12),
            new MatchSettlementCandidate(2, 80, 200, 40)
        });

        Assert.Equal(2, result.BestToWorst[0].PlayerId);
        Assert.Equal(MatchTieBreakCriterion.CumulativePvpDamage, result.DecisiveCriterion);
    }

    [Fact]
    public void Resolve_PrefersSmallerFieldDamageWhenHealthAndPvpDamageMatch()
    {
        var result = MatchFieldService.ResolveEliminationOrder(new[]
        {
            new MatchSettlementCandidate(1, 80, 200, 40),
            new MatchSettlementCandidate(2, 80, 200, 12)
        });

        Assert.Equal(2, result.BestToWorst[0].PlayerId);
        Assert.Equal(MatchTieBreakCriterion.FieldDamage, result.DecisiveCriterion);
    }

    [Fact]
    public void Resolve_FallsBackToHumansFirstThenPlayerId()
    {
        var result = MatchFieldService.ResolveEliminationOrder(new[]
        {
            new MatchSettlementCandidate(-3, 90, 300, 17),
            new MatchSettlementCandidate(12, 90, 300, 17),
            new MatchSettlementCandidate(11, 90, 300, 17)
        });

        Assert.Equal(new long[] { 11, 12, -3 }, result.BestToWorst.Select(candidate => candidate.PlayerId));
        Assert.Equal(MatchTieBreakCriterion.PlayerId, result.DecisiveCriterion);
    }
}
