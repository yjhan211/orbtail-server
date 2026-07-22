namespace game_server.services;

public readonly record struct SurvivorSettlementCandidate(
    long PlayerId,
    int PreDamageCorruption,
    int TotalPvpDamage);

public sealed class SurvivorSettlementResolution
{
    public required IReadOnlyList<SurvivorSettlementCandidate> BestToWorst { get; init; }
    public required string DecisiveCriterion { get; init; }
}

public static class SurvivorSettlementResolver
{
    public static SurvivorSettlementResolution Resolve(
        long matchingId,
        IEnumerable<SurvivorSettlementCandidate> candidates)
    {
        var ordered = candidates
            .DistinctBy(candidate => candidate.PlayerId)
            .OrderBy(candidate => candidate.PreDamageCorruption)
            .ThenByDescending(candidate => candidate.TotalPvpDamage)
            .ThenBy(candidate => GetMatchSeedPriority(matchingId, candidate.PlayerId))
            .ThenBy(candidate => candidate.PlayerId)
            .ToList();

        string criterion = "single_candidate";
        if (ordered.Count > 1)
        {
            var first = ordered[0];
            var second = ordered[1];
            criterion = first.PreDamageCorruption != second.PreDamageCorruption
                ? "pre_damage_corruption"
                : first.TotalPvpDamage != second.TotalPvpDamage
                    ? "cumulative_pvp_damage"
                    : "match_seed_priority";
        }

        return new SurvivorSettlementResolution
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
