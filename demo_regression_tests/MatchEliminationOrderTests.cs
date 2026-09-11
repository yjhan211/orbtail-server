using game_server.matches;
using game_server.matches.field;
using game_server.matches.results;
using static game_server.matches.field.MatchEnvironmentService;

namespace demo_regression_tests;

public sealed class MatchEliminationOrderTests
{
    [Fact]
    public void Resolve_PrefersHigherPreDamageHealth()
    {
        var result = MatchEnvironmentService.ResolveEliminationOrder(195001, new[]
        {
            new MatchSettlementCandidate(1, 60, 500),
            new MatchSettlementCandidate(2, 70, 10)
        });

        Assert.Equal(2, result.BestToWorst[0].PlayerId);
        Assert.Equal(MatchTieBreakCriterion.PreDamageHealth, result.DecisiveCriterion);
    }

    [Fact]
    public void Resolve_PrefersHigherPvpDamageWhenHealthMatches()
    {
        var result = MatchEnvironmentService.ResolveEliminationOrder(195002, new[]
        {
            new MatchSettlementCandidate(1, 80, 100),
            new MatchSettlementCandidate(2, 80, 200)
        });

        Assert.Equal(2, result.BestToWorst[0].PlayerId);
        Assert.Equal(MatchTieBreakCriterion.CumulativePvpDamage, result.DecisiveCriterion);
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

        var first = MatchEnvironmentService.ResolveEliminationOrder(195003, candidates);
        var second = MatchEnvironmentService.ResolveEliminationOrder(195003, candidates.Reverse());

        Assert.Equal(MatchTieBreakCriterion.MatchSeedPriority, first.DecisiveCriterion);
        Assert.Equal(
            first.BestToWorst.Select(candidate => candidate.PlayerId),
            second.BestToWorst.Select(candidate => candidate.PlayerId));
        Assert.Equal(3, first.BestToWorst.Select(candidate => candidate.PlayerId).Distinct().Count());
    }
}
