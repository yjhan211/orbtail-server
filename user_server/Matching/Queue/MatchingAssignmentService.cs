using System.Globalization;
using Microsoft.Extensions.Logging;
using network.common;
using network.infrastructure.redis;

namespace user_server.matching.queue;

/// <summary>
///     플레이어를 매치에 중복 배정하지 않도록 Redis에 매치 번호를 기록한다.
///     요청이 아직 대기열에 있고 기존 배정이 없을 때만 배정하며, 일부 참가자의 배정에 실패하면 앞서 배정한 참가자도 해제한다.
///     취소는 같은 키를 선점해 매치 생성과 동시에 진행되지 않도록 한다.
///     해제 시 매치 번호 또는 취소 작업 ID를 비교해 다른 작업의 값을 지우지 않는다.
///     정리에 실패한 값은 TTL로 만료된다.
/// </summary>
internal sealed class MatchingAssignmentService(IRedisOperations redisOperations, ILogger<MatchingAssignmentService> logger)
{
    private static readonly TimeSpan ReservationLifetime = MatchingRedisKeys.EntryReservationLifetime;
    private static string ReservationKey(long playerId) => MatchingRedisKeys.ReservationKey(playerId);

    public async Task<bool> IsMatchingBlockedAsync(long playerId)
    {
        var reservation = await redisOperations.StringGetAsync(ReservationKey(playerId));
        return !reservation.IsNullOrEmpty;
    }

    public async Task<bool> TryAssignAsync(IEnumerable<MatchingQueueData> requests, long matchingId)
    {
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(matchingId);
        var participantRequests = requests
            .Where(request => request.PlayerId > 0)
            .DistinctBy(request => request.PlayerId)
            .ToList();
        string assignmentValue = matchingId.ToString(CultureInfo.InvariantCulture);
        var assignedPlayerIds = new List<long>(participantRequests.Count);

        try
        {
            foreach (var request in participantRequests)
            {
                bool acquired = await redisOperations.StringSetIfQueueEntryExistsAsync(
                    MatchingRedisKeys.MatchingQueueKey,
                    MatchingRedisKeys.MatchingRequestsKey,
                    request.RequestId,
                    ReservationKey(request.PlayerId),
                    assignmentValue,
                    ReservationLifetime);

                if (!acquired)
                {
                    await ReleaseAssignmentsAsync(assignedPlayerIds, matchingId);
                    return false;
                }

                assignedPlayerIds.Add(request.PlayerId);
            }

            return true;
        }
        catch
        {
            await ReleaseAssignmentsAsync(assignedPlayerIds, matchingId);
            throw;
        }
    }

    public async Task<string?> TryAcquireCancellationAsync(long playerId)
    {
        string cancellationId = "cancel_" + Guid.NewGuid().ToString("N");
        bool acquired = await redisOperations.StringSetIfNotExistsAsync(
            ReservationKey(playerId),
            cancellationId,
            ReservationLifetime);
        return acquired
            ? cancellationId
            : null;
    }

    public Task ReleaseCancellationAsync(long playerId, string cancellationId)
    {
        return redisOperations.StringDeleteIfEqualsAsync(ReservationKey(playerId), cancellationId);
    }

    public async Task ReleaseAssignmentsAsync(IEnumerable<long> playerIds, long matchingId)
    {
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(matchingId);
        string assignmentValue = matchingId.ToString(CultureInfo.InvariantCulture);
        foreach (long playerId in playerIds)
        {
            try
            {
                await redisOperations.StringDeleteIfEqualsAsync(
                    ReservationKey(playerId),
                    assignmentValue);
            }
            catch (Exception ex)
            {
                logger.LogWarning(
                    ex,
                    "Matching assignment release failed; TTL will release it: PlayerId={PlayerId}",
                    playerId);
            }
        }
    }

    public async Task ReleaseAssignmentAsync(long playerId, long matchingId)
    {
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(matchingId);
        try
        {
            await redisOperations.StringDeleteIfEqualsAsync(
                ReservationKey(playerId),
                matchingId.ToString(CultureInfo.InvariantCulture));
        }
        catch (Exception ex)
        {
            logger.LogWarning(
                ex,
                "Failed to release matching assignment; TTL remains as fallback: PlayerId={PlayerId}, MatchingId={MatchingId}",
                playerId,
                matchingId);
        }
    }
}
