using game_server.matches.logging;
using game_server.sessions;
using MessagePack;
using Microsoft.Extensions.Logging;
using network.common;
using network.common.data.models;
using network.packets;

namespace game_server.matches;

/// <summary>
///     매치 종료를 한 번만 확정하고 참가자별 결과와 순위를 만든다.
///     매치 잠금 안에서 결과·종료 패킷을 전송하고 각 세션에 게임 종료 상태를 반영한다.
///     완료 알림과 요약 파일 저장은 매치 잠금이 풀린 뒤 실행한다.
///     탈락자에게 보여 줄 중간 결과표도 생성한다.
/// </summary>
internal sealed class MatchResultService(
    MatchRuntimeStore matchRuntimes,
    GameEventLogManager gameEventLogManager,
    MatchSummaryFileStore matchSummaryFileStore,
    ILogger logger)
{
    public void FinalizeMatch(long matchingId,
        long winnerId,
        MatchEndReason endReason = MatchEndReason.LastSurvivor,
        MatchTieBreakCriterion tieBreakCriterion = MatchTieBreakCriterion.None,
        bool isTimeout = false)
    {
        var runtime = matchRuntimes.GetOrNull(matchingId);
        if (runtime == null)
        {
            logger.LogDebug("Terminal match result preparation rejected: MatchingId={MatchingId}", matchingId);
            return;
        }

        using var scope = runtime.Enter();
        if (endReason != MatchEndReason.LastSurvivor && !runtime.IsEnded)
        {
            logger.LogInformation("Swarm match resolved: matchingId={MatchingId}, WinnerId={WinnerId}, Criterion={Criterion}", matchingId, winnerId, tieBreakCriterion);
            gameEventLogManager.LogSystem(matchingId, $"survivor_settlement winner={winnerId} reason={endReason} criterion={tieBreakCriterion}");
        }

        if (!runtime.TryMarkEnded())
        {
            logger.LogDebug("Duplicate match finalization ignored: MatchingId={MatchingId}", matchingId);
            return;
        }

        var sessionSnapshot = runtime.GetSessions();
        var players = BuildPlayerResults(runtime, winnerId);
        byte[] resultPayload = MessagePackSerializer.Serialize(new G_TO_C_GAME_RESULT
        {
            WinnerId = winnerId,
            IsTimeout = isTimeout,
            Players = players
        });

        MatchSummaryDocument? summaryRequest = null;
        IReadOnlyList<GameEventEntry> capturedEvents = [];
        if (gameEventLogManager.TryBeginFinalization(matchingId))
        {
            try
            {
                var finalPlayerStats = new List<MatchFinalPlayerStats>(players.Count);
                foreach (var player in players)
                {
                    finalPlayerStats.Add(new MatchFinalPlayerStats(
                        player.PlayerId,
                        player.Rank,
                        player.SurvivalTimeSeconds,
                        player.KillCount,
                        player.TotalDamageDealt,
                        player.TotalRecovery,
                        player.OrbCount));
                }
                gameEventLogManager.LogMatchEnded(matchingId, winnerId, endReason.ToString(), tieBreakCriterion.ToString(), finalPlayerStats);
            }
            catch (Exception ex)
            {
                logger.LogWarning(ex, "Final match event logging failed; summary capture will continue: MatchingId={MatchingId}", matchingId);
            }

            try
            {
                summaryRequest = MatchSummaryFileStore.Prepare(gameEventLogManager, logger, matchingId, endReason.ToString(), winnerId, out capturedEvents);
            }
            catch (Exception ex)
            {
                logger.LogWarning(ex, "Final match summary capture failed; terminal publication will continue: MatchingId={MatchingId}", matchingId);
            }
        }

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
                    IsEscaped = !isTimeout && session.PlayerId == winnerId
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

        var capturedSummary = summaryRequest;
        if (capturedSummary != null)
        {
            runtime.AfterRelease.Add(() => matchSummaryFileStore.Save(capturedSummary, capturedEvents, logger));
        }
    }

    public bool TryEndOnScoreTimeout(MatchRuntime runtime, DateTime nowUtc)
    {
        if (!Monitor.IsEntered(runtime.MatchLock))
        {
            throw new InvalidOperationException("Score timeout requires the match lock.");
        }
        if (runtime.TimeoutResultProcessed || runtime.IsEnded)
        {
            return false;
        }

        var startedAtUtc = runtime.StartsAtUtc;
        if (startedAtUtc == null)
        {
            if (!runtime.FallbackStartedAtUtc.HasValue)
            {
                runtime.FallbackStartedAtUtc = nowUtc;
                return false;
            }
            startedAtUtc = runtime.FallbackStartedAtUtc.Value;
        }

        if ((nowUtc - startedAtUtc.Value).TotalSeconds < Config.SWARM_MATCH_DURATION_SECONDS)
        {
            return false;
        }

        var candidates = new List<(long PlayerId, int OrbCount, int TierSum, int Health)>();
        foreach (var player in runtime.GetAlivePlayers())
        {
            var (orbCount, tierSum) = runtime.GetOrbs(player.PlayerId).GetOrbScore();
            candidates.Add((player.PlayerId, orbCount, tierSum, player.Health));
        }
        candidates = candidates
            .OrderByDescending(candidate => candidate.OrbCount)
            .ThenByDescending(candidate => candidate.TierSum)
            .ThenByDescending(candidate => candidate.Health)
            .ThenBy(candidate => candidate.PlayerId)
            .ToList();

        long winnerId = candidates.Count > 0 ? candidates[0].PlayerId : 0;
        runtime.TimeoutResultProcessed = true;
        var scoreLog = new List<string>();
        foreach (var candidate in candidates)
        {
            scoreLog.Add($"{candidate.PlayerId}:{candidate.OrbCount}:{candidate.TierSum}");
        }
        gameEventLogManager.LogSystem(runtime.MatchingId, "match_score_result " + string.Join(",", scoreLog));

        FinalizeMatch(runtime.MatchingId, winnerId, MatchEndReason.OrbScoreTimeout);
        runtime.AutoAttack.Clear();
        return true;
    }

    public void BroadcastOrbRankings(MatchRuntime runtime, List<GameClientSession> sessions)
    {
        if (!Monitor.IsEntered(runtime.MatchLock))
        {
            throw new InvalidOperationException("Orb rankings broadcast requires the match lock.");
        }
        if (sessions.Count == 0)
        {
            return;
        }

        var entries = new List<(long PlayerId, int Orbs, int TierSum)>();
        foreach (var player in runtime.GetPlayers())
        {
            if (player.IsEliminated)
            {
                entries.Add((player.PlayerId, 0, 0));
                continue;
            }
            var (orbCount, tierSum) = runtime.GetOrbs(player.PlayerId).GetOrbScore();
            entries.Add((player.PlayerId, orbCount, tierSum));
        }
        if (entries.Count == 0)
        {
            return;
        }
        entries = entries
            .OrderByDescending(entry => entry.Orbs)
            .ThenByDescending(entry => entry.TierSum)
            .ThenBy(entry => entry.PlayerId)
            .ToList();

        var playerIds = new List<long>(entries.Count);
        var orbCounts = new List<int>(entries.Count);
        var signatureParts = new List<string>(entries.Count);
        foreach (var entry in entries)
        {
            playerIds.Add(entry.PlayerId);
            orbCounts.Add(entry.Orbs);
            signatureParts.Add($"{entry.PlayerId}:{entry.Orbs}");
        }
        string signature = string.Join("|", signatureParts);
        bool isFirstBroadcast = runtime.OrbRankingsSignature == null;
        if (!isFirstBroadcast && runtime.OrbRankingsSignature == signature)
        {
            return;
        }

        runtime.OrbRankingsSignature = signature;
        if (isFirstBroadcast)
        {
            logger.LogInformation("Orb rankings broadcast armed: MatchingId={MatchingId}, Participants={Count}, Sessions={Sessions}", runtime.MatchingId, entries.Count, sessions.Count);
        }
        using var packet = Packet.Create((int)Protocol.G_TO_C_ORB_RANKINGS);
        packet.SetBody(MessagePackSerializer.Serialize(new G_TO_C_ORB_RANKINGS
        {
            PlayerIds = playerIds,
            OrbCounts = orbCounts
        }));
        foreach (var session in sessions)
        {
            session.TrySend(packet);
        }
    }

    public List<GameResultPlayerInfo> BuildPlayerResults(MatchRuntime runtime, long winnerId)
    {
        if (!Monitor.IsEntered(runtime.MatchLock))
        {
            throw new InvalidOperationException("Building player results requires the match lock.");
        }
        var endedAtUtc = DateTime.UtcNow;
        var startedAtUtc = runtime.StartsAtUtc ?? endedAtUtc;
        var resultRows = runtime.BuildGameResult();

        var killCountsByPlayerId = new Dictionary<long, int>();
        foreach (var row in resultRows)
        {
            if (row.attackerPlayerId == 0 || row.reason != EliminationReason.HEALTH_ZERO)
            {
                continue;
            }
            killCountsByPlayerId.TryGetValue(row.attackerPlayerId, out int killCount);
            killCountsByPlayerId[row.attackerPlayerId] = killCount + 1;
        }

        var playerResults = new List<GameResultPlayerInfo>();
        foreach (var row in resultRows)
        {
            var player = runtime.GetParticipant(row.playerId)!;
            var stats = gameEventLogManager.GetResultStats(runtime.MatchingId, row.playerId);
            var orbs = runtime.GetOrbs(row.playerId);
            bool isWinner = row.playerId == winnerId;
            killCountsByPlayerId.TryGetValue(row.playerId, out int pvpKillCount);
            var survivalEndUtc = row.eliminatedAt ?? endedAtUtc;
            playerResults.Add(new GameResultPlayerInfo
            {
                PlayerId = row.playerId,
                Name = string.IsNullOrEmpty(player.Profile.Name) ? $"Player{Math.Abs(row.playerId)}" : player.Profile.Name,
                EliminationReason = row.reason,
                FinalStatus = row.finalStatus,
                Health = player.Health,
                MaxHealth = Config.MAX_HEALTH,
                WearItemIdList = player.Profile.WearItemIdList is { Count: > 0 } wearItemIds ? new List<int>(wearItemIds) : new List<int>(),
                SurvivalTimeSeconds = Math.Max(0, (int)Math.Floor((survivalEndUtc - startedAtUtc).TotalSeconds)),
                KillCount = pvpKillCount + stats.MonsterKillCount,
                TotalDamageDealt = stats.TotalDamageDealt + stats.MonsterDamageDealt,
                TotalRecovery = stats.TotalRecovery,
                AttackerPlayerId = row.attackerPlayerId,
                EliminatedArea = row.eliminatedArea,
                Rank = isWinner ? 1 : row.eliminationRank,
                FinalOrbTier = isWinner ? orbs.GetHighestOrbTier() : row.finalOrbTier,
                OrbCount = orbs.GetOrbScore().OrbCount
            });
        }

        return AssignRankings(playerResults, winnerId);
    }

    internal static List<GameResultPlayerInfo> AssignRankings(IEnumerable<GameResultPlayerInfo> players, long winnerId = 0)
    {
        var ordered = players
            .Where(player => player.PlayerId != 0)
            .OrderByDescending(player => winnerId != 0 && player.PlayerId == winnerId)
            .ThenBy(player => player.Rank > 0 ? player.Rank : 0)
            .ThenByDescending(player => player.OrbCount)
            .ThenByDescending(player => player.SurvivalTimeSeconds)
            .ThenByDescending(player => player.KillCount)
            .ThenByDescending(player => player.TotalDamageDealt)
            .ThenByDescending(player => player.TotalRecovery)
            .ThenBy(player => player.PlayerId)
            .ToList();

        for (int i = 0; i < ordered.Count; i++)
        {
            ordered[i].Rank = i + 1;
        }

        return ordered;
    }
}
