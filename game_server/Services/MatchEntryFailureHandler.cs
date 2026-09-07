using game_server.network;
using Microsoft.Extensions.Logging;
using network.common;

namespace game_server.services;

/// <summary>
///     입장에 실패한 매치를 중단하고 관련 세션에 실패를 알린다.
///     매치 잠금 안에서 중단 여부와 대상자를 확정하고, 종료 알림은 잠금을 푼 뒤 발행한다.
///     이미 교체된 세션이나 종료된 매치의 늦은 실패가 현재 상태를 덮어쓰지 않도록 검사한다.
/// </summary>
internal sealed class MatchEntryFailureHandler(
    MatchRuntimeStore matchRuntimes,
    GameSessionRegistry sessions,
    MatchingLifecycleService lifecycle,
    ILogger logger) : IMatchEntryFailureHandler
{
    /// <summary>
    ///     입장 실패로 매치를 중단한다. 터미널 전이를 이긴 호출이 잠금 안에서 로스터 전원의 lifecycle subject
    ///     선점과 FATAL 응답·끊기를 소유하고(발행은 잠금 밖 후처리), 이미 끝난 매치에 늦게 온 호출은
    ///     자기 세션의 entry_failed 발행과 끊기만 한다 — 정상 종료가 먼저 선점한 subject는 중복 제거된다.
    /// </summary>
    public void Handle(GameClientSession session)
    {
        if (!session.PlayerId.HasValue || session.MatchingId <= 0)
            return;

        long playerId = session.PlayerId.Value;
        long matchingId = session.MatchingId;
        MatchRuntime? runtime = matchRuntimes.Get(matchingId);
        if (runtime == null)
        {
            PublishLateEntryFailure(session, playerId, matchingId);
            return;
        }

        bool wonTerminal = false;
        using (matchRuntimes.Enter(runtime))
        {
            if (!runtime.IsTerminal)
            {
                if (sessions.TryGetCurrent(playerId, out GameClientSession? currentSession) &&
                    currentSession != null &&
                    !ReferenceEquals(currentSession, session) &&
                    currentSession.MatchingId == matchingId)
                {
                    logger.LogDebug(
                        "Skipped entry-failed reservation release for superseded session: PlayerId={PlayerId}, MatchingId={MatchingId}",
                        playerId,
                        matchingId);
                    return;
                }

                wonTerminal = runtime.TryMarkTerminal();
                List<GameClientSession> affectedSessions = sessions.GetByMatch(matchingId);
                foreach (GameClientSession affectedSession in affectedSessions)
                    affectedSession.TryMarkMatchingLifecycleHandledExternally();

                IReadOnlyCollection<long> affectedPlayerIds =
                    session.MatchHumanPlayerIds.Count > 0
                        ? session.MatchHumanPlayerIds.ToArray()
                        : [playerId];
                var lifecyclePublications = new List<Action>();
                PrepareEntryFailureLifecycle(matchingId, affectedPlayerIds, lifecyclePublications);
                foreach (GameClientSession affectedSession in affectedSessions)
                {
                    try
                    {
                        affectedSession.DisconnectForEntryFailure();
                    }
                    catch (Exception ex)
                    {
                        logger.LogWarning(
                            ex,
                            "Failed to deliver entry failure disconnect: PlayerId={PlayerId}, MatchingId={MatchingId}",
                            affectedSession.PlayerId,
                            matchingId);
                    }
                }

                runtime.AfterRelease.Add(
                    () => DispatchPreparedEntryFailureLifecycle(matchingId, lifecyclePublications));
            }
        }

        if (wonTerminal)
        {
            logger.LogWarning(
                "Match aborted after client entry failure: MatchingId={MatchingId}, FailedPlayerId={PlayerId}",
                matchingId,
                playerId);
            return;
        }

        PublishLateEntryFailure(session, playerId, matchingId);
    }

    /// <summary>이미 끝난 매치에 늦게 도착한 입장 실패 — 이 세션 한 명만 발행·끊는다.</summary>
    private void PublishLateEntryFailure(GameClientSession session, long playerId, long matchingId)
    {
        lifecycle.Publish(
            MatchingLifecycleSubjects.PlayerEntryFailed,
            playerId,
            matchingId);
        try
        {
            session.DisconnectForEntryFailure();
        }
        catch (Exception ex)
        {
            logger.LogWarning(
                ex,
                "Failed to disconnect late entry failure: PlayerId={PlayerId}, MatchingId={MatchingId}",
                playerId,
                matchingId);
        }
    }

    /// <summary>
    ///     잠금 안에서 플레이어별 subject를 선점하고 발행 작업만 남긴다 — 정상 종료가 먼저 선점한 subject는
    ///     늦은 입장 중단이 덮어쓰지 못한다.
    /// </summary>
    private void PrepareEntryFailureLifecycle(
        long matchingId,
        IReadOnlyCollection<long> playerIds,
        List<Action> lifecyclePublications)
    {
        foreach (long playerId in playerIds)
        {
            try
            {
                Action? publication = lifecycle.PreparePublication(
                    MatchingLifecycleSubjects.PlayerEntryFailed,
                    playerId,
                    matchingId);
                if (publication != null)
                    lifecyclePublications.Add(publication);
            }
            catch (Exception ex)
            {
                logger.LogError(
                    ex,
                    "Failed to prepare entry failure lifecycle publication: PlayerId={PlayerId}, MatchingId={MatchingId}",
                    playerId,
                    matchingId);
            }
        }
    }

    private void DispatchPreparedEntryFailureLifecycle(
        long matchingId,
        IReadOnlyList<Action> lifecyclePublications)
    {
        foreach (Action publication in lifecyclePublications)
        {
            try
            {
                publication();
            }
            catch (Exception ex)
            {
                logger.LogError(
                    ex,
                    "Failed to dispatch prepared entry failure lifecycle: MatchingId={MatchingId}",
                    matchingId);
            }
        }
    }

}
