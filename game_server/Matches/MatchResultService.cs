using MessagePack;
using Microsoft.Extensions.Logging;
using network.common;
using network.common.data.models;
using network.packets;

namespace game_server.matches;

/// <summary>
///     매치 종료를 한 번만 확정하고 참가자별 결과와 순위를 만든다.
///     매치 잠금 안에서 결과·종료 패킷을 전송하고 각 세션에 게임 종료 상태를 반영한다.
///     완료 알림은 매치 잠금이 풀린 뒤 실행한다.
///     탈락자에게 보여 줄 중간 결과표도 생성한다.
/// </summary>
internal sealed class MatchResultService(ILogger logger)
{
    public void FinalizeMatch(MatchRuntime runtime, long winnerId, MatchEndReason endReason = MatchEndReason.LastSurvivor, MatchTieBreakCriterion tieBreakCriterion = MatchTieBreakCriterion.None)
    {
        if (!Monitor.IsEntered(runtime.MatchLock))
        {
            throw new InvalidOperationException("Match finalization requires the match lock.");
        }

        long matchingId = runtime.MatchingId;
        if (!runtime.TryMarkEnded())
        {
            logger.LogDebug("Duplicate match finalization ignored: MatchingId={MatchingId}", matchingId);
            return;
        }

        logger.LogInformation("Match ended: MatchingId={MatchingId}, Reason={Reason}, WinnerId={WinnerId}, TieBreak={TieBreak}", matchingId, endReason, winnerId, tieBreakCriterion);

        MatchSynchronizationService.SendPendingCombatHits(runtime);
        MatchSynchronizationService.SendPendingDeathNotifications(runtime);
        MatchSynchronizationService.SendPendingNotifications(runtime);

        var sessionSnapshot = runtime.GetSessions();
        var players = BuildPlayerResults(runtime, winnerId);
        byte[] resultPayload = MessagePackSerializer.Serialize(new G_TO_C_GAME_RESULT
        {
            WinnerId = winnerId,
            IsTimeout = endReason == MatchEndReason.OrbScoreTimeout,
            Players = players
        });

        foreach (var session in sessionSnapshot)
        {
            try
            {
                using var resultPacket = Packet.Create((int)Protocol.G_TO_C_GAME_RESULT);
                resultPacket.SetBody(resultPayload);
                session.TrySend(resultPacket);
            }
            catch (Exception ex)
            {
                logger.LogWarning(ex, "Match terminal publication failed: MatchingId={MatchingId}, PlayerId={PlayerId}, Component={Component}", matchingId, session.PlayerId, "GAME_RESULT");
            }
        }

        foreach (var session in sessionSnapshot)
        {
            try
            {
                using var endPacket = Packet.Create((int)Protocol.G_TO_C_GAME_END);
                var endMessage = new G_TO_C_GAME_END
                {
                    MatchingId = matchingId,
                    IsEscaped = session.PlayerId == winnerId
                };
                endPacket.SetBody(MessagePackSerializer.Serialize(endMessage));
                session.TrySend(endPacket);
            }
            catch (Exception ex)
            {
                logger.LogWarning(ex, "Match terminal publication failed: MatchingId={MatchingId}, PlayerId={PlayerId}, Component={Component}", matchingId, session.PlayerId, "GAME_END");
            }
        }

        foreach (var session in sessionSnapshot)
        {
            try
            {
                var lifecyclePublication = session.MarkGameEndedAndPrepareLifecyclePublication();
                if (lifecyclePublication != null)
                {
                    runtime.AfterRelease.Add(lifecyclePublication);
                }
            }
            catch (Exception ex)
            {
                logger.LogWarning(ex, "Match terminal publication failed: MatchingId={MatchingId}, PlayerId={PlayerId}, Component={Component}", matchingId, session.PlayerId, "MarkGameEnded");
            }
        }
    }

