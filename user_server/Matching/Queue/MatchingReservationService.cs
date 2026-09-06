using System.Globalization;
using Microsoft.Extensions.Logging;
using network.common;
using network.infrastructure.redis;

namespace user_server.matching.queue;

/// <summary>
///     플레이어가 다른 매치에 중복 배정되지 않도록 Redis에 예약을 남긴다.
///     매치 생성 시 대기 중인 요청을 임시로 확보하고, 매치 번호가 정해지면 예약 값으로 기록한다.
///     취소도 같은 예약 키를 사용하므로 매치 생성과 취소 중 먼저 확보한 작업만 진행할 수 있다.
///     실패하거나 매치가 끝나면 해당 작업의 예약을 해제하고, 정리에 실패하면 TTL로 만료된다.
/// </summary>
internal sealed class MatchingReservationService(IRedisOperations redisOperations, ILogger logger)
{
    private static readonly TimeSpan ReservationLifetime = TimeSpan.FromMinutes(2);
    private static readonly TimeSpan ActiveReservationLifetime = MatchingRedisKeys.AdmissionReservationLifetime;
    private static string ReservationKey(long playerId) => MatchingRedisKeys.ReservationKey(playerId);

    public async Task<bool> HasReservationAsync(long playerId)
    {
        var reservation = await redisOperations.StringGetAsync(ReservationKey(playerId));
        return !reservation.IsNullOrEmpty;
    }

    public async Task<MatchingReservationLease?> TryAcquireAsync(IEnumerable<MatchingQueueData> requests)
    {
        var reservedRequests = requests
            .Where(request => request.PlayerId > 0)
            .DistinctBy(request => request.PlayerId)
            .ToList();
        string reservationId = Guid.NewGuid().ToString("N");
        var acquiredPlayerIds = new List<long>(reservedRequests.Count);

        try
        {
            foreach (var reservedRequest in reservedRequests)
            {
                bool acquired = await redisOperations.StringSetIfQueueEntryExistsAsync(
                    MatchingRedisKeys.MatchingQueueKey,
                    MatchingRedisKeys.MatchingRequestsKey,
                    reservedRequest.RequestId,
                    ReservationKey(reservedRequest.PlayerId),
                    reservationId,
                    ReservationLifetime);

                if (!acquired)
                {
                    await RollbackAsync(new MatchingReservationLease(reservationId, acquiredPlayerIds));
                    return null;
                }

                acquiredPlayerIds.Add(reservedRequest.PlayerId);
            }

            return new MatchingReservationLease(reservationId, acquiredPlayerIds);
        }
        catch
        {
            await RollbackAsync(new MatchingReservationLease(reservationId, acquiredPlayerIds));
            throw;
        }
    }

    public async Task<MatchingReservationLease?> TryAcquireCancellationAsync(long playerId)
    {
        string reservationId = "cancel_" + Guid.NewGuid().ToString("N");
        bool acquired = await redisOperations.StringSetIfNotExistsAsync(
            ReservationKey(playerId),
            reservationId,
            ReservationLifetime);
        return acquired
            ? new MatchingReservationLease(reservationId, [playerId])
            : null;
    }

    public Task ReleaseCancellationAsync(MatchingReservationLease reservationLease)
    {
        long playerId = reservationLease.PlayerIds.Single();
        return redisOperations.StringDeleteIfEqualsAsync(ReservationKey(playerId), reservationLease.ReservationId);
    }

    public async Task CommitAsync(MatchingReservationLease reservationLease, long matchingId)
    {
        string matchingIdValue = matchingId.ToString(CultureInfo.InvariantCulture);
        reservationLease.MatchingId = matchingId;

        foreach (long playerId in reservationLease.PlayerIds)
        {
            bool committed = await redisOperations.StringSetIfEqualsAsync(
                ReservationKey(playerId),
                reservationLease.ReservationId,
                matchingIdValue,
                ActiveReservationLifetime);
            if (!committed)
            {
                throw new InvalidOperationException(
                    $"Matching reservation ownership changed before commit for player {playerId}.");
            }
        }
    }

    public async Task RollbackAsync(MatchingReservationLease reservationLease)
    {
        foreach (long playerId in reservationLease.PlayerIds)
        {
            try
            {
                await redisOperations.StringDeleteIfEqualsAsync(
                    ReservationKey(playerId),
                    reservationLease.ReservationId);
                if (reservationLease.MatchingId.HasValue)
                {
                    await redisOperations.StringDeleteIfEqualsAsync(
                        ReservationKey(playerId),
                        reservationLease.MatchingId.Value.ToString(CultureInfo.InvariantCulture));
                }
            }
            catch (Exception ex)
            {
                logger.LogWarning(
                    ex,
                    "Matching reservation rollback failed; TTL will release it: PlayerId={PlayerId}",
                    playerId);
            }
        }
    }

    public async Task ReleaseMatchingReservationAsync(long playerId, long matchingId)
    {
        try
        {
            if (matchingId > 0)
            {
                await redisOperations.StringDeleteIfEqualsAsync(
                    ReservationKey(playerId),
                    matchingId.ToString(CultureInfo.InvariantCulture));
            }
            else
            {
                await redisOperations.KeyDeleteAsync(ReservationKey(playerId));
            }
        }
        catch (Exception ex)
        {
            logger.LogWarning(
                ex,
                "Failed to release matching reservation; TTL remains as fallback: PlayerId={PlayerId}, MatchingId={MatchingId}",
                playerId,
                matchingId);
        }
    }
}
