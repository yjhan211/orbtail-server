namespace game_server.matches.results;

/// <summary>매치가 종료된 이유.</summary>
public enum MatchEndReason
{
    LastSurvivor,
    LastSurvivorAfterCombat,
    OrbScoreTimeout,
    PressureFieldSettlement,
    LastHumanLeft,
    LastSurvivorBotOnly
}

/// <summary>환경 피해 정산에서 승자를 결정한 기준.</summary>
public enum MatchTieBreakCriterion
{
    None,
    SingleCandidate,
    PreDamageHealth,
    CumulativePvpDamage,
    FieldDamage,
    PlayerId
}
