using game_server.network;
using game_server.services;
using game_server.sessions;
using Microsoft.Extensions.Logging;
using network.common;

namespace game_server.matches;

/// <summary>
///     매치의 환경 피해와 동시 탈락 순위를 정산하고 승자를 확정한다.
///     MatchTickRunner가 매치 잠금 안에서 5초 간격으로 호출한다.
///     매치 상태는 전달받은 MatchRuntime을 사용하며 별도 상태나 타이머를 소유하지 않는다.
/// </summary>
internal sealed class MatchEnvironmentService(
    GameEventLogManager eventLogs,
    MatchCleanupService matchCleanup,
    BotEliminationService botEliminations,
    MatchEliminationService matchEliminations,
    GameServerDevOptions devOptions,
    ILogger<MatchEnvironmentService> logger)
{
    /// <summary>매치의 5초 환경 정산. 50ms 틱이 전투 처리 후 같은 매치 잠금 안에서 호출한다.</summary>
    public void Process(
        MatchRuntime match,
        List<GameClientSession> activeSessions)
    {
        if (!Monitor.IsEntered(match.MatchLock))
            throw new InvalidOperationException("Environmental settlement requires the match lock.");
        if (match.IsEnded)
            return;

        long matchingId = match.MatchingId;

        var humans = activeSessions
            .Where(session =>
                session.MatchingId == matchingId &&
                !session.IsEliminated &&
                !session.IsGameEnded)
            .ToList();
        var bots = match.Bots.GetBots(matchingId)
            .Where(bot => !bot.IsEliminated)
            .ToList();

        int aliveCount = humans.Count + bots.Count;
        if (aliveCount <= 1)
        {
            if (devOptions.DisableGameEnd || devOptions.CutDummy || devOptions.CrossfireSandbox)
                return;

            if (aliveCount == 1 && humans.Count > 0)
            {
                matchEliminations.EndMatch(matchingId, humans[0].PlayerId ?? 0, "last_survivor_before_overtime");
                match.AutoAttack.Clear();
                return;
            }

            if (humans.Count == 0)
            {
                long winnerPlayerId = bots.Count == 1 ? bots[0].PlayerId : 0;
                match.AutoAttack.Clear();
                matchCleanup.EndBotOnlyMatchIfSettled(matchingId, winnerPlayerId);
            }

            return;
        }

        int overtimeDelta = match.Closures.GetOvertimeDamagePerTick(
            MatchTickSchedule.EnvironmentalTickIntervalSeconds);
        var targets = new List<EnvironmentalTarget>(aliveCount);

        foreach (var session in humans)
        {
            int closureDelta = match.Closures.GetClosedAreaDamagePerTick(
                session.CurrentArea,
                MatchTickSchedule.EnvironmentalTickIntervalSeconds);
            if (session.LastValidatedPosition != null)
                closureDelta += MatchPressureFieldPolicy.GetDamagePerTick(match, session.LastValidatedPosition, DateTime.UtcNow);
            targets.Add(new EnvironmentalTarget(
                session.PlayerId!.Value,
                session.CurrentHealth,
                closureDelta,
                overtimeDelta,
                session,
                null));
        }

        foreach (var bot in bots)
        {
            int closureDelta = match.Closures.GetClosedAreaDamagePerTick(
                bot.CurrentArea,
                MatchTickSchedule.EnvironmentalTickIntervalSeconds);
            closureDelta += MatchPressureFieldPolicy.GetDamagePerTick(match, bot.Position, DateTime.UtcNow);
            targets.Add(new EnvironmentalTarget(
                bot.PlayerId,
                bot.Health,
                closureDelta,
                overtimeDelta,
                null,
                bot));
        }

        foreach (var target in targets)
        {
            int totalDelta = target.ClosureDelta + target.OvertimeDelta;
            if (totalDelta == 0)
                continue;

            if (target.Session != null)
            {
                target.Session.HealthChanges.Handle(
                    target.Session.Condition.ApplyDamage(totalDelta),
                    deferElimination: true);
            }
            else if (target.Bot != null)
            {
                match.Bots.ApplyEnvironmentalDamage(target.Bot, totalDelta);
            }
        }

        var eliminatedTargets = targets
            .Where(target => target.PreDamageHealth - target.ClosureDelta - target.OvertimeDelta <= 0)
            .ToList();
        if (eliminatedTargets.Count == 0)
            return;

        var resolution = MatchSettlementResolver.Resolve(
            matchingId,
            eliminatedTargets.Select(target =>
            {
                int damage = eventLogs
                    .GetResultStats(matchingId, target.PlayerId)
                    .TotalDamageDealt;
                return new MatchSettlementCandidate(
                    target.PlayerId,
                    target.PreDamageHealth,
                    damage);
            }));

        var survivorsToEliminate = resolution.BestToWorst.ToList();
        if (eliminatedTargets.Count == aliveCount)
            survivorsToEliminate.RemoveAt(0);

        if (resolution.BestToWorst.Count > 1)
        {
            string orderedPlayers = string.Join(
                ",",
                resolution.BestToWorst.Select(candidate => candidate.PlayerId));
            logger.LogInformation(
                "Simultaneous environmental settlement: MatchingId={MatchingId}, Criterion={Criterion}, BestToWorst={BestToWorst}",
                matchingId,
                resolution.DecisiveCriterion,
                orderedPlayers);
            eventLogs.LogSystem(
                matchingId,
                $"environment_tiebreak criterion={resolution.DecisiveCriterion} best_to_worst={orderedPlayers}");
        }

        int rank = aliveCount;
        foreach (var candidate in survivorsToEliminate.AsEnumerable().Reverse())
        {
            var target = eliminatedTargets.First(entry => entry.PlayerId == candidate.PlayerId);
            bool closureElimination =
                target.ClosureDelta > 0 &&
                target.PreDamageHealth - target.ClosureDelta <= 0;
            bool overtimeElimination = !closureElimination && target.OvertimeDelta > 0;

            if (target.Session != null)
            {
                matchEliminations.Process(
                    matchingId, target.PlayerId, EliminationReason.HEALTH_ZERO,
                    deferGameOver: true,
                    isAreaClosureElimination: closureElimination,
                    isOvertimeElimination: overtimeElimination,
                    forcedRank: rank);
            }
            else if (target.Bot != null)
            {
                botEliminations.Process(
                    match,
                    target.PlayerId,
                    EliminationReason.HEALTH_ZERO,
                    isAreaClosureElimination: closureElimination,
                    isOvertimeElimination: overtimeElimination,
                    deferGameOver: true,
                    forcedRank: rank);
            }

            rank--;
        }

        (bool isGameOver, long? winnerId) = match.Roster.CheckGameOver();
        bool hasActiveSession = match.Sessions.Snapshot().Any(session => !session.IsGameEnded);
        if (isGameOver && winnerId.HasValue && hasActiveSession)
        {
            matchEliminations.EndMatch(matchingId, winnerId.Value, resolution.DecisiveCriterion);
            match.AutoAttack.Clear();
        }
    }

    private sealed record EnvironmentalTarget(
        long PlayerId,
        int PreDamageHealth,
        int ClosureDelta,
        int OvertimeDelta,
        GameClientSession? Session,
        BotPlayerState? Bot);
}
