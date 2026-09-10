using game_server.logging;
using game_server.matches.results;
using game_server.matches;
using Microsoft.Extensions.Logging;
using network.common;

namespace game_server.players;

/// <summary>
/// 매치 플레이어의 체력 변경 결과를 기록하고 필요하면 탈락시킨다. 연결이 있으면 결과도 전송한다.
/// 체력 계산은 MatchPlayer가 담당하며 모든 호출은 같은 매치 잠금 안에서 수행한다.
/// </summary>
internal sealed class PlayerHealthChangeService(
    GameEventLogManager eventLogs,
    PlayerEliminationService eliminations,
    ILogger logger)
{
    public void Handle(MatchRuntime match, MatchPlayer player, MatchPlayer.HealthChange change, long attackerPlayerId = 0,
        bool deferElimination = false)
    {
        // 값이 변경되지 않았으면 패킷 전송 안함
        if (!change.Changed) return;

        logger.LogInformation(
            "Player {PlayerId} Health: {OldHealth}→{Health} ({Delta:+#;-#;0})",
            player.PlayerId, change.Before, change.After, change.RequestedDelta);

        // 효과 표시에는 요청한 변화량을, 상태에는 적용 후 체력을 보낸다.
        player.Session?.SendHealth(change);

        // 회복량과 체력 변경 기록
        if (player.PlayerId != 0)
        {
            if (change.Recovered > 0)
                eventLogs.RecordRecovery(match.MatchingId, player.PlayerId, change.Recovered);

            eventLogs.LogResource(match.MatchingId, player.PlayerId,
                change.RequestedDelta, change.After, reason: "", isBot: player.PlayerId < 0);
        }

        if (change.IsDepleted && !deferElimination &&
            !match.IsEnded && !player.IsEliminated && player.Health <= 0)
        {
            eliminations.EliminatePlayer(match.MatchingId, player.PlayerId, EliminationReason.HEALTH_ZERO,
                attackerPlayerId: attackerPlayerId);
        }
    }
}
