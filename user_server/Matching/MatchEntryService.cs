using user_server.matching.creation;
using user_server.matching.queue;
using Microsoft.Extensions.Logging;
using network.common;
using network.common.data.models;
using network.gamehandoff;
using network.infrastructure.redis;
using user_server.sessions;

namespace user_server.matching;

/// <summary>
///     매칭된 플레이어가 GameServer에 입장할 수 있도록 준비하고, 입장 실패 시 정리를 담당한다.
///     매치 구성을 Redis에 저장하고, 플레이어별 입장 티켓을 발급해 매칭 결과를 전달한다.
///     MatchCreationService가 전원에게 결과를 전달한 뒤 호출하면 입장 가능 상태를 기록한다.
///
///     45초 뒤 입장 완료 여부를 확인하고,
///     아직 대기 중이면 입장을 취소한 뒤 실패 알림을 보내고 세션의 매칭 배정과 Redis 예약을 해제한다.
/// </summary>
internal sealed class MatchEntryService(
    IRedisOperations redisOperations,
    GameHandoffTicketService gameHandoffTicketService,
    MatchingReservationService reservations,
    IPlayerSessionRouter sessions,
    Func<Func<Task>, string, bool> tryRunBackgroundOperation,
    ILogger logger,
    CancellationToken shutdownToken) : IMatchEntryService
{
    public async Task StoreMatchManifestAsync(long matchingId, MatchManifest manifest)
    {
        string handoffKey = MatchingRedisKeys.Key(matchingId);
        byte[] serialized = MessagePack.MessagePackSerializer.Serialize(manifest);
        await redisOperations.HashSetWithExpiryAsync(
            handoffKey,
            MatchingRedisKeys.ManifestField,
            serialized,
            MatchingRedisKeys.HandoffStateLifetime);
    }

    public async Task<bool> DeliverMatchingSuccessAsync(MatchingQueueData request, long matchingId, GameServerAllocation gameServer)
    {
        long playerId = request.PlayerId;
        logger.LogInformation("Processing matched player {DataPlayerId}", playerId);

        string requestId = request.RequestId;
        if (string.IsNullOrWhiteSpace(requestId))
        {
            logger.LogWarning("Matched player has no valid matching request id: PlayerId={DataPlayerId}", playerId);
            return false;
        }

        string gameHandoffTicket = await gameHandoffTicketService.IssueAsync(new GameHandoffContext
        {
            PlayerId = playerId,
            MatchingId = matchingId,
            GameServerNodeId = gameServer.NodeId
        });

        long gameEndTimestamp = DateTimeOffset.UtcNow.AddMinutes(Config.GAME_DURATION_MINUTES).ToUnixTimeMilliseconds();
        var result = new U_TO_C_MATCHING_SUCCESS
        {
            MatchingId = matchingId,
            GameServerIp = gameServer.PublicHost,
            GameServerPort = gameServer.PublicPort,
            GameEndTimestamp = gameEndTimestamp,
            GameHandoffTicket = gameHandoffTicket
        };

        if (!await sessions.DeliverMatchingSuccessAsync(playerId, requestId, result))
        {
            logger.LogWarning("Matching success was not accepted by the exact session owner: PlayerId={DataPlayerId}", playerId);
            return false;
        }

        logger.LogInformation("Matching success sent: PlayerId={DataPlayerId}", playerId);
        return true;
    }

    private async Task EnsureEntryStatePendingAsync(long matchingId)
    {
        string stateKey = MatchingRedisKeys.EntryStateKey(matchingId);
        bool created = await redisOperations.StringSetIfNotExistsAsync(
            stateKey,
            MatchingRedisKeys.EntryPendingState,
            MatchingRedisKeys.HandoffStateLifetime);
        if (created)
        {
            return;
        }

        var existingState = await redisOperations.StringGetAsync(stateKey);
        if (string.Equals(existingState.ToString(), MatchingRedisKeys.EntryPendingState, StringComparison.Ordinal))
        {
            return;
        }

        throw new InvalidOperationException($"Entry state for match {matchingId} is already '{existingState}'.");
    }

    public async Task MarkEntryReadyAsync(long matchingId)
    {
        await EnsureEntryStatePendingAsync(matchingId);
        await redisOperations.HashSetWithExpiryAsync(
            MatchingRedisKeys.Key(matchingId),
            MatchingRedisKeys.EntryReadyField,
            [MatchingRedisKeys.EntryReadyValue],
            MatchingRedisKeys.HandoffStateLifetime);
    }

    public async Task<bool> TryCancelEntryForRollbackAsync(long matchingId)
    {
        if (matchingId <= 0)
        {
            return true;
        }

        string stateKey = MatchingRedisKeys.EntryStateKey(matchingId);
        try
        {
            bool canceled = await redisOperations.StringSetIfEqualsAsync(
                stateKey,
                MatchingRedisKeys.EntryPendingState,
                MatchingRedisKeys.EntryCanceledState,
                MatchingRedisKeys.HandoffStateLifetime);
            if (canceled)
            {
                return true;
            }

            var entryState = await redisOperations.StringGetAsync(stateKey);
            if (entryState.IsNullOrEmpty || string.Equals(entryState.ToString(), MatchingRedisKeys.EntryCanceledState,
                    StringComparison.Ordinal))
            {
                return true;
            }

            if (string.Equals(entryState.ToString(), MatchingRedisKeys.EntryCompletedState, StringComparison.Ordinal))
            {
                logger.LogInformation(
                    "Skipped matching rollback because GameServer completed entry first: MatchingId={MatchingId}",
                    matchingId);
                return false;
            }

            logger.LogWarning(
                "Skipped matching rollback because entry state is uncertain: MatchingId={MatchingId}, State={State}",
                matchingId, entryState.ToString());

            return false;
        }
        catch (Exception ex)
        {
            logger.LogWarning(ex, "Could not confirm entry cancellation; skipping matching rollback: MatchingId={MatchingId}",
                matchingId);
            return false;
        }
    }

    public async Task DeleteMatchEntryDataAsync(long matchingId)
    {
        if (matchingId <= 0)
        {
            return;
        }

        try
        {
            await redisOperations.KeyDeleteAsync(MatchingRedisKeys.Key(matchingId));
        }
        catch (Exception ex)
        {
            logger.LogWarning(ex, "Failed to delete rolled-back matching handoff; TTL remains as fallback: MatchingId={MatchingId}", matchingId);
        }
    }

    public bool StartEntryTimeoutCheck(long matchingId, IReadOnlyCollection<long> humanPlayerIds)
    {
        if (humanPlayerIds.Count == 0)
            return false;

        long[] playerIds = humanPlayerIds.ToArray();
        return tryRunBackgroundOperation(
            () => CheckEntryTimeoutAsync(matchingId, playerIds),
            $"matching-entry-timeout:{matchingId}");
    }

    private async Task CheckEntryTimeoutAsync(long matchingId, long[] humanPlayerIds)
    {
        try
        {
            await Task.Delay(MatchingRedisKeys.EntryTimeout, shutdownToken);
            shutdownToken.ThrowIfCancellationRequested();

            string stateKey = MatchingRedisKeys.EntryStateKey(matchingId);

            // 아직 입장 대기중이면 취소 (GameServer에서 completed로 바꿨어야 함)
            bool canceled = await redisOperations.StringSetIfEqualsAsync(
                stateKey,
                MatchingRedisKeys.EntryPendingState,
                MatchingRedisKeys.EntryCanceledState,
                MatchingRedisKeys.HandoffStateLifetime);
            if (!canceled)
            {
                var entryState = await redisOperations.StringGetAsync(stateKey);
                if (string.Equals(entryState.ToString(), MatchingRedisKeys.EntryCompletedState, StringComparison.Ordinal))
                {
                    return;
                }

                // 취소된 상태가 확인될 때만 정리한다. 없거나 알 수 없는 상태는 임의로 지우지 않는다.
                if (!string.Equals(entryState.ToString(), MatchingRedisKeys.EntryCanceledState, StringComparison.Ordinal))
                {
                    logger.LogWarning(
                        "Skipped entry timeout cleanup because entry state is uncertain: MatchingId={MatchingId}, State={State}",
                        matchingId, entryState.ToString());
                    return;
                }
            }

            logger.LogWarning(
                "Matching entry timed out; rolling back the whole human roster: MatchingId={MatchingId}, Players={PlayerCount}",
                matchingId, humanPlayerIds.Length);
            await DeleteMatchEntryDataAsync(matchingId);
            foreach (long playerId in humanPlayerIds)
            {
                try
                {
                    await NotifyEntryFailedAsync(playerId, matchingId);
                }
                catch (Exception ex)
                {
                    logger.LogWarning(ex, "Matching entry timeout notification failed: PlayerId={PlayerId}, MatchingId={MatchingId}", playerId, matchingId);
                }

                try
                {
                    sessions.ClearMatchingAssignment(playerId, matchingId);
                }
                catch (Exception ex)
                {
                    logger.LogWarning(ex, "Matching assignment cleanup failed: PlayerId={PlayerId}, MatchingId={MatchingId}", playerId, matchingId);
                }

                try
                {
                    await reservations.ReleaseMatchingReservationAsync(playerId, matchingId);
                }
                catch (Exception ex)
                {
                    logger.LogWarning(ex, "Matching reservation cleanup failed; TTL remains as fallback: PlayerId={PlayerId}, MatchingId={MatchingId}", playerId, matchingId);
                }
            }
        }
        catch (OperationCanceledException) when (shutdownToken.IsCancellationRequested)
        {
            // 서버 종료 시 입장 대기를 중단한다.
        }
        catch (Exception ex)
        {
            logger.LogWarning(ex, "Could not verify entry timeout; TTL remains as fallback: MatchingId={MatchingId}", matchingId);
        }
    }

    public async Task NotifyBatchFailedAsync(IEnumerable<MatchingQueueData> players, long matchingId)
    {
        try
        {
            foreach (var player in players.DistinctBy(request => request.PlayerId))
            {
                if (await sessions.DeliverMatchingFailedAsync(player.PlayerId, matchingId, player.RequestId, ErrorCode.MATCHING_FAILED))
                {
                    return;
                }
                logger.LogWarning("Matching rollback notification was rejected or found no session: PlayerId={PlayerId}, MatchingId={MatchingId}", player.PlayerId, matchingId);
            }
        }
        catch (Exception ex)
        {
            logger.LogWarning(ex, "Failed to send matching batch rollback notification");
        }
    }

    public async Task NotifyEntryFailedAsync(long playerId, long matchingId)
    {
        if (await sessions.DeliverEntryFailedAsync(playerId, matchingId, ErrorCode.MATCHING_FAILED))
        {
            return;
        }
        logger.LogWarning("Matching entry failure was rejected or found no session: PlayerId={PlayerId}, MatchingId={MatchingId}", playerId, matchingId);
    }
}
