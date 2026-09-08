using game_server.network;
using game_server.sessions;
using Microsoft.Extensions.Logging;

namespace game_server.services;

/// <summary>
///     전달받은 매치 하나의 잠금 안에서 카운트다운·전투·환경 정산·구역 폐쇄·봇 이동을 순서대로 실행한다.
///     바쁜 매치는 해당 틱을 건너뛰며 다른 매치를 순회하거나 기다리지 않는다.
/// </summary>
internal sealed class MatchTickRunner(
    MatchRuntimeStore matchRuntimes,
    ILogger logger,
    GroundItemAutoPickupService groundItemAutoPickup,
    Action<IEnumerable<long>, IReadOnlyCollection<GameClientSession>> publishCountdown,
    Action<long, List<GameClientSession>> processCombat,
    Action<MatchRuntime, List<GameClientSession>> processEnvironment,
    Action<MatchRuntime> moveBots,
    Action<long, GameClientSession[]> processAreaClosure)
{
    public void Run(MatchRuntime runtime)
    {
        long matchingId = runtime.MatchingId;
        if (!ReferenceEquals(matchRuntimes.Get(matchingId), runtime) || runtime.IsTerminal)
            return;
        if (!matchRuntimes.TryEnter(matchingId, out MatchScope scope))
        {
            RecordBotTickBusySkip(matchingId);
            return;
        }

        using (scope)
        {
            if (!ReferenceEquals(scope.Runtime, runtime) || scope.Runtime.IsTerminal)
                return;

            List<GameClientSession> countdownSessions;
            List<GameClientSession> activeSessions;
            try
            {
                countdownSessions = runtime.Sessions.Snapshot()
                    .Where(static session => session.PlayerId.HasValue).ToList();
                activeSessions = countdownSessions
                    .Where(static session => !session.IsEliminated && !session.IsGameEnded).ToList();
            }
            catch (Exception ex)
            {
                logger.LogError(ex, "Match session snapshot failed: MatchingId={MatchingId}", matchingId);
                return;
            }

            try
            {
                publishCountdown([matchingId], countdownSessions);
                if (scope.Runtime.IsTerminal)
                    return;
                if (MatchStartGate.IsGameplayActive(matchingId))
                    groundItemAutoPickup.Process(runtime, activeSessions);
                if (runtime.IsTerminal) return;
                processCombat(matchingId, activeSessions);
                if (scope.Runtime.IsTerminal)
                    return;
                if (scope.Runtime.TryBeginEnvironmentalTick(
                        DateTime.UtcNow, MatchStartGate.GetGameplayStartedAtUtc(matchingId)))
                {
                    processEnvironment(scope.Runtime, activeSessions);
                }
            }
            catch (Exception ex)
            {
                logger.LogError(ex, "Match combat tick failed: MatchingId={MatchingId}", matchingId);
            }

            if (scope.Runtime.IsTerminal)
                return;

            try
            {
                if (scope.Runtime.TryBeginAreaClosureTick(
                        DateTime.UtcNow, MatchStartGate.GetGameplayStartedAtUtc(matchingId)))
                {
                    processAreaClosure(matchingId, countdownSessions.ToArray());
                }
            }
            catch (Exception ex)
            {
                logger.LogError(ex, "Match area closure tick failed: MatchingId={MatchingId}", matchingId);
            }

            if (scope.Runtime.IsTerminal || !ShouldMoveBots(scope.Runtime))
                return;

            try
            {
                moveBots(scope.Runtime);
            }
            catch (Exception ex)
            {
                logger.LogError(ex, "Bot walking tick failed: MatchingId={MatchingId}", matchingId);
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
