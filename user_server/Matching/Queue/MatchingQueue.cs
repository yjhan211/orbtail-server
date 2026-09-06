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
    MatchingReservationCoordinator reservations,
    ILogger logger)
{
    internal const string QueueKey = MatchingHandoffRedisKeys.MatchingQueueKey;
    internal const string RequestsKey = MatchingHandoffRedisKeys.MatchingRequestsKey;
    private const string LockKeyPrefix = "matching_queue_lock:";

    public async Task<ErrorCode> AddToQueueAsync(long playerId, PlayerSession user)
    {
        try
        {
            await using var queueLock = await redLock.AcquireLockAsync(MakeLockKey(playerId), Config.LOCK_TTL);
            if (await reservations.HasReservationAsync(playerId))
            {
                return ErrorCode.MATCHING_ALREADY_IN_QUEUE;
            }

            int removedCount = await RemovePlayerEntriesAsync(playerId);
            if (removedCount > 0)
            {
                logger.LogInformation("Player {PlayerId}: removed {Count} stale matching entries", playerId, removedCount);
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
            var entry = new MatchingQueueData
            {
                PlayerId = playerId,
                RequestTime = matchingNow.UtcDateTime,
                RequestId = requestId!
            };

            bool added = await redisOperations.SortedSetAddWithHashAsync(
                QueueKey, RequestsKey, entry.RequestId, MessagePackSerializer.Serialize(entry), matchingNow.ToUnixTimeSeconds());
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
                int removedCount = await RemovePlayerEntriesAsync(playerId);
                logger.LogInformation("Matching cancelled: PlayerId={PlayerId}, RemovedEntries={RemovedEntries}", playerId, removedCount);
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

    public async Task<int> RemovePlayerEntriesAsync(long playerId)
    {
        byte[][] allEntries = await redisOperations.SortedSetRangeByScoreAsync(QueueKey);
        var details = await ReadDetailsAsync(allEntries);
        int removedCount = 0;

        for (int i = 0; i < allEntries.Length; i++)
        {
            var entry = ReadEntry(allEntries[i], details[i]);
            if (entry != null && entry.PlayerId != playerId) continue;
            if (await redisOperations.SortedSetRemoveWithHashAsync(QueueKey, RequestsKey, allEntries[i]))
            {
                removedCount++;
            }
        }

        return removedCount;
    }

    public async Task<MatchingQueueData[]> ReadWaitingEntriesAsync(long cutoffUnixSeconds)
    {
        byte[][] rawEntries = await redisOperations.SortedSetRangeByScoreAsync(
            QueueKey,
            double.NegativeInfinity,
            cutoffUnixSeconds);
        if (rawEntries.Length == 0) return [];

        return await CleanUpEntriesAsync(rawEntries);
    }

    internal async Task<MatchingQueueData[]> CleanUpEntriesAsync(IEnumerable<byte[]> rawEntries)
    {
        byte[][] requestIds = rawEntries.ToArray();
        var details = await ReadDetailsAsync(requestIds);
        var validEntries = new List<MatchingQueueData>();
        var seenPlayerIds = new HashSet<long>();

        for (int i = 0; i < requestIds.Length; i++)
        {
            var entry = ReadEntry(requestIds[i], details[i]);
            if (entry != null && seenPlayerIds.Add(entry.PlayerId))
            {
                validEntries.Add(entry);
                continue;
            }
            if (entry != null)
                logger.LogWarning("Removed duplicate matching entry: PlayerId={PlayerId}", entry.PlayerId);
            await redisOperations.SortedSetRemoveWithHashAsync(QueueKey, RequestsKey, requestIds[i]);
        }

        return validEntries.ToArray();
    }

    public Task<bool> RemoveEntryAsync(MatchingQueueData entry)
    {
        return redisOperations.SortedSetRemoveWithHashAsync(QueueKey, RequestsKey, Encoding.UTF8.GetBytes(entry.RequestId));
    }

    private Task<RedisValue[]> ReadDetailsAsync(byte[][] requestIds)
    {
        return requestIds.Length == 0
            ? Task.FromResult(Array.Empty<RedisValue>())
            : redisOperations.HashGetAsync(RequestsKey, requestIds.Select(id => (RedisValue)id).ToArray());
    }

    private MatchingQueueData? ReadEntry(byte[] member, RedisValue detail)
    {
        string requestId = Encoding.UTF8.GetString(member);
        if (!string.IsNullOrWhiteSpace(requestId) && !detail.IsNullOrEmpty)
        {
            try
            {
                var entry = MatchingQueueData.Parse((byte[])detail!);
                if (entry.PlayerId > 0 && string.Equals(entry.RequestId, requestId, StringComparison.Ordinal))
                    return entry;
            }
            catch (MessagePackSerializationException ex)
            {
                logger.LogWarning(ex, "Malformed matching request details: RequestId={RequestId}", requestId);
            }
        }

        logger.LogWarning("Invalid matching entry removed: request ID or details missing/invalid");
        return null;
    }

    public static MatchingQueueData[] SortByRequestTime(IEnumerable<MatchingQueueData> entries)
    {
        return entries
            .OrderBy(entry => entry.RequestTime)
            .ThenBy(entry => entry.PlayerId)
            .ToArray();
    }

    internal static string MakeLockKey(long playerId)
    {
        return LockKeyPrefix + playerId;
    }
}
