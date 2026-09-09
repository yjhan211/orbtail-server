namespace game_server.matches.results;

/// <summary>매치가 종료된 이유.</summary>
public enum MatchEndReason
{
    LastSurvivor,
    LastSurvivorAfterCombat,
    LastSurvivorBeforeOvertime,
    OrbScoreTimeout,
    OvertimeSettlement,
    LastHumanLeft,
    LastSurvivorBotOnly
}
