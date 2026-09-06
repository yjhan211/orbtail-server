using System.Globalization;
using Microsoft.Extensions.Logging;
using network.common;
using network.infrastructure.redis;

namespace user_server.matching.queue;

/// <summary>
///     Orchestrates matching reservation acquisition, matching-id commit, exact rollback,
///     cancellation fencing, and lifecycle release while delegating the queue-entry Lua transition.
/// </summary>
internal sealed class MatchingReservationCoordinator(
    IRedisOperations redisOperations,
    ILogger logger)
{
    private static readonly TimeSpan ReservationLifetime = TimeSpan.FromMinutes(2);
    private static readonly TimeSpan ActiveReservationLifetime = MatchingHandoffRedisKeys.AdmissionReservationLifetime;

    public async Task<bool> HasReservationAsync(long playerId)
    {
        var reservation = await redisOperations.StringGetAsync(ReservationKey(playerId));
        return !reservation.IsNullOrEmpty;
    }

    public async Task<MatchingReservationLease?> TryAcquireAsync(IEnumerable<MatchingQueueData> entries)
    {
        var reservedEntries = entries
            .Where(entry => entry.PlayerId > 0)
            .DistinctBy(entry => entry.PlayerId)
            .ToList();
        string reservationId = Guid.NewGuid().ToString("N");
        var acquiredPlayerIds = new List<long>(reservedEntries.Count);

        try
        {
            foreach (var reservedEntry in reservedEntries)
            {
                bool acquired = await redisOperations.StringSetIfQueueEntryExistsAsync(
                    MatchingHandoffRedisKeys.MatchingQueueKey,
                    MatchingHandoffRedisKeys.MatchingRequestsKey,
                    reservedEntry.RequestId,
                    ReservationKey(reservedEntry.PlayerId),
                    reservationId,
                    ReservationLifetime);
                if (!acquired)
                {
                    await RollbackAsync(new MatchingReservationLease(reservationId, acquiredPlayerIds));
                    return null;
                }

                acquiredPlayerIds.Add(reservedEntry.PlayerId);
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

    public async Task ReleaseActiveBestEffortAsync(long playerId, long matchingId)
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
                // Rolling compatibility for the previous 8-byte lifecycle payload.
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

    private static string ReservationKey(long playerId)
    {
        return MatchingHandoffRedisKeys.ReservationKey(playerId);
    }
}

/// <summary>
///     Tracks the exact reservation token and players owned by one queue pass until it is committed
///     to a matching id or rolled back.
/// </summary>
internal sealed class MatchingReservationLease(string reservationId, List<long> playerIds)
{
    public string ReservationId { get; } = reservationId;
    public List<long> PlayerIds { get; } = playerIds;
    public long? MatchingId { get; set; }
}
