using game_server.network;
using Microsoft.Extensions.Logging;

namespace game_server.services;

/// <summary>
///     매치마다 잠금을 잡고 카운트다운, 전투, 환경 정산, 봇 이동을 순서대로 실행한다.
///     잠금이 바쁜 매치는 이번 틱을 건너뛰며, 종료된 매치는 처리하지 않는다.
///     GameServer가 타이머 수명을 관리하고 이 클래스에는 각 단계의 처리 함수를 전달한다.
/// </summary>
internal sealed class MatchTickRunner(
    MatchRuntimeStore matchRuntimes,
    GameSessionRegistry sessions,
    ILogger logger,
    Action<IEnumerable<long>, IReadOnlyCollection<GameClientSession>> publishCountdown,
    Action<long, List<GameClientSession>> processCombat,
    Action<MatchRuntime, List<GameClientSession>> processEnvironment,
    Action<long> moveBots)
{
    public void Run()
    {
        List<GameClientSession> activeSessions;
        List<GameClientSession> countdownSessions;
        IReadOnlyList<long> activeMatchingIds;
        try
        {
            countdownSessions = sessions.SnapshotWhere(static session => session.PlayerId.HasValue);
            activeSessions = countdownSessions
                .Where(static session => !session.IsEliminated && !session.IsGameEnded)
                .ToList();
            activeMatchingIds = matchRuntimes.ActiveIds();
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "Proximity auto combat snapshot failed");
            return;
        }

        foreach (long matchingId in activeMatchingIds)
        {
            // 카운트다운 중에도 몬스터 연출을 진행한다. 실제 전투 허용 여부는 전투 처리기가 판단한다.
            if (!matchRuntimes.TryEnter(matchingId, out MatchScope scope))
            {
                RecordBotTickBusySkip(matchingId);
                continue;
            }

            using (scope)
            {
                if (scope.Runtime.IsTerminal)
                    continue;

                try
                {
                    publishCountdown([matchingId], countdownSessions);
                    processCombat(matchingId, activeSessions);
                    if (scope.Runtime.TryBeginEnvironmentalTick(
                            DateTime.UtcNow, MatchStartGate.GetGameplayStartedAtUtc(matchingId)))
                    {
                        processEnvironment(scope.Runtime, activeSessions);
                    }
                }
                catch (Exception ex)
                {
                    logger.LogError(
                        ex,
                        "Proximity auto combat tick failed: MatchingId={MatchingId}",
                        matchingId);
                }

                if (scope.Runtime.IsTerminal || !ShouldMoveBots(scope.Runtime))
                    continue;

                try
                {
                    moveBots(matchingId);
                }
                catch (Exception ex)
                {
                    logger.LogError(ex, "Bot walking tick failed: MatchingId={MatchingId}", matchingId);
                }
            }
        }
    }

    private static bool ShouldMoveBots(MatchRuntime runtime) =>
        MatchStartGate.IsGameplayActive(runtime.MatchingId) && runtime.Bots.HasBots(runtime.MatchingId);

    private void RecordBotTickBusySkip(long matchingId)
    {
        if (matchRuntimes.Get(matchingId) is { IsTerminal: false } runtime && ShouldMoveBots(runtime))
            runtime.Swarm.BotTickMetrics.RecordBusySkip();
    }
}
