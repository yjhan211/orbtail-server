using game_server.matches;
using game_server.services;
using game_server.sessions;
using Microsoft.Extensions.Logging;
using network.common;

namespace game_server.players;

/// <summary>
/// 한 플레이어의 체력 변경 결과를 전송·기록하고 필요하면 탈락시킨다.
/// 체력 계산은 PlayerCondition이 담당하며 모든 호출은 같은 매치 잠금 안에서 수행한다.
/// </summary>
internal sealed class PlayerHealthChangeService(
    GameClientSession session,
    GameEventLogManager eventLogs,
    MatchEliminationService eliminations,
    ILogger logger)
{


    public void Handle(PlayerCondition.HealthChange change, long attackerPlayerId = 0,
        bool isAreaClosureElimination = false, bool isOvertimeElimination = false, bool deferElimination = false)
    {
        // 값이 변경되지 않았으면 패킷 전송 안함
        if (!change.Changed) return;

        logger.LogInformation(
            "Player {PlayerId} Health: {OldHealth}→{Health} ({Delta:+#;-#;0})",
            session.PlayerId, change.Before, change.After, change.RequestedDelta);

        // 효과 표시에는 요청한 변화량을, 상태에는 적용 후 체력을 보낸다.
        session.SendHealth(change);

        // 회복량과 체력 변경 기록
        if (session.PlayerId.HasValue)
        {
            if (change.Recovered > 0)
                eventLogs.RecordRecovery(session.MatchingId, session.PlayerId.Value, change.Recovered);

            eventLogs.LogResource(session.MatchingId, session.PlayerId.Value,
                change.RequestedDelta, change.After, reason: "", isBot: false);
        }

        if (change.IsDepleted && !deferElimination &&
            session.PlayerId.HasValue && !session.IsGameEnded && !session.IsEliminated && session.CurrentHealth <= 0)
        {
            eliminations.Process(session.MatchingId, session.PlayerId.Value, EliminationReason.HEALTH_ZERO,
                attackerPlayerId: attackerPlayerId,
                isAreaClosureElimination: isAreaClosureElimination,
                isOvertimeElimination: isOvertimeElimination);
        }
    }
}
