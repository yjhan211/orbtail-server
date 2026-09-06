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
            logger.LogWarning(
                "Matching success was not accepted by the exact session owner: PlayerId={DataPlayerId}",
                playerId);
            return false;
        }

        logger.LogInformation("Matching success sent: PlayerId={DataPlayerId}", playerId);
        return true;
    }

    public async Task MarkEntryReadyAsync(long matchingId)
    {
        await EnsureEntryStatePendingAsync(matchingId);
        string handoffKey = MatchingRedisKeys.Key(matchingId);
        Exception? lastError = null;
        for (int attempt = 0; attempt < 3; attempt++)
        {
            try
            {
                await redisOperations.HashSetWithExpiryAsync(
                    handoffKey,
                    MatchingRedisKeys.EntryReadyField,
                    [MatchingRedisKeys.EntryReadyValue],
                    MatchingRedisKeys.HandoffStateLifetime);
                return;
            }
            catch (Exception ex)
            {
                lastError = ex;
                try
                {
                    var marker = await redisOperations.HashGetAsync(
                        handoffKey,
                        MatchingRedisKeys.EntryReadyField);
                    if (!marker.IsNullOrEmpty && ((byte[])marker!).AsSpan().SequenceEqual([MatchingRedisKeys.EntryReadyValue]))
                    {
                        logger.LogWarning(ex, "Matching entry marker write response was lost; read-back confirmed commit: MatchingId={MatchingId}",
                            matchingId);
                        return;
                    }
                }
                catch (Exception readBackError)
                {
                    logger.LogWarning(
                        readBackError,
                        "Matching entry marker read-back failed: MatchingId={MatchingId}, Attempt={Attempt}",
                        matchingId,
                        attempt + 1);
                }
            }
        }

        throw new InvalidOperationException($"Could not confirm the entry marker for match {matchingId}.", lastError);
    }

    private async Task EnsureEntryStatePendingAsync(long matchingId)
    {
        string stateKey = MatchingRedisKeys.EntryStateKey(matchingId);
        Exception? lastError = null;
        for (int attempt = 0; attempt < 3; attempt++)
        {
            string? conflictingState;
            try
            {
                bool created = await redisOperations.StringSetIfNotExistsAsync(
                    stateKey,
                    MatchingRedisKeys.EntryPendingState,
                    MatchingRedisKeys.HandoffStateLifetime);
                if (created)
                    return;

                var existing = await redisOperations.StringGetAsync(stateKey);
                if (!existing.IsNullOrEmpty &&
                    string.Equals(existing.ToString(), MatchingRedisKeys.EntryPendingState,
                        StringComparison.Ordinal))
                    return;
                conflictingState = existing.ToString();
            }
            catch (Exception ex)
            {
                lastError = ex;
                try
                {
                    var existing = await redisOperations.StringGetAsync(stateKey);
                    if (!existing.IsNullOrEmpty &&
                        string.Equals(existing.ToString(), MatchingRedisKeys.EntryPendingState,
                            StringComparison.Ordinal))
                    {
                        logger.LogWarning(
                            ex,
                            "Entry state creation response was lost; read-back confirmed pending: MatchingId={MatchingId}",
                            matchingId);
                        return;
                    }
                }
                catch (Exception readBackError)
                {
                    logger.LogWarning(
                        readBackError,
                        "Entry state read-back failed: MatchingId={MatchingId}, Attempt={Attempt}",
                        matchingId,
                        attempt + 1);
                }

                continue;
            }

            // Redis 연산 자체는 성공했으므로 pending이 아닌 값은 일시 장애가 아니라 확정된 상태 충돌이다.
            throw new InvalidOperationException(
                $"Entry state for match {matchingId} is already '{conflictingState}'.");
        }

        throw new InvalidOperationException(
            $"Could not initialize entry state for match {matchingId}.",
            lastError);
    }

    /// <summary>
    ///     rollback 전에 entry state를 pending → canceled로 CAS한다. Game Server가 먼저 completed로 바꿨거나
    ///     상태를 확정할 수 없으면 false — 그 매치는 되돌리지 않고 TTL에 맡긴다.
    /// </summary>
    public async Task<bool> TryCancelEntryForRollbackAsync(long matchingId)
    {
        if (matchingId <= 0)
            return true;

        string stateKey = MatchingRedisKeys.EntryStateKey(matchingId);
        for (int attempt = 0; attempt < 3; attempt++)
        {
            try
            {
                bool canceled = await redisOperations.StringSetIfEqualsAsync(
                    stateKey,
                    MatchingRedisKeys.EntryPendingState,
                    MatchingRedisKeys.EntryCanceledState,
                    MatchingRedisKeys.HandoffStateLifetime);
                if (canceled)
                    return true;

                var state = await redisOperations.StringGetAsync(stateKey);
                if (state.IsNullOrEmpty ||
                    string.Equals(state.ToString(), MatchingRedisKeys.EntryCanceledState,
                        StringComparison.Ordinal))
                    return true;
                if (string.Equals(state.ToString(), MatchingRedisKeys.EntryCompletedState,
                        StringComparison.Ordinal))
                {
                    logger.LogInformation(
                        "Skipped matching rollback because GameServer completed entry first: MatchingId={MatchingId}",
                        matchingId);
                    return false;
                }
            }
            catch (Exception ex)
            {
                logger.LogWarning(
                    ex,
                    "Could not confirm entry cancellation: MatchingId={MatchingId}, Attempt={Attempt}",
                    matchingId,
                    attempt + 1);
            }

            await Task.Delay(TimeSpan.FromMilliseconds(50));
        }

        // 알 수 없는 terminal 상태는 Game Server가 이미 완료했을 수 있는 매치를 되돌릴 권한이 아니다.
        // entry/reservation TTL이 복구 fallback으로 남는다.
        logger.LogError(
            "Skipped ambiguous matching rollback after bounded entry-state reconciliation: MatchingId={MatchingId}",
            matchingId);
        return false;
    }

    public async Task DeleteHandoffBestEffortAsync(long matchingId)
    {
        if (matchingId <= 0)
            return;

        try
        {
            await redisOperations.KeyDeleteAsync(MatchingRedisKeys.Key(matchingId));
        }
        catch (Exception ex)
        {
            logger.LogWarning(
                ex,
                "Failed to delete rolled-back matching handoff; TTL remains as fallback: MatchingId={MatchingId}",
                matchingId);
        }
    }

    /// <summary>
    ///     45초 process-local 입장 watchdog을 등록한다. 등록 실패(shutdown)면 false.
    /// </summary>
    public bool StartEntryWatchdog(long matchingId, IReadOnlyCollection<long> humanPlayerIds)
    {
        long[] snapshot = humanPlayerIds
            .Where(playerId => playerId > 0)
            .Distinct()
            .ToArray();
        return snapshot.Length > 0 && tryRunBackgroundOperation(
            () => MonitorEntryAsync(matchingId, snapshot),
            $"matching-entry-watchdog:{matchingId}");
    }

    private async Task MonitorEntryAsync(long matchingId, IReadOnlyCollection<long> humanPlayerIds)
    {
        try
        {
            await Task.Delay(MatchingRedisKeys.EntryTimeout, shutdownToken);
        }
        catch (OperationCanceledException) when (shutdownToken.IsCancellationRequested)
        {
            return;
        }

        string stateKey = MatchingRedisKeys.EntryStateKey(matchingId);
        while (!shutdownToken.IsCancellationRequested)
        {
            try
            {
                bool canceled = await redisOperations.StringSetIfEqualsAsync(
                    stateKey,
                    MatchingRedisKeys.EntryPendingState,
                    MatchingRedisKeys.EntryCanceledState,
                    MatchingRedisKeys.HandoffStateLifetime);
                if (!canceled)
                {
                    var state = await redisOperations.StringGetAsync(stateKey);
                    if (!state.IsNullOrEmpty &&
                        string.Equals(state.ToString(), MatchingRedisKeys.EntryCompletedState,
                            StringComparison.Ordinal))
                        return;
                    if (state.IsNullOrEmpty ||
                        !string.Equals(state.ToString(), MatchingRedisKeys.EntryCanceledState,
                            StringComparison.Ordinal))
                    {
                        throw new InvalidOperationException(
                            $"Entry state for match {matchingId} is ambiguous: '{state}'.");
                    }
                }

                logger.LogWarning(
                    "Matching entry timed out; rolling back the whole human roster: MatchingId={MatchingId}, Players={PlayerCount}",
                    matchingId,
                    humanPlayerIds.Count);
                await DeleteHandoffBestEffortAsync(matchingId);
                foreach (long playerId in humanPlayerIds)
                {
                    try
                    {
                        await NotifyEntryFailedAsync(playerId, matchingId);
                    }
                    catch (Exception ex)
                    {
                        logger.LogWarning(
                            ex,
                            "Matching entry timeout notification failed: PlayerId={PlayerId}, MatchingId={MatchingId}",
                            playerId,
                            matchingId);
                    }

                    sessions.ClearMatchingAssignment(playerId, matchingId);
                    await reservations.ReleaseMatchingReservationAsync(playerId, matchingId);
                }
                return;
            }
            catch (Exception ex)
            {
                // 읽기 실패는 모호하다: Game Server가 entry을 commit했을 수 있다. Redis가 불안정한 동안
                // 거짓 rollback을 내지 않도록 재시도한다.
                logger.LogWarning(
                    ex,
                    "Could not verify matching entry timeout; retrying: MatchingId={MatchingId}",
                    matchingId);
                try
                {
                    await Task.Delay(TimeSpan.FromSeconds(1), shutdownToken);
                }
                catch (OperationCanceledException) when (shutdownToken.IsCancellationRequested)
                {
                    return;
                }
            }
        }
    }

    /// <summary>
    ///     rollback된 매치의 인간 전원에게 matchingId가 포함된 실패 패킷을 보낸다. 요청 ID가 다른 세션은 건너뛴다.
    /// </summary>
    public async Task NotifyBatchFailedAsync(IEnumerable<MatchingQueueData> players, long matchingId)
    {
        try
        {
            foreach (var player in players.DistinctBy(request => request.PlayerId))
            {
                if (!await sessions.DeliverMatchingFailedAsync(player.PlayerId, matchingId, player.RequestId, ErrorCode.MATCHING_FAILED))
                {
                    logger.LogWarning(
                        "Matching rollback notification was rejected or found no session: PlayerId={PlayerId}, MatchingId={MatchingId}",
                        player.PlayerId,
                        matchingId);
                }
            }
        }
        catch (Exception ex)
        {
            logger.LogWarning(ex, "Failed to send matching batch rollback notification");
        }
    }

    /// <summary>
    ///     입장 실패 패킷을 보낸다. 이 매치에 배정되지 않은 세션은 보낼 것이 없고, 세션이 아예 없으면 false다.
    /// </summary>
    public async Task NotifyEntryFailedAsync(long playerId, long matchingId)
    {
        if (!await sessions.DeliverEntryFailedAsync(playerId, matchingId, ErrorCode.MATCHING_FAILED))
        {
            logger.LogWarning(
                "Matching entry failure was rejected or found no session: PlayerId={PlayerId}, MatchingId={MatchingId}",
                playerId,
                matchingId);
        }
    }
}
