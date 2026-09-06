using Microsoft.Extensions.Logging;
using System.Text;
using MessagePack;
using StackExchange.Redis;
using network.common;
using network.infrastructure.redis;
using user_server.sessions;

namespace user_server.matching.queue;

/// <summary>
///     Redis에 저장된 매칭 대기열의 등록·취소·조회를 담당한다.
///     등록과 취소는 플레이어별 락과 reservation 확인을 통해 매치 생성 작업과 충돌하지 않도록 처리한다.
///     조회 시 손상되거나 중복된 항목을 정리하고, 지정한 시각까지 등록된 대기자를 반환한다.
/// </summary>
internal sealed class MatchingQueue(
    IRedisOperations redisOperations,
    IRedLockFactory redLock,
    MatchingReservationService reservations,
    ILogger<MatchingQueue> logger)
{
    internal const string QueueKey = MatchingRedisKeys.MatchingQueueKey;
    internal const string RequestsKey = MatchingRedisKeys.MatchingRequestsKey;
    internal static string MakeLockKey(long playerId) => MatchingRedisKeys.QueueLockKey(playerId);

    public async Task<ErrorCode> AddToQueueAsync(long playerId, PlayerSession user)
    {
        try
        {
            await using var queueLock = await redLock.AcquireLockAsync(MakeLockKey(playerId), Config.LOCK_TTL);
            if (await reservations.HasReservationAsync(playerId))
            {
                return ErrorCode.MATCHING_ALREADY_IN_QUEUE;
            }

            int removedCount = await RemovePlayerRequestsAsync(playerId);
            if (removedCount > 0)
            {
                logger.LogInformation("Player {PlayerId}: removed {Count} stale matching requests", playerId, removedCount);
            }

            // 위에서 제거한 snapshot을 다른 worker가 이미 reservation했을 수 있다.
            if (await reservations.HasReservationAsync(playerId))
            {
                return ErrorCode.MATCHING_ALREADY_IN_QUEUE;
            }

            string? requestId = user.ActiveMatchingRequestId;
            if (string.IsNullOrWhiteSpace(requestId))
            {
                logger.LogWarning("Matching queue rejected because the session has no active request fence: PlayerId={PlayerId}", playerId);
                return ErrorCode.MATCHING_FAILED;
            }

            var matchingNow = DateTimeOffset.UtcNow;
            var request = new MatchingQueueData
            {
                PlayerId = playerId,
                RequestTime = matchingNow.UtcDateTime,
                RequestId = requestId
            };

            bool added = await redisOperations.SortedSetAddWithHashAsync(
                QueueKey, RequestsKey, request.RequestId, MessagePackSerializer.Serialize(request), matchingNow.ToUnixTimeSeconds());
            if (!added)
            {
                return ErrorCode.MATCHING_ALREADY_IN_QUEUE;
            }
            logger.LogInformation("Player {PlayerId} queued for matching", playerId);

            return ErrorCode.SUCCESS;
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "Failed to queue player for matching: PlayerId={PlayerId}", playerId);
            return ErrorCode.SERVER_INTERNAL_ERROR;
        }
    }

    public async Task<ErrorCode> CancelMatchingAsync(long playerId)
    {
        try
        {
            await using var queueLock = await redLock.AcquireLockAsync(MakeLockKey(playerId), Config.LOCK_TTL);
            var cancellationReservation = await reservations.TryAcquireCancellationAsync(playerId);
            if (cancellationReservation == null)
            {
                return ErrorCode.MATCHING_FAILED;
            }

            try
            {
                int removedCount = await RemovePlayerRequestsAsync(playerId);
                logger.LogInformation("Matching cancelled: PlayerId={PlayerId}, RemovedRequests={RemovedRequests}", playerId, removedCount);
                return ErrorCode.SUCCESS;
            }
            finally
            {
                await reservations.ReleaseCancellationAsync(cancellationReservation);
            }
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "Failed to cancel matching: PlayerId={PlayerId}", playerId);
            return ErrorCode.SERVER_INTERNAL_ERROR;
        }
    }

    public async Task<int> RemovePlayerRequestsAsync(long playerId)
    {
        byte[][] requestIds = await redisOperations.SortedSetRangeByScoreAsync(QueueKey);
        var details = await ReadRequestDetailsAsync(requestIds);
        int removedCount = 0;

        for (int i = 0; i < requestIds.Length; i++)
        {
            var request = ReadRequest(requestIds[i], details[i]);
            if (request != null && request.PlayerId != playerId) continue;
            if (await redisOperations.SortedSetRemoveWithHashAsync(QueueKey, RequestsKey, requestIds[i]))
            {
                removedCount++;
            }
        }

        return removedCount;
    }

    public async Task<MatchingQueueData[]> ReadWaitingRequestsAsync(long cutoffUnixSeconds)
    {
        byte[][] rawRequestIds = await redisOperations.SortedSetRangeByScoreAsync(
            QueueKey,
            double.NegativeInfinity,
            cutoffUnixSeconds);
        if (rawRequestIds.Length == 0) return [];

        return await CleanUpRequestsAsync(rawRequestIds);
    }

    internal async Task<MatchingQueueData[]> CleanUpRequestsAsync(IEnumerable<byte[]> rawRequestIds)
    {
        byte[][] requestIds = rawRequestIds.ToArray();
        var details = await ReadRequestDetailsAsync(requestIds);
        var validRequests = new List<MatchingQueueData>();
        var seenPlayerIds = new HashSet<long>();

        for (int i = 0; i < requestIds.Length; i++)
        {
            var request = ReadRequest(requestIds[i], details[i]);
            if (request != null && seenPlayerIds.Add(request.PlayerId))
            {
                validRequests.Add(request);
                continue;
            }

            if (request != null)
            {
                logger.LogWarning("Removed duplicate matching request: PlayerId={PlayerId}", request.PlayerId);
            }
            await redisOperations.SortedSetRemoveWithHashAsync(QueueKey, RequestsKey, requestIds[i]);
        }

        return validRequests.ToArray();
    }

    public Task<bool> RemoveRequestAsync(MatchingQueueData request)
    {
        return redisOperations.SortedSetRemoveWithHashAsync(QueueKey, RequestsKey, Encoding.UTF8.GetBytes(request.RequestId));
    }

    private Task<RedisValue[]> ReadRequestDetailsAsync(byte[][] requestIds)
    {
        return requestIds.Length == 0
            ? Task.FromResult(Array.Empty<RedisValue>())
            : redisOperations.HashGetAsync(RequestsKey, requestIds.Select(id => (RedisValue)id).ToArray());
    }

    private MatchingQueueData? ReadRequest(byte[] member, RedisValue detail)
    {
        string requestId = Encoding.UTF8.GetString(member);
        if (!string.IsNullOrWhiteSpace(requestId) && !detail.IsNullOrEmpty)
        {
            try
            {
                var request = MatchingQueueData.Parse((byte[])detail!);
                if (request.PlayerId > 0 && string.Equals(request.RequestId, requestId, StringComparison.Ordinal))
                    return request;
            }
            catch (MessagePackSerializationException ex)
            {
                logger.LogWarning(ex, "Malformed matching request details: RequestId={RequestId}", requestId);
            }
        }

        logger.LogWarning("Invalid matching request removed: request ID or details missing/invalid");
        return null;
    }

    public static MatchingQueueData[] SortByRequestTime(IEnumerable<MatchingQueueData> requests)
    {
        return requests
            .OrderBy(request => request.RequestTime)
            .ThenBy(request => request.PlayerId)
            .ToArray();
    }
}
