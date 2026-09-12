using Microsoft.Extensions.Logging;

namespace game_server.matches;

/// <summary>
///     사람 세션이 남지 않은 매치를 종료한다.
///     매치 잠금 안에서 사람 세션 유무를 다시 확인하고 종료를 한 번만 확정한다. 세션 등록·제거 자체는 담당하지 않는다.
/// </summary>
internal sealed class MatchCleanupService(
    MatchRuntimeStore matchRuntimes,
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

        logger.LogInformation("Removed matching without human sessions: MatchingId={MatchingId}, EndReason={EndReason}", matchingId, endReason);
    }
}
