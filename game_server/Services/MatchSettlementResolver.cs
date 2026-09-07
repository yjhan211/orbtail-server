namespace game_server.services;

public readonly record struct MatchSettlementCandidate(
    long PlayerId,
    int PreDamageHealth,
    int TotalPvpDamage);

public sealed class MatchSettlementResolution
{
    public required IReadOnlyList<MatchSettlementCandidate> BestToWorst { get; init; }
    public required string DecisiveCriterion { get; init; }
}

public static class MatchSettlementResolver
{
    public static MatchSettlementResolution Resolve(
        long matchingId,
        IEnumerable<MatchSettlementCandidate> candidates)
    {
        var ordered = candidates
            .DistinctBy(candidate => candidate.PlayerId)
            .OrderByDescending(candidate => candidate.PreDamageHealth)
            .ThenByDescending(candidate => candidate.TotalPvpDamage)
            .ThenBy(candidate => GetMatchSeedPriority(matchingId, candidate.PlayerId))
            .ThenBy(candidate => candidate.PlayerId)
            .ToList();

        string criterion = "single_candidate";
        if (ordered.Count > 1)
        {
            var first = ordered[0];
            var second = ordered[1];
            criterion = first.PreDamageHealth != second.PreDamageHealth
                ? "pre_damage_health"
                : first.TotalPvpDamage != second.TotalPvpDamage
                    ? "cumulative_pvp_damage"
                    : "match_seed_priority";
        }

        return new MatchSettlementResolution
        {
            BestToWorst = ordered,
            DecisiveCriterion = criterion
        };
    }

    internal static ulong GetMatchSeedPriority(long matchingId, long playerId)
    {
        ulong value = unchecked((ulong)matchingId) ^
                      (unchecked((ulong)playerId) + 0x9E3779B97F4A7C15UL);
        value = (value ^ (value >> 30)) * 0xBF58476D1CE4E5B9UL;
        value = (value ^ (value >> 27)) * 0x94D049BB133111EBUL;
        return value ^ (value >> 31);
    }
}
