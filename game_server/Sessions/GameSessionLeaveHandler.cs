using game_server.matches;
using game_server.network;
using Microsoft.Extensions.Logging;
using network.common;
using network.packets;

namespace game_server.sessions;

/// <summary>
///     종료된 게임 세션을 레지스트리에서 제거하고 같은 매치·구역의 플레이어에게 퇴장을 알린다.
///     교체된 이전 세션이 현재 세션을 제거하지 않도록 확인하며,
///     사람 세션이 남지 않은 매치의 정리는 MatchCleanupService에 맡긴다.
/// </summary>
internal sealed class GameSessionLeaveHandler(
    GameSessionRegistry sessions,
    MatchCleanupService matchCleanup,
    ILogger<GameSessionLeaveHandler> logger)
{
    public void Handle(GameClientSession session)
    {
        if (session.PlayerId.HasValue)
        {
            bool removed = sessions.Remove(session);

            if (!removed)
            {
                logger.LogDebug(
                    "Ignored removal from a superseded game session: PlayerId={PlayerId}, MatchingId={MatchingId}",
                    session.PlayerId.Value,
                    session.MatchingId);
                if (session.MatchingId > 0)
                    matchCleanup.CleanupIfNoHumanSessionsRemain(session.MatchingId);
                return;
            }

            logger.LogInformation("Game client session removed: PlayerId={SessionPlayerId}", session.PlayerId.Value);

            if (session.MatchingId > 0 && session.CurrentArea != AreaType.None)
            {
                using var leavePacket = PacketMaker.G_TO_C_AREA_PLAYER_LEAVE(session.PlayerId.Value);
                var sameAreaSessions = session.Match.Sessions.Snapshot()
                    .Where(other =>
                        !ReferenceEquals(other, session) &&
                        other.CurrentArea == session.CurrentArea)
                    .ToList();
                foreach (var other in sameAreaSessions) other.TrySend(leavePacket);

                logger.LogInformation(
                    "Broadcasted disconnected player leave: PlayerId={PlayerId}, MatchingId={MatchingId}, Area={Area}, Receivers={ReceiverCount}",
                    session.PlayerId.Value,
                    session.MatchingId,
                    session.CurrentArea,
                    sameAreaSessions.Count);
            }

            if (session.MatchingId > 0)
                matchCleanup.CleanupIfNoHumanSessionsRemain(session.MatchingId);
        }
    }
}
