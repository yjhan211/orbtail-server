namespace game_server.matches.results;

/// <summary>환경 피해 정산에서 승자를 결정한 기준.</summary>
public enum MatchTieBreakCriterion
{
    None,
    SingleCandidate,
    PreDamageHealth,
    CumulativePvpDamage,
    MatchSeedPriority
}
