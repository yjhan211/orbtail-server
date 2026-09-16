using game_server.matches;
using game_server.sessions;
using Microsoft.Extensions.Logging;
using network.common;

namespace game_server.matches;

/// <summary>
///     게임 입장에 실패하면 해당 매치를 중단하고 참가자들의 연결을 종료한다.
///     매치 잠금 안에서 종료 사유와 대상자를 확정하고,
///     잠금을 푼 뒤 서버 간 실패 알림과 매칭 예약 정리를 시작한다.
///     교체된 이전 세션의 실패가 현재 세션의 매치를 중단하지 않도록 확인한다.
/// </summary>
internal sealed class MatchEntryFailureHandler(
    MatchRuntimeStore matchRuntimes,
    GameSessionRegistry sessions,
    MatchSessionCleanupService lifecycle,
    ILogger logger) : IMatchEntryFailureHandler
{
    public void Handle(GameClientSession session)
    {
        if (!session.PlayerId.HasValue || session.MatchingId <= 0)
        {
            return;
        }

        long playerId = session.PlayerId.Value;
        long matchingId = session.MatchingId;
        var runtime = matchRuntimes.GetOrNull(matchingId);
        if (runtime == null)
        {
            HandleLateEntryFailure(session, playerId, matchingId);
            return;
        }

        bool isFirstEndRequest = false;
        using (runtime.Enter())
        {
            if (!runtime.IsEnded)
            {
                if (sessions.TryGetSession(playerId, out var currentSession) && currentSession != null && !ReferenceEquals(currentSession, session) && currentSession.MatchingId == matchingId)
                {
                    logger.LogDebug("Skipped entry-failed reservation release for superseded session: PlayerId={PlayerId}, MatchingId={MatchingId}", playerId, matchingId);
                    return;
                }
                isFirstEndRequest = AbortMatchForEntryFailure(runtime, playerId);
            }
        }

        if (isFirstEndRequest)
        {
            logger.LogWarning("Match aborted after client entry failure: MatchingId={MatchingId}, FailedPlayerId={PlayerId}", matchingId, playerId);
            return;
        }

        HandleLateEntryFailure(session, playerId, matchingId);
    }

    public bool AbortMatchForEntryFailure(MatchRuntime runtime, long? failedPlayerId = null)
    {
        using (runtime.Enter())
        {
            if (runtime.IsEnded || !ReferenceEquals(matchRuntimes.GetOrNull(runtime.MatchingId), runtime))
            {
                return false;
            }

            if (!failedPlayerId.HasValue && runtime.StartsAtUtc.HasValue)
            {
                return false;
            }

            if (!runtime.TryMarkEnded())
            {
                return false;
            }

            long matchingId = runtime.MatchingId;
            var affectedSessions = runtime.GetSessions();
            foreach (var session in affectedSessions)
            {
                session.MarkMatchEndHandledExternally();
            }

            var affectedPlayerIds = new HashSet<long>();
            if (runtime.IsSetupComplete)
            {
                foreach (var participant in runtime.GetPlayers())
                {
                    affectedPlayerIds.Add(participant.PlayerId);
                }
            }
            else
            {
                foreach (var session in affectedSessions)
                {
                    if (session.PlayerId.HasValue)
                    {
                        affectedPlayerIds.Add(session.PlayerId.Value);
                    }
                }

                if (failedPlayerId.HasValue)
                {
                    affectedPlayerIds.Remove(failedPlayerId.Value);
                }
            }

            var failureNotifications = new List<Action>();
            PrepareFailureNotifications(matchingId, affectedPlayerIds, failureNotifications);
            foreach (var session in affectedSessions)
            {
                try
                {
                    session.DisconnectForEntryFailure();
                }
                catch (Exception ex)
                {
                    logger.LogWarning(ex, "Failed to deliver entry failure disconnect: PlayerId={PlayerId}, MatchingId={MatchingId}", session.PlayerId, matchingId);
                }
            }
            runtime.AfterRelease.Add(() => SendFailureNotifications(matchingId, failureNotifications));
            if (!failedPlayerId.HasValue)
            {
                logger.LogWarning("Match aborted after entry deadline expired: MatchingId={MatchingId}", runtime.MatchingId);
            }
            return true;
        }
    }

    private void HandleLateEntryFailure(GameClientSession session, long playerId, long matchingId)
    {
        lifecycle.Publish(MatchingLifecycleSubjects.PlayerEntryFailed, playerId, matchingId);
        try
        {
            session.DisconnectForEntryFailure();
        }
        catch (Exception ex)
        {
            logger.LogWarning(ex, "Failed to disconnect late entry failure: PlayerId={PlayerId}, MatchingId={MatchingId}", playerId, matchingId);
        }
    }

    private void PrepareFailureNotifications(long matchingId, IReadOnlyCollection<long> playerIds, List<Action> failureNotifications)
    {
        foreach (long playerId in playerIds)
        {
            try
            {
                var publication = lifecycle.PrepareNotification(MatchingLifecycleSubjects.PlayerEntryFailed, playerId, matchingId);
                if (publication != null)
                {
                    failureNotifications.Add(publication);
                }
            }
            catch (Exception ex)
            {
                logger.LogError(ex, "Failed to prepare entry failure lifecycle publication: PlayerId={PlayerId}, MatchingId={MatchingId}", playerId, matchingId);
            }
        }
    }

    private void SendFailureNotifications(long matchingId, IReadOnlyList<Action> failureNotifications)
    {
        foreach (var publication in failureNotifications)
        {
            try
            {
                publication();
            }
            catch (Exception ex)
            {
                logger.LogError(ex, "Failed to dispatch prepared entry failure lifecycle: MatchingId={MatchingId}", matchingId);
            }
        }
    }
}
