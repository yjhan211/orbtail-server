using Microsoft.Extensions.Logging;
using network.common;
using network.infrastructure.redis;
using user_server.sessions;

namespace user_server.matching.queue;

/// <summary>
///     Redis에 저장된 매칭 대기열의 등록·취소·조회를 담당한다.
///     등록과 취소는 플레이어별 락과 reservation 확인을 통해 매치 생성 작업과 충돌하지 않도록 처리한다.
///     조회 시 손상되거나 중복된 항목을 정리하고, 지정한 시각까지 등록된 대기자를 반환한다.
///     실제 참가자 구성과 매치 생성은 MatchCreationService가 담당한다.
/// </summary>
internal sealed class MatchingQueue(
    IRedisOperations redisOperations,
    IRedLockFactory redLock,
    MatchingReservationCoordinator reservations,
    ILogger logger)
{
    internal const string QueueKey = MatchingHandoffRedisKeys.MatchingQueueKey;
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
            if (!MatchingRequestTokens.IsSafeTokenComponent(requestId))
            {
                logger.LogWarning("Matching queue rejected because the session has no active request fence: PlayerId={PlayerId}", playerId);
                return ErrorCode.MATCHING_FAILED;
            }

            var matchingNow = DateTimeOffset.UtcNow;
            var entry = MatchingQueueEntry.FromData(new MatchingQueueData
            {
                PlayerId = playerId,
                RequestTime = matchingNow.UtcDateTime,
                RequestId = requestId!
            });

            await redisOperations.SortedSetAddAsync(QueueKey, entry.Raw, matchingNow.ToUnixTimeSeconds());
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
            MatchingReservationLease? cancellationReservation = await reservations.TryAcquireCancellationAsync(playerId);
            if (cancellationReservation == null)
                return ErrorCode.MATCHING_FAILED;

            try
            {
                int removedCount = await RemovePlayerEntriesAsync(playerId);
                logger.LogInformation(
                    "Matching cancelled: PlayerId={PlayerId}, RemovedEntries={RemovedEntries}",
                    playerId,
                    removedCount);
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

    /// <summary>
    ///     해당 player의 entry를 모두 제거한다. 역직렬화 실패 entry도 함께 제거한다.
    /// </summary>
    public async Task<int> RemovePlayerEntriesAsync(long playerId)
    {
        byte[][] allEntries = await redisOperations.SortedSetRangeByScoreAsync(QueueKey);
        int removedCount = 0;

        foreach (byte[] raw in allEntries)
        {
            try
            {
                if (MatchingQueueEntry.Parse(raw).PlayerId != playerId) continue;

                if (await redisOperations.SortedSetRemoveAsync(QueueKey, raw))
                    removedCount++;
            }
            catch (Exception ex)
            {
                logger.LogError(ex, "Invalid matching entry removed while cleaning the queue");
                if (await redisOperations.SortedSetRemoveAsync(QueueKey, raw))
                    removedCount++;
            }
        }

        return removedCount;
    }

    /// <summary>
    ///     score가 cutoff 이하(충분히 기다린) entry를 읽고 sanitize한 결과를 돌려준다. 정렬은 하지 않는다.
    /// </summary>
    public async Task<MatchingQueueEntry[]> ReadWaitingEntriesAsync(long cutoffUnixSeconds)
    {
        byte[][] rawEntries = await redisOperations.SortedSetRangeByScoreAsync(
            QueueKey,
            double.NegativeInfinity,
            cutoffUnixSeconds);
        if (rawEntries.Length == 0) return Array.Empty<MatchingQueueEntry>();

        return await SanitizeAsync(rawEntries);
    }

    /// <summary>
    ///     손상·PlayerId 0 이하·중복 PlayerId entry를 큐에서 제거하고 나머지를 한 번만 역직렬화해 돌려준다.
    /// </summary>
    internal async Task<MatchingQueueEntry[]> SanitizeAsync(IEnumerable<byte[]> rawEntries)
    {
        var validEntries = new List<MatchingQueueEntry>();
        var seenPlayerIds = new HashSet<long>();

        foreach (byte[] raw in rawEntries)
        {
            bool removeEntry;
            try
            {
                MatchingQueueEntry entry = MatchingQueueEntry.Parse(raw);
                removeEntry = entry.PlayerId <= 0 || !seenPlayerIds.Add(entry.PlayerId);
                if (!removeEntry)
                {
                    validEntries.Add(entry);
                    continue;
                }

                logger.LogWarning("Removed duplicate or invalid matching entry: PlayerId={PlayerId}", entry.PlayerId);
            }
            catch (Exception ex)
            {
                removeEntry = true;
                logger.LogWarning(ex, "Removed malformed matching queue entry");
            }

            if (removeEntry)
                await redisOperations.SortedSetRemoveAsync(QueueKey, raw);
        }

        return validEntries.ToArray();
    }

    public Task<bool> RemoveEntryAsync(MatchingQueueEntry entry)
    {
        return redisOperations.SortedSetRemoveAsync(QueueKey, entry.Raw);
    }

    /// <summary>
    ///     요청 시각 → PlayerId 순으로 정렬해 안정적인 매칭 순서를 만든다.
    /// </summary>
    public static MatchingQueueEntry[] SortByRequestTime(IEnumerable<MatchingQueueEntry> entries)
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
