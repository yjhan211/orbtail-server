using game_server.network;
using game_server.services;
using Microsoft.Extensions.Logging;

namespace game_server.matches;

/// <summary>
///     사람 세션이 남지 않은 매치를 종료하고 결과 로그와 요약을 정리한다.
///     매치 잠금 안에서 사람 세션 유무를 다시 확인하고 종료와 요약 캡처를 한 번만 수행한다.
///     파일 저장은 최외곽 매치 잠금이 해제된 뒤 실행한다. 세션 등록·제거 자체는 담당하지 않는다.
/// </summary>
internal sealed class MatchCleanupService(
    MatchRuntimeStore matchRuntimes,
    GameEventLogManager eventLogs,
    MatchSummaryFileStore summaryFileStore,
    ILogger logger)
{
    public void EndBotOnlyMatchIfSettled(long matchingId, long winnerPlayerId)
    {
        if (matchRuntimes.GetOrNull(matchingId)?.Sessions.HasSessions == true)
            return;

        CleanupIfNoHumanSessionsRemain(matchingId, "last_survivor_bot_only", winnerPlayerId);
    }

    public void CleanupIfNoHumanSessionsRemain(long matchingId) =>
        CleanupIfNoHumanSessionsRemain(matchingId, "last_human_left", 0);

    /// <summary>
    ///     사람 세션이 하나도 남지 않은 매치를 잠금 안에서 터미널로 표시한다. 마지막 이벤트·요약은 잠금 안에서
    ///     캡처하고 파일 쓰기는 잠금 밖 후처리로 돈다. 사람 수신자가 없으므로 결과 패킷은 없다.
    /// </summary>
    private void CleanupIfNoHumanSessionsRemain(long matchingId, string endReason, long winnerPlayerId)
    {
        if (matchRuntimes.GetOrNull(matchingId)?.Sessions.HasSessions == true)
            return;

        var runtime = matchRuntimes.GetOrNull(matchingId);
        if (runtime == null)
            return;

        using var scope = runtime.Enter();
        if (runtime.IsEnded || runtime.Sessions.HasSessions)
            return;

        runtime.TryMarkEnded();
        if (eventLogs.TryBeginFinalization(matchingId))
        {
            var endedAtUtc = DateTime.UtcNow;
            var startedAtUtc =
                runtime.Closures.GetMatchingState()?.GameStartTime ?? endedAtUtc;
            var finalPlayerStats = runtime.Roster.BuildGameResult()
                .Select(row =>
                {
                    var stats = eventLogs.GetResultStats(matchingId, row.playerId);
                    var survivalEndUtc = row.eliminatedAt ?? endedAtUtc;
                    return new MatchFinalPlayerStats(
                        row.playerId,
                        row.eliminationRank,
                        Math.Max(0, (int)Math.Floor((survivalEndUtc - startedAtUtc).TotalSeconds)),
                        stats.KillCount + stats.MonsterKillCount,
                        stats.TotalDamageDealt + stats.MonsterDamageDealt,
                        stats.TotalRecovery,
                        // 승점 (#229): 사람이 나간 매치도 오브 수를 남긴다 — 봇 매치가 유일한
                        // 자동 검증 창구라 여기서 빠지면 결과 집계를 로그로 확인할 수 없다.
                        runtime.Inventory.GetOrbScore(row.playerId).OrbCount);
                })
                .ToList();
            eventLogs.LogMatchAbandoned(matchingId, endReason, finalPlayerStats);
            var summaryRequest = MatchSummaryPersistence.Capture(
                eventLogs,
                logger,
                matchingId,
                endReason,
                winnerPlayerId);
            if (summaryRequest != null)
            {
                runtime.AfterRelease.Add(() => MatchSummaryPersistence.Persist(
                    summaryRequest,
                    summaryFileStore,
                    logger));
            }
        }

        logger.LogInformation(
            "Removed matching without human sessions: MatchingId={MatchingId}, EndReason={EndReason}",
            matchingId, endReason);
    }
}
