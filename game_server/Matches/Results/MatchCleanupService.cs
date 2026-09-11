using game_server.matches.logging;
using Microsoft.Extensions.Logging;

namespace game_server.matches.results;

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
        if (matchRuntimes.GetOrNull(matchingId)?.GetSessions().Count > 0)
        {
            return;
        }

        CleanupIfNoHumanSessionsRemain(matchingId, MatchEndReason.LastSurvivorBotOnly, winnerPlayerId);
    }

    public void CleanupIfNoHumanSessionsRemain(long matchingId, MatchEndReason endReason = MatchEndReason.LastHumanLeft, long winnerPlayerId = 0)
    {
        if (matchRuntimes.GetOrNull(matchingId)?.GetSessions().Count > 0)
        {
            return;
        }

        var runtime = matchRuntimes.GetOrNull(matchingId);
        if (runtime == null)
        {
            return;
        }

        using var scope = runtime.Enter();
        if (runtime.IsEnded || runtime.GetSessions().Count > 0)
        {
            return;
        }

        runtime.TryMarkEnded();
        if (eventLogs.TryBeginFinalization(matchingId))
        {
            var endedAtUtc = DateTime.UtcNow;
            var startedAtUtc = runtime.Closures.GameStartTime ?? endedAtUtc;
            var finalPlayerStats = new List<MatchFinalPlayerStats>();
            foreach (var row in runtime.BuildGameResult())
            {
                var stats = eventLogs.GetResultStats(matchingId, row.playerId);
                var survivalEndUtc = row.eliminatedAt ?? endedAtUtc;
                int survivalSeconds = Math.Max(0, (int)Math.Floor((survivalEndUtc - startedAtUtc).TotalSeconds));
                int orbCount = runtime.GetOrbs(row.playerId).GetOrbScore().OrbCount;
                var playerStats = new MatchFinalPlayerStats(
                    row.playerId,
                    row.eliminationRank,
                    survivalSeconds,
                    stats.KillCount + stats.MonsterKillCount,
                    stats.TotalDamageDealt + stats.MonsterDamageDealt,
                    stats.TotalRecovery,
                    orbCount);
                finalPlayerStats.Add(playerStats);
            }

            eventLogs.LogMatchAbandoned(matchingId, endReason.ToString(), finalPlayerStats);
            var summaryRequest = MatchSummaryFileStore.Prepare(eventLogs, logger, matchingId, endReason.ToString(), winnerPlayerId, out var capturedEvents);
            if (summaryRequest != null)
            {
                runtime.AfterRelease.Add(() => summaryFileStore.Save(summaryRequest, capturedEvents, logger));
            }
        }

        logger.LogInformation("Removed matching without human sessions: MatchingId={MatchingId}, EndReason={EndReason}", matchingId, endReason);
    }
}