    public bool TryEndOnScoreTimeout(MatchRuntime runtime, DateTime nowUtc)
    {
        if (!Monitor.IsEntered(runtime.MatchLock))
        {
            throw new InvalidOperationException("Score timeout requires the match lock.");
        }
        if (runtime.IsEnded || runtime.StartsAtUtc is not { } startedAtUtc)
        {
            return false;
        }
        if ((nowUtc - startedAtUtc).TotalSeconds < Config.SWARM_MATCH_DURATION_SECONDS)
        {
            return false;
        }

        var candidates = new List<(long PlayerId, int OrbCount, int TierSum, int Health)>();
        foreach (var player in runtime.GetAlivePlayers())
        {
            var (orbCount, tierSum) = runtime.GetOrbs(player.PlayerId).GetOrbScore();
            candidates.Add((player.PlayerId, orbCount, tierSum, player.Health));
        }
        candidates.Sort(CompareOrbScore);

        long winnerId = candidates.Count > 0 ? candidates[0].PlayerId : 0;
        var scoreLog = new List<string>();
        foreach (var candidate in candidates)
        {
            scoreLog.Add($"{candidate.PlayerId}:{candidate.OrbCount}:{candidate.TierSum}");
        }
        logger.LogInformation("Match score result: MatchingId={MatchingId}, Scores={Scores}", runtime.MatchingId, string.Join(",", scoreLog));
        FinalizeMatch(runtime, winnerId, MatchEndReason.OrbScoreTimeout);
        return true;
    }

    public List<GameResultPlayerInfo> BuildPlayerResults(MatchRuntime runtime, long winnerId)
    {
        if (!Monitor.IsEntered(runtime.MatchLock))
        {
            throw new InvalidOperationException("Building player results requires the match lock.");
        }
        var resultRows = runtime.BuildGameResult();

        var playerResults = new List<GameResultPlayerInfo>();
        foreach (var row in resultRows)
        {
            var player = runtime.GetPlayer(row.playerId)!;
            var orbs = runtime.GetOrbs(row.playerId);
            bool isWinner = row.playerId == winnerId;
            playerResults.Add(new GameResultPlayerInfo
            {
                PlayerId = row.playerId,
                Name = string.IsNullOrEmpty(player.GameInfo.Name) ? $"Player{Math.Abs(row.playerId)}" : player.GameInfo.Name,
                EliminationReason = row.reason,
                FinalStatus = row.finalStatus,
                Health = player.Health,
                MaxHealth = Config.MAX_HEALTH,
                WearItemIdList = player.GameInfo.WearItemIdList is { Count: > 0 } wearItemIds ? new List<int>(wearItemIds) : new List<int>(),
                AttackerPlayerId = row.attackerPlayerId,
                EliminatedArea = row.eliminatedArea,
                Rank = isWinner ? 1 : row.eliminationRank,
                OrbCount = orbs.GetOrbScore().OrbCount
            });
        }

        return AssignRankings(playerResults, winnerId);
    }

    internal static int CompareOrbScore((long PlayerId, int OrbCount, int TierSum, int Health) left, (long PlayerId, int OrbCount, int TierSum, int Health) right)
    {
        int byOrbs = right.OrbCount.CompareTo(left.OrbCount);
        if (byOrbs != 0)
        {
            return byOrbs;
        }
        int byTierSum = right.TierSum.CompareTo(left.TierSum);
        if (byTierSum != 0)
        {
            return byTierSum;
        }
        int byHealth = right.Health.CompareTo(left.Health);
        return byHealth != 0 ? byHealth : left.PlayerId.CompareTo(right.PlayerId);
    }

    internal static List<GameResultPlayerInfo> AssignRankings(IEnumerable<GameResultPlayerInfo> players, long winnerId = 0)
    {
        var ordered = new List<GameResultPlayerInfo>();
        foreach (var player in players)
        {
            if (player.PlayerId != 0)
            {
                ordered.Add(player);
            }
        }

        ordered.Sort((left, right) =>
        {
            bool leftWon = winnerId != 0 && left.PlayerId == winnerId;
            bool rightWon = winnerId != 0 && right.PlayerId == winnerId;
            if (leftWon != rightWon)
            {
                return leftWon ? -1 : 1;
            }
            int byRank = Math.Max(left.Rank, 0).CompareTo(Math.Max(right.Rank, 0));
            if (byRank != 0)
            {
                return byRank;
            }
            int byOrbs = right.OrbCount.CompareTo(left.OrbCount);
            return byOrbs != 0 ? byOrbs : left.PlayerId.CompareTo(right.PlayerId);
        });

        for (int i = 0; i < ordered.Count; i++)
        {
            ordered[i].Rank = i + 1;
        }

        return ordered;
    }
}
