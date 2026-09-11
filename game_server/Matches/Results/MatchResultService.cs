using game_server.matches.logging;
using MessagePack;
using Microsoft.Extensions.Logging;
using network.common;
using network.common.data.models;
using network.packets;

namespace game_server.matches.results;

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

        var sessionSnapshot = matchRuntimes.GetOrThrow(matchingId).GetSessions();
        var players = BuildPlayerResults(matchingId, winnerId);
        byte[] resultPayload = MessagePackSerializer.Serialize(new G_TO_C_GAME_RESULT
        {
            WinnerId = winnerId,
            IsTimeout = isTimeout,
            Players = players
        });

        var completionNotifications = new List<Action>();
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
            Action? lifecyclePublication = null;
            try
            {
                lifecyclePublication = session.MarkGameEndedAndPrepareLifecyclePublication();
            }
            catch (Exception ex)
            {
                logger.LogWarning(ex, "Match terminal publication failed: MatchingId={MatchingId}, PlayerId={PlayerId}, Component={Component}", matchingId, session.PlayerId, "MarkGameEnded");
            }

            if (lifecyclePublication != null)
            {
                completionNotifications.Add(lifecyclePublication);
            }

        }
        var capturedSummary = summaryRequest;
        runtime.AfterRelease.Add(() => SendCompletionNotifications(matchingId, completionNotifications));

        if (capturedSummary != null)
        {
            runtime.AfterRelease.Add(() => matchSummaryFileStore.Save(capturedSummary, capturedEvents, logger));
        }
    }

    private void SendCompletionNotifications(long matchingId, IReadOnlyList<Action> lifecyclePublications)
    {
        foreach (var dispatch in lifecyclePublications)
        {
            try
            {
                dispatch();
            }
            catch (Exception ex)
            {
                logger.LogWarning(ex, "Deferred matching lifecycle dispatch failed: MatchingId={MatchingId}", matchingId);
            }
        }
    }

    public List<GameResultPlayerInfo> BuildPlayerResults(long matchingId, long winnerId)
    {
        var runtime = matchRuntimes.GetOrThrow(matchingId);
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
            long playerId = row.playerId;
            var player = runtime.GetParticipant(playerId)!;
            var playerProfile = player.Profile;
            var stats = gameEventLogManager.GetResultStats(matchingId, playerId);

            string? name = playerProfile?.Name;
            if (string.IsNullOrEmpty(name))
            {
                name = $"Player{Math.Abs(playerId)}";
            }
            int health = player.Health;
            var wearItemIds = new List<int>();
            if (playerProfile?.WearItemIdList is { Count: > 0 })
            {
                wearItemIds.AddRange(playerProfile.WearItemIdList);
            }

            var survivalEndUtc = row.eliminatedAt ?? endedAtUtc;
            int survivalSeconds = Math.Max(0, (int)Math.Floor((survivalEndUtc - startedAtUtc).TotalSeconds));
            killCountsByPlayerId.TryGetValue(playerId, out int playerKillCount);
            int killCount = playerKillCount + stats.MonsterKillCount;
            int orbCount = runtime.GetOrbs(playerId).GetOrbScore().OrbCount;
            bool isWinner = playerId == winnerId;
            int rank = isWinner ? 1 : row.eliminationRank;
            int finalOrbTier = isWinner ? runtime.GetOrbs(playerId).GetHighestOrbTier() : row.finalOrbTier;

            var result = new GameResultPlayerInfo
            {
                PlayerId = playerId,
                Name = name,
                EliminationReason = row.reason,
                FinalStatus = row.finalStatus,
                Health = health,
                MaxHealth = Config.MAX_HEALTH,
                WearItemIdList = wearItemIds,
                SurvivalTimeSeconds = survivalSeconds,
                KillCount = killCount,
                TotalDamageDealt = stats.TotalDamageDealt + stats.MonsterDamageDealt,
                TotalRecovery = stats.TotalRecovery,
                AttackerPlayerId = row.attackerPlayerId,
                EliminatedArea = row.eliminatedArea,
                Rank = rank,
                FinalOrbTier = finalOrbTier,
                OrbCount = orbCount
            };
            playerResults.Add(result);
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
