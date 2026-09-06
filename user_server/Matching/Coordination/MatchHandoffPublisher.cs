using user_server.matching.creation;
using user_server.matching.queue;
using Microsoft.Extensions.Logging;
using network.common;
using network.common.data;
using network.common.data.models;
using network.gamehandoff;
using network.helpers;
using network.infrastructure.redis;
using user_server.sessions;

namespace user_server.matching.coordination;

/// <summary>
///     매치 확정 뒤 Game Server 인계에 필요한 쓰기와 클라이언트 전달을 담당하는 port.
///     <see cref="MatchmakingPass" />가 테스트에서 전달·마커 순서를 관찰할 수 있도록 분리한다.
/// </summary>
internal interface IMatchHandoffPublisher
{
    public Task StoreMatchManifestAsync(long matchingId, MatchManifest manifest);

    public Task<bool> DeliverMatchingSuccessAsync(
        MatchingQueueEntry entry,
        long matchingId,
        List<PlayerInfo> playerRoster,
        GameServerAllocation gameServer);

    public Task MarkHandoffReadyAsync(long matchingId);
    public bool StartAdmissionWatchdog(long matchingId, IReadOnlyCollection<long> humanPlayerIds);
    public Task<bool> TryCancelAdmissionForRollbackAsync(long matchingId);
    public Task DeleteHandoffBestEffortAsync(long matchingId);
    public Task NotifyBatchFailedAsync(IEnumerable<MatchingQueueEntry> players, long matchingId);
}

