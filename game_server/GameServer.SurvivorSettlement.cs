using game_server.network;
using game_server.services;
using Microsoft.Extensions.Logging;
using network.common;
using network.common.data;
using network.helpers;

namespace game_server;

public partial class GameServer
{
    private object GetSurvivorSettlementLock(long matchingId) =>
        _survivorSettlementLocks.GetOrAdd(matchingId, _ => new object());

    private void ProcessSurvivorResourceTickForMatching(
        long matchingId,
        List<GameClientSession> activeSessions)
    {
        lock (GetSurvivorSettlementLock(matchingId))
        {
            // Combat always settles before environmental damage in the same server resource tick.
            if (Config.PROXIMITY_AUTO_COMBAT_P0_ENABLED)
                ProcessProximityAutoCombatForMatching(matchingId, activeSessions);

            var humans = activeSessions
                .Where(session =>
                    session.CurrentMapSubId == matchingId &&
                    !session.IsEliminated &&
                    !session.IsGameEnded)
                .ToList();
            var bots = _botPlayerManager.GetBots(matchingId)
                .Where(bot => !bot.IsEliminated)
                .ToList();

            int aliveCount = humans.Count + bots.Count;
            if (aliveCount <= 1)
            {
                // 맵 이동 검증에서는 단독 생존을 승리 상태로 정산하지 않는다.
                if (DevFlags.DisableGameEnd)
                    return;

                if (aliveCount == 1 && humans.Count > 0)
                {
                    humans[0].TryEndSurvivorMatch(humans[0].PlayerId ?? 0, "last_survivor_before_overtime");
                    CleanupSurvivorSettlementState(matchingId);
                }
                return;
            }

            // Overtime is valid only while at least two survivors remain.
            int overtimeDelta = _areaClosureManager.GetOvertimeCorruptionPerTick(
                matchingId,
                ResourceTickIntervalSeconds);
            var targets = new List<EnvironmentalTarget>(aliveCount);

            foreach (var session in humans)
            {
                int closureDelta = _areaClosureManager.GetClosedAreaCorruptionPerTick(
                    matchingId,
                    session.CurrentArea,
                    ResourceTickIntervalSeconds);
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
                int closureDelta = _areaClosureManager.GetClosedAreaCorruptionPerTick(
                    matchingId,
                    bot.CurrentArea,
                    ResourceTickIntervalSeconds);
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
                    _botPlayerManager.ApplyEnvironmentalCorruption(target.Bot, totalDelta);
                }
            }

            var eliminatedTargets = targets
                .Where(target => target.PreDamageCorruption + target.ClosureDelta + target.OvertimeDelta >= Config.SURVIVOR_MAX_CORRUPTION)
                .ToList();
            if (eliminatedTargets.Count == 0)
                return;

            var resolution = SurvivorSettlementResolver.Resolve(
                matchingId,
                eliminatedTargets.Select(target =>
                {
                    int damage = _gameEventLogManager
                        .GetSurvivorResultStats(matchingId, target.PlayerId)
                        .TotalDamageDealt;
                    return new SurvivorSettlementCandidate(
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
                    target.PreDamageCorruption + target.ClosureDelta >= Config.SURVIVOR_MAX_CORRUPTION;
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
                    _botPlayerManager.MarkEnvironmentalEliminated(target.Bot, matchingId);
                    _gameEventLogManager.LogElimination(
                        matchingId,
                        target.PlayerId,
                        EliminationReason.MENTAL_ZERO.ToString(),
                        isBot: true);
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

            var (isGameOver, winnerId) = _manittoChainManager.CheckGameOver(matchingId);
            var resultHost = _clientSessions.Values.FirstOrDefault(session =>
                session.PlayerId.HasValue &&
                session.CurrentMapSubId == matchingId &&
                !session.IsGameEnded);
            if (isGameOver && winnerId.HasValue && resultHost != null)
            {
                resultHost.TryEndSurvivorMatch(winnerId.Value, resolution.DecisiveCriterion);
                CleanupSurvivorSettlementState(matchingId);
            }
        }
    }

    private void CleanupSurvivorSettlementState(long matchingId)
    {
        _proximityAutoCombatResolver.RemoveMatching(matchingId);
        _survivorSettlementLocks.TryRemove(matchingId, out _);
    }

    private int ResolveFinalOrbTier(long matchingId, long playerId)
    {
        var equippedItem = _inGameInventoryManager.GetEquippedBattleItem(matchingId, playerId);
        return equippedItem == null ? 0 : BattleItemCombatData.Get(equippedItem.ItemId)?.Tier ?? 0;
    }

    private sealed record EnvironmentalTarget(
        long PlayerId,
        int PreDamageCorruption,
        int ClosureDelta,
        int OvertimeDelta,
        GameClientSession? Session,
        BotPlayerState? Bot);
}
