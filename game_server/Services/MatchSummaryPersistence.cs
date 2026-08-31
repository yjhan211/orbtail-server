using Microsoft.Extensions.Logging;

namespace game_server.services;

/// <summary>
///     매치 요약 영속 공용 경로 (#297 중복 단일화) — 세션 정산과 봇 전용 매치 정산이 같은 저장·로그를 쓴다.
/// </summary>
public static class MatchSummaryPersistence
{
    public static void Persist(
        GameEventLogManager gameEventLogManager,
        MatchSummaryFileStore matchSummaryFileStore,
        ILogger logger,
        long matchingId,
        string endReason,
        long winnerId)
    {
        try
        {
            var events = gameEventLogManager.GetForPersistence(matchingId);
            var summary = matchSummaryFileStore.Save(matchingId, endReason, winnerId, events);
            logger.LogInformation(
                "Match summary persisted: MatchingId={MatchingId}, EndReason={EndReason}, Events={EventCount}, Directory={Directory}",
                matchingId, summary.EndReason, summary.RawEventCount, matchSummaryFileStore.DirectoryPath);
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "Failed to persist match summary: MatchingId={MatchingId}", matchingId);
        }
    }
}
