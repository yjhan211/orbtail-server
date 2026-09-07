using game_server.network;
using game_server.services;
using Microsoft.Extensions.Logging;
using network.common;

namespace game_server;

public partial class GameServer
{
    /// <summary>매치의 5초 환경 정산. 50ms 틱이 전투 처리 후 같은 매치 잠금 안에서 호출한다.</summary>
    private void ProcessEnvironmentalTickForMatching(
        MatchRuntime match,
        List<GameClientSession> activeSessions)
    {
        long matchingId = match.MatchingId;

        var humans = activeSessions
            .Where(session =>
                session.MatchingId == matchingId &&
                !session.IsEliminated &&
                !session.IsGameEnded)
            .ToList();
        var bots = MatchRuntimes.GetRequired(matchingId).Bots.GetBots(matchingId)
            .Where(bot => !bot.IsEliminated)
            .ToList();

        int aliveCount = humans.Count + bots.Count;
        if (aliveCount <= 1)
        {
            if (devOptions.DisableGameEnd || SwarmDummySandboxActive)
                return;

            if (aliveCount == 1 && humans.Count > 0)
            {
                humans[0].TryEndMatch(humans[0].PlayerId ?? 0, "last_survivor_before_overtime");
                CleanupMatchSettlementState(matchingId);
                return;
            }

            if (humans.Count == 0)
            {
                long winnerPlayerId = bots.Count == 1 ? bots[0].PlayerId : 0;
                CleanupMatchSettlementState(matchingId);
                EndBotOnlyMatchIfSettled(matchingId, winnerPlayerId);
            }

            return;
        }

        int overtimeDelta = match.Closures.GetOvertimeCorruptionPerTick(
            MatchRuntime.EnvironmentalTickIntervalSeconds);
        var targets = new List<EnvironmentalTarget>(aliveCount);

        foreach (var session in humans)
        {
            int closureDelta = match.Closures.GetClosedAreaCorruptionPerTick(
                session.CurrentArea,
                MatchRuntime.EnvironmentalTickIntervalSeconds);
            if (session.LastValidatedPosition != null)
                closureDelta += GetSwarmFieldCorruptionPerTick(matchingId, session.LastValidatedPosition);
            targets.Add(new EnvironmentalTarget(
                session.PlayerId!.Value,
                session.CurrentCorruption,
                closureDelta,
                overtimeDelta,
                session,
                null));
        }

        foreach (var bot in bots)
        {
            int closureDelta = match.Closures.GetClosedAreaCorruptionPerTick(
                bot.CurrentArea,
                MatchRuntime.EnvironmentalTickIntervalSeconds);
            closureDelta += GetSwarmFieldCorruptionPerTick(matchingId, bot.Position);
            targets.Add(new EnvironmentalTarget(
                bot.PlayerId,
                bot.Corruption,
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
                target.Session.ModifyStats(
                    corruptionDelta: totalDelta,
                    deferElimination: true);
            }
            else if (target.Bot != null)
            {
                MatchRuntimes.GetRequired(matchingId).Bots.ApplyEnvironmentalCorruption(target.Bot, totalDelta);
            }
        }

        var eliminatedTargets = targets
            .Where(target => target.PreDamageCorruption + target.ClosureDelta + target.OvertimeDelta >= Config.MAX_CORRUPTION)
            .ToList();
        if (eliminatedTargets.Count == 0)
            return;

        var resolution = MatchSettlementResolver.Resolve(
            matchingId,
            eliminatedTargets.Select(target =>
            {
                int damage = _gameEventLogManager
                    .GetResultStats(matchingId, target.PlayerId)
                    .TotalDamageDealt;
                return new MatchSettlementCandidate(
                    target.PlayerId,
                    target.PreDamageCorruption,
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
            _gameEventLogManager.LogSystem(
                matchingId,
                $"environment_tiebreak criterion={resolution.DecisiveCriterion} best_to_worst={orderedPlayers}");
        }

        int rank = aliveCount;
        foreach (var candidate in survivorsToEliminate.AsEnumerable().Reverse())
        {
            var target = eliminatedTargets.First(entry => entry.PlayerId == candidate.PlayerId);
            bool closureElimination =
                target.ClosureDelta > 0 &&
                target.PreDamageCorruption + target.ClosureDelta >= Config.MAX_CORRUPTION;
            bool overtimeElimination = !closureElimination && target.OvertimeDelta > 0;

            if (target.Session != null)
            {
                target.Session.EliminateForSettlement(
                    target.PlayerId,
                    closureElimination,
                    overtimeElimination,
                    rank);
            }
            else if (target.Bot != null)
            {
                ProcessBotElimination(
                    matchingId,
                    target.PlayerId,
                    EliminationReason.MENTAL_ZERO,
                    activeSessions,
                    isAreaClosureElimination: closureElimination,
                    isOvertimeElimination: overtimeElimination,
                    deferGameOver: true,
                    forcedRank: rank);
            }

            rank--;
        }

        (bool isGameOver, long? winnerId) = match.Roster.CheckGameOver();
        var resultHost = GetSessionsByMatch(matchingId)
            .FirstOrDefault(session => !session.IsGameEnded);
        if (isGameOver && winnerId.HasValue && resultHost != null)
        {
            resultHost.TryEndMatch(winnerId.Value, resolution.DecisiveCriterion);
            CleanupMatchSettlementState(matchingId);
        }
    }

    private void CleanupMatchSettlementState(long matchingId)
    {
        _proximityAutoCombatResolver.RemoveMatching(matchingId);
    }

    private sealed record EnvironmentalTarget(
        long PlayerId,
        int PreDamageCorruption,
        int ClosureDelta,
        int OvertimeDelta,
        GameClientSession? Session,
        BotPlayerState? Bot);
}