/// <summary>
///     매치 handoff 발행자: handoff ticket 발급, 매치 manifest 기록, <c>admission_ready</c> 마커, admission state의
///     pending/canceled 전이, handoff 삭제, 사람별 성공·실패 패킷 전달, 45초 process-local 입장 watchdog.
///     불변식: 성공 패킷은 전원 인간의 세션 송신 큐에 들어간 뒤에만 <c>admission_ready</c>를 쓴다
///     (호출 순서는 <see cref="MatchmakingPass" />가 지킨다). Redis 응답 유실은 read-back으로 보정하고,
///     watchdog은 shutdown token으로만 멈춘다. 프로세스 상태는 갖지 않으며 background task 등록은 소유자에게 위임한다.
/// </summary>
internal sealed class MatchHandoffPublisher(
    IRedisOperations redisOperations,
    GameHandoffTicketService gameHandoffTicketService,
    MatchingQueueClaimCoordinator claims,
    IPlayerSessionRouter sessions,
    Func<Func<Task>, string, bool> tryRunBackgroundOperation,
    CancellationToken shutdownToken,
    ILogger logger) : IMatchHandoffPublisher
{
    /// <summary>
    ///     Game Server가 매치당 한 번 읽는 구성(사람·봇 ID)을 기록한다.
    /// </summary>
    public async Task StoreMatchManifestAsync(long matchingId, MatchManifest manifest)
    {
        string handoffKey = MatchingHandoffRedisKeys.Key(matchingId);
        byte[] serialized = MessagePack.MessagePackSerializer.Serialize(manifest);
        await redisOperations.HashSetWithExpiryAsync(
            handoffKey,
            MatchingHandoffRedisKeys.ManifestField,
            serialized,
            MatchingHandoffRedisKeys.HandoffStateLifetime);
    }

    /// <summary>
    ///     사람 한 명에게 handoff ticket을 발급하고 성공 패킷을 세션 송신 큐에 넣는다(세션이 다른 프로세스에 있으면 라우터가 위임).
    ///     세션이 없거나 요청 ID가 다르면 false. 전송 실패 시 배정을 남기지 않는 것은 세션 쪽 책임이다.
    /// </summary>
    public async Task<bool> DeliverMatchingSuccessAsync(
        MatchingQueueEntry entry,
        long matchingId,
        List<PlayerInfo> playerRoster,
        GameServerAllocation gameServer)
    {
        long playerId = entry.PlayerId;
        logger.LogInformation("Processing matched player {DataPlayerId}", playerId);

        string requestId = entry.RequestId;
        if (!MatchingRequestTokens.IsSafeTokenComponent(requestId))
        {
            logger.LogWarning("Matched player has no valid matching request id: PlayerId={DataPlayerId}", playerId);
            return false;
        }

        // ticket은 먼저 발급한다. 응답 유실 시 이미 전달됐을 수도 있으나 admission_ready 전에는 입장할 수 없다.
        // 실패한 매치의 handoff는 롤백으로 정리하고, 소비되지 않은 ticket은 3분 TTL로 사라진다.
        string gameHandoffTicket = await gameHandoffTicketService.IssueAsync(new GameHandoffContext
        {
            PlayerId = playerId,
            MatchingId = matchingId,
            GameServerNodeId = gameServer.NodeId
        });

        long gameEndTimestamp = DateTimeOffset.UtcNow.AddMinutes(Config.GAME_DURATION_MINUTES)
            .ToUnixTimeMilliseconds();

        var result = new U_TO_C_MATCHING_SUCCESS
        {
            MatchingId = matchingId,
            GameServerIp = gameServer.PublicHost,
            GameServerPort = gameServer.PublicPort,
            GameEndTimestamp = gameEndTimestamp,
            GameHandoffTicket = gameHandoffTicket,
            PlayerRoster = playerRoster
        };

        // 세션이 어느 User Server에 있든 라우터가 요청 ID fence를 확인한 뒤 송신 큐에 넣는다.
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

    /// <summary>
    ///     admission state를 pending으로 만든 뒤 <c>admission_ready</c> 마커를 쓴다. 응답 유실은 read-back으로 확인한다.
    /// </summary>
    public async Task MarkHandoffReadyAsync(long matchingId)
    {
        await EnsureAdmissionStatePendingAsync(matchingId);
        string handoffKey = MatchingHandoffRedisKeys.Key(matchingId);
        Exception? lastError = null;
        for (int attempt = 0; attempt < 3; attempt++)
        {
            try
            {
                await redisOperations.HashSetWithExpiryAsync(
                    handoffKey,
                    MatchingHandoffRedisKeys.AdmissionReadyField,
                    [MatchingHandoffRedisKeys.AdmissionReadyValue],
                    MatchingHandoffRedisKeys.HandoffStateLifetime);
                return;
            }
            catch (Exception ex)
            {
                lastError = ex;
                try
                {
                    var marker = await redisOperations.HashGetAsync(
                        handoffKey,
                        MatchingHandoffRedisKeys.AdmissionReadyField);
                    if (!marker.IsNullOrEmpty &&
                        ((byte[])marker!).AsSpan().SequenceEqual([MatchingHandoffRedisKeys.AdmissionReadyValue]))
                    {
                        logger.LogWarning(
                            ex,
                            "Matching admission marker write response was lost; read-back confirmed commit: MatchingId={MatchingId}",
                            matchingId);
                        return;
                    }
                }
                catch (Exception readBackError)
                {
                    logger.LogWarning(
                        readBackError,
                        "Matching admission marker read-back failed: MatchingId={MatchingId}, Attempt={Attempt}",
                        matchingId,
                        attempt + 1);
                }
            }
        }

        throw new InvalidOperationException(
            $"Could not confirm the admission marker for match {matchingId}.",
            lastError);
    }

    private async Task EnsureAdmissionStatePendingAsync(long matchingId)
    {
        string stateKey = MatchingHandoffRedisKeys.AdmissionStateKey(matchingId);
        Exception? lastError = null;
        for (int attempt = 0; attempt < 3; attempt++)
        {
            string? conflictingState = null;
            try
            {
                bool created = await redisOperations.StringSetIfNotExistsAsync(
                    stateKey,
                    MatchingHandoffRedisKeys.AdmissionPendingState,
                    MatchingHandoffRedisKeys.HandoffStateLifetime);
                if (created)
                    return;

                var existing = await redisOperations.StringGetAsync(stateKey);
                if (!existing.IsNullOrEmpty &&
                    string.Equals(existing.ToString(), MatchingHandoffRedisKeys.AdmissionPendingState,
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
                        string.Equals(existing.ToString(), MatchingHandoffRedisKeys.AdmissionPendingState,
                            StringComparison.Ordinal))
                    {
                        logger.LogWarning(
                            ex,
                            "Admission state creation response was lost; read-back confirmed pending: MatchingId={MatchingId}",
                            matchingId);
                        return;
                    }
                }
                catch (Exception readBackError)
                {
                    logger.LogWarning(
                        readBackError,
                        "Admission state read-back failed: MatchingId={MatchingId}, Attempt={Attempt}",
                        matchingId,
                        attempt + 1);
                }

                continue;
            }

            // Redis 연산 자체는 성공했으므로 pending이 아닌 값은 일시 장애가 아니라 확정된 상태 충돌이다.
            throw new InvalidOperationException(
                $"Admission state for match {matchingId} is already '{conflictingState}'.");
        }

        throw new InvalidOperationException(
            $"Could not initialize admission state for match {matchingId}.",
            lastError);
    }

    /// <summary>
    ///     rollback 전에 admission state를 pending → canceled로 CAS한다. Game Server가 먼저 completed로 바꿨거나
    ///     상태를 확정할 수 없으면 false — 그 매치는 되돌리지 않고 TTL에 맡긴다.
    /// </summary>
    public async Task<bool> TryCancelAdmissionForRollbackAsync(long matchingId)
    {
        if (matchingId <= 0)
            return true;

        string stateKey = MatchingHandoffRedisKeys.AdmissionStateKey(matchingId);
        for (int attempt = 0; attempt < 3; attempt++)
        {
            try
            {
                bool canceled = await redisOperations.StringSetIfEqualsAsync(
                    stateKey,
                    MatchingHandoffRedisKeys.AdmissionPendingState,
                    MatchingHandoffRedisKeys.AdmissionCanceledState,
                    MatchingHandoffRedisKeys.HandoffStateLifetime);
                if (canceled)
                    return true;

                var state = await redisOperations.StringGetAsync(stateKey);
                if (state.IsNullOrEmpty ||
                    string.Equals(state.ToString(), MatchingHandoffRedisKeys.AdmissionCanceledState,
                        StringComparison.Ordinal))
                    return true;
                if (string.Equals(state.ToString(), MatchingHandoffRedisKeys.AdmissionCompletedState,
                        StringComparison.Ordinal))
                {
                    logger.LogInformation(
                        "Skipped matching rollback because GameServer completed admission first: MatchingId={MatchingId}",
                        matchingId);
                    return false;
                }
            }
            catch (Exception ex)
            {
                logger.LogWarning(
                    ex,
                    "Could not confirm admission cancellation: MatchingId={MatchingId}, Attempt={Attempt}",
                    matchingId,
                    attempt + 1);
            }

            await Task.Delay(TimeSpan.FromMilliseconds(50));
        }

        // 알 수 없는 terminal 상태는 Game Server가 이미 완료했을 수 있는 매치를 되돌릴 권한이 아니다.
        // admission/claim TTL이 복구 fallback으로 남는다.
        logger.LogError(
            "Skipped ambiguous matching rollback after bounded admission-state reconciliation: MatchingId={MatchingId}",
            matchingId);
        return false;
    }

    public async Task DeleteHandoffBestEffortAsync(long matchingId)
    {
        if (matchingId <= 0)
            return;

        try
        {
            await redisOperations.KeyDeleteAsync(MatchingHandoffRedisKeys.Key(matchingId));
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
    public bool StartAdmissionWatchdog(long matchingId, IReadOnlyCollection<long> humanPlayerIds)
    {
        long[] snapshot = humanPlayerIds
            .Where(playerId => playerId > 0)
            .Distinct()
            .ToArray();
        return snapshot.Length > 0 && tryRunBackgroundOperation(
            () => MonitorAdmissionAsync(matchingId, snapshot),
            $"matching-admission-watchdog:{matchingId}");
    }

    private async Task MonitorAdmissionAsync(long matchingId, IReadOnlyCollection<long> humanPlayerIds)
    {
        try
        {
            await Task.Delay(MatchingHandoffRedisKeys.AdmissionTimeout, shutdownToken);
        }
        catch (OperationCanceledException) when (shutdownToken.IsCancellationRequested)
        {
            return;
        }

        string stateKey = MatchingHandoffRedisKeys.AdmissionStateKey(matchingId);
        while (!shutdownToken.IsCancellationRequested)
        {
            try
            {
                bool canceled = await redisOperations.StringSetIfEqualsAsync(
                    stateKey,
                    MatchingHandoffRedisKeys.AdmissionPendingState,
                    MatchingHandoffRedisKeys.AdmissionCanceledState,
                    MatchingHandoffRedisKeys.HandoffStateLifetime);
                if (!canceled)
                {
                    var state = await redisOperations.StringGetAsync(stateKey);
                    if (!state.IsNullOrEmpty &&
                        string.Equals(state.ToString(), MatchingHandoffRedisKeys.AdmissionCompletedState,
                            StringComparison.Ordinal))
                        return;
                    if (state.IsNullOrEmpty ||
                        !string.Equals(state.ToString(), MatchingHandoffRedisKeys.AdmissionCanceledState,
                            StringComparison.Ordinal))
                    {
                        throw new InvalidOperationException(
                            $"Admission state for match {matchingId} is ambiguous: '{state}'.");
                    }
                }

                logger.LogWarning(
                    "Matching admission timed out; rolling back the whole human roster: MatchingId={MatchingId}, Players={PlayerCount}",
                    matchingId,
                    humanPlayerIds.Count);
                await DeleteHandoffBestEffortAsync(matchingId);
                foreach (long playerId in humanPlayerIds)
                {
                    try
                    {
                        await NotifyAdmissionFailedAsync(playerId, matchingId);
                    }
                    catch (Exception ex)
                    {
                        logger.LogWarning(
                            ex,
                            "Matching admission timeout notification failed: PlayerId={PlayerId}, MatchingId={MatchingId}",
                            playerId,
                            matchingId);
                    }

                    sessions.ClearMatchingAssignment(playerId, matchingId);
                    await claims.ReleaseActiveBestEffortAsync(playerId, matchingId);
                }
                return;
            }
            catch (Exception ex)
            {
                // 읽기 실패는 모호하다: Game Server가 admission을 commit했을 수 있다. Redis가 불안정한 동안
                // 거짓 rollback을 내지 않도록 재시도한다.
                logger.LogWarning(
                    ex,
                    "Could not verify matching admission timeout; retrying: MatchingId={MatchingId}",
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
    public async Task NotifyBatchFailedAsync(IEnumerable<MatchingQueueEntry> players, long matchingId)
    {
        try
        {
            foreach (MatchingQueueEntry player in players.DistinctBy(entry => entry.PlayerId))
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
    public async Task NotifyAdmissionFailedAsync(long playerId, long matchingId)
    {
        if (!await sessions.DeliverAdmissionFailedAsync(playerId, matchingId, ErrorCode.MATCHING_FAILED))
        {
            logger.LogWarning(
                "Matching admission failure was rejected or found no session: PlayerId={PlayerId}, MatchingId={MatchingId}",
                playerId,
                matchingId);
        }
    }
}
