using game_server.network;
using game_server.services;
using Microsoft.Extensions.Logging;
using network.common;
using network.common.data;
using network.helpers;

namespace game_server;

public partial class GameServer
{
    private void ProcessResourceTickForMatching(
        long matchingId,
        List<GameClientSession> activeSessions)
    {
        _matchRuntimeRegistry.TryExecute(matchingId, () =>
        {
            // Combat always settles before environmental damage in the same server resource tick.
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
                // 실험장(절단·교차사격 샌드박스)도 같다 (2026-08-24): 교차사격 샌드박스가 더미 없이
                // 봇 전원을 퇴장시키므로, 이 게이트가 없으면 매치가 첫 틱에 단독 생존 승리로 끝난다.
                if (DevFlags.DisableGameEnd || SwarmDummySandboxActive)
                    return;

                if (aliveCount == 1 && humans.Count > 0)
                {
                    humans[0].TryEndMatch(humans[0].PlayerId ?? 0, "last_survivor_before_overtime");
                    CleanupMatchSettlementState(matchingId);
                    return;
                }

                // 사람 세션이 없는 매치(관리자 봇 전용 인스턴스)는 정산 주체가 없어
                // 최후 1인이 남아도 끝나지 않았다. 오염도가 한계에 닿은 봇이 계속 살아 있는
                // 채로 매치가 무한히 이어진다.
                if (humans.Count == 0)
                {
                    long winnerPlayerId = bots.Count == 1 ? bots[0].PlayerId : 0;
                    CleanupMatchSettlementState(matchingId);
                    EndBotOnlyMatchIfSettled(matchingId, winnerPlayerId);
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
                // 스웜 자기장: 구역이 아니라 참가자 셀의 중심 거리 기준 연속 압박.
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
                int closureDelta = _areaClosureManager.GetClosedAreaCorruptionPerTick(
                    matchingId,
                    bot.CurrentArea,
                    ResourceTickIntervalSeconds);
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
                    _botPlayerManager.ApplyEnvironmentalCorruption(target.Bot, totalDelta);
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

            var (isGameOver, winnerId) = _matchRosterManager.CheckGameOver(matchingId);
            var resultHost = GetSessionsByMatch(matchingId)
                .FirstOrDefault(session => !session.IsGameEnded);
            if (isGameOver && winnerId.HasValue && resultHost != null)
            {
                resultHost.TryEndMatch(winnerId.Value, resolution.DecisiveCriterion);
                CleanupMatchSettlementState(matchingId);
            }
        });
    }

    private void CleanupMatchSettlementState(long matchingId)
    {
        _proximityAutoCombatResolver.RemoveMatching(matchingId);
        RemoveOrbVisualStates(matchingId);
    }

    private sealed record EnvironmentalTarget(
        long PlayerId,
        int PreDamageCorruption,
        int ClosureDelta,
        int OvertimeDelta,
        GameClientSession? Session,
        BotPlayerState? Bot);
}
