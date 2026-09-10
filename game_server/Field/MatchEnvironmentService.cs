using game_server.players;
using game_server.matches;
using game_server;
using game_server.bots;
using game_server.logging;
using game_server.matches.results;
using game_server.sessions;
using Microsoft.Extensions.Logging;
using network.common;

namespace game_server.field;

/// <summary>
///     매치의 환경 피해와 동시 탈락 순위를 정산하고 승자를 확정한다.
///     MatchTickLoop가 매치 잠금 안에서 5초 간격으로 호출한다.
///     매치 상태는 전달받은 MatchRuntime을 사용하며 별도 상태나 타이머를 소유하지 않는다.
/// </summary>
internal class MatchEnvironmentService(
    GameEventLogManager eventLogs,
    MatchCleanupService matchCleanup,
    PlayerEliminationService matchEliminations,
    MatchResultService matchResults,
    ILogger<MatchEnvironmentService> logger)
{
    /// <summary>매치의 5초 환경 정산. 50ms 틱이 전투 처리 후 같은 매치 잠금 안에서 호출한다.</summary>
    public virtual void ProcessTick(
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
                !session.Player.IsEliminated &&
                !session.IsGameEnded)
            .ToList();
        var bots = match.Bots.GetBots(matchingId)
            .Where(bot => !bot.Player.IsEliminated)
            .ToList();

        int aliveCount = humans.Count + bots.Count;
        if (aliveCount <= 1)
        {

            if (aliveCount == 1 && humans.Count > 0)
            {
                matchResults.FinalizeMatch(matchingId, humans[0].PlayerId ?? 0, MatchEndReason.LastSurvivor);
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

        var targets = new List<EnvironmentalTarget>(aliveCount);

        foreach (var session in humans)
        {
            int fieldDamage = MatchPressureFieldPolicy.GetDamagePerTick(match, session.Player.LastValidatedPosition, DateTime.UtcNow);
            targets.Add(new EnvironmentalTarget(
                session.PlayerId!.Value,
                session.Player.Health,
                fieldDamage,
                session,
                null));
        }

        foreach (var bot in bots)
        {
            int fieldDamage = MatchPressureFieldPolicy.GetDamagePerTick(match, bot.Position, DateTime.UtcNow);
            targets.Add(new EnvironmentalTarget(
                bot.PlayerId,
                bot.Player.Health,
                fieldDamage,
                null,
                bot));
        }

        foreach (var target in targets)
        {
            int totalDelta = target.FieldDamage;
            if (totalDelta == 0)
                continue;

            var player = target.Session?.Player ?? target.Bot!.Player;
            var change = player.ApplyDamage(totalDelta);
            PlayerHealthChangeService.Record(matchingId, player, change, eventLogs, logger);
        }
        var eliminatedTargets = targets
            .Where(target => target.FieldDamage > 0 && target.PreDamageHealth - target.FieldDamage <= 0)
            .ToList();
        if (eliminatedTargets.Count == 0)
            return;

        var resolution = ResolveEliminationOrder(
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

            matchEliminations.EliminatePlayer(
                matchingId, target.PlayerId, EliminationReason.PRESSURE_FIELD,
                deferGameOver: true,
                forcedRank: rank);

            rank--;
        }

        (bool isGameOver, long? winnerId) = match.CheckGameOver();
        bool hasActiveSession = match.GetSessions().Any(session => !session.IsGameEnded);
        if (isGameOver && winnerId.HasValue && hasActiveSession)
        {
            matchResults.FinalizeMatch(matchingId, winnerId.Value, MatchEndReason.PressureFieldSettlement, resolution.DecisiveCriterion);
            match.AutoAttack.Clear();
        }
    }

    private sealed record EnvironmentalTarget(
        long PlayerId,
        int PreDamageHealth,
        int FieldDamage,
        GameClientSession? Session,
        BotPlayerState? Bot);

    internal readonly record struct MatchSettlementCandidate(
        long PlayerId,
        int PreDamageHealth,
        int TotalPvpDamage);

    internal sealed class MatchSettlementResolution
    {
        public required IReadOnlyList<MatchSettlementCandidate> BestToWorst { get; init; }
        public required MatchTieBreakCriterion DecisiveCriterion { get; init; }
    }

    internal static MatchSettlementResolution ResolveEliminationOrder(
        long matchingId,
        IEnumerable<MatchSettlementCandidate> candidates)
    {
        var ordered = candidates
            .DistinctBy(candidate => candidate.PlayerId)
            .OrderByDescending(candidate => candidate.PreDamageHealth)
            .ThenByDescending(candidate => candidate.TotalPvpDamage)
            .ThenBy(candidate => GetMatchSeedPriority(matchingId, candidate.PlayerId))
            .ThenBy(candidate => candidate.PlayerId)
            .ToList();

        MatchTieBreakCriterion criterion = MatchTieBreakCriterion.SingleCandidate;
        if (ordered.Count > 1)
        {
            var first = ordered[0];
            var second = ordered[1];
            criterion = first.PreDamageHealth != second.PreDamageHealth
                ? MatchTieBreakCriterion.PreDamageHealth
                : first.TotalPvpDamage != second.TotalPvpDamage
                    ? MatchTieBreakCriterion.CumulativePvpDamage
                    : MatchTieBreakCriterion.MatchSeedPriority;
        }

        return new MatchSettlementResolution
        {
            BestToWorst = ordered,
            DecisiveCriterion = criterion
        };
    }

    internal static ulong GetMatchSeedPriority(long matchingId, long playerId)
    {
        ulong value = unchecked((ulong)matchingId) ^
                      (unchecked((ulong)playerId) + 0x9E3779B97F4A7C15UL);
        value = (value ^ (value >> 30)) * 0xBF58476D1CE4E5B9UL;
        value = (value ^ (value >> 27)) * 0x94D049BB133111EBUL;
        return value ^ (value >> 31);
    }
}
