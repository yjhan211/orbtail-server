using System.Globalization;
using Microsoft.Extensions.Logging;
using network.common;
using network.common.data.models;
using network.contracts.authentication;
using network.interfaces;

namespace user_server.services;

/// <summary>
///     Orchestrates matching claim acquisition, matching-id commit, exact rollback,
///     cancellation fencing, and lifecycle release while delegating the queue-entry Lua transition.
/// </summary>
internal sealed class MatchingQueueClaimCoordinator(
    ICacheHelper cacheHelper,
    IMatchingQueueClaimStore claimStore,
    ILogger logger)
{
    private static readonly TimeSpan ReservationLifetime = TimeSpan.FromMinutes(2);
    private static readonly TimeSpan ActiveClaimLifetime = MatchingHandoffRedisKeys.AdmissionClaimLifetime;

    public async Task<bool> HasClaimAsync(long playerId)
    {
        var claim = await cacheHelper.StringGetAsync(ClaimKey(playerId));
        return !claim.IsNullOrEmpty;
    }

    public async Task<MatchingClaimLease?> TryAcquireAsync(IEnumerable<MatchingQueueEntry> entries)
    {
        var claimedEntries = entries
            .Where(entry => entry.IsHuman)
            .DistinctBy(entry => entry.PlayerId)
            .ToList();
        string claimId = Guid.NewGuid().ToString("N");
        var acquiredPlayerIds = new List<long>(claimedEntries.Count);

        try
        {
            foreach (var claimedEntry in claimedEntries)
            {
                bool acquired = await claimStore.TryClaimQueueEntryAsync(
                    claimedEntry.Raw,
                    claimedEntry.PlayerId,
                    claimId,
                    ReservationLifetime);
                if (!acquired)
                {
                    await RollbackAsync(new MatchingClaimLease(claimId, acquiredPlayerIds));
                    return null;
                }

                acquiredPlayerIds.Add(claimedEntry.PlayerId);
            }

            return new MatchingClaimLease(claimId, acquiredPlayerIds);
        }
        catch
        {
            await RollbackAsync(new MatchingClaimLease(claimId, acquiredPlayerIds));
            throw;
        }
    }

    public async Task<MatchingClaimLease?> TryAcquireCancellationAsync(long playerId)
    {
        string claimId = "cancel_" + Guid.NewGuid().ToString("N");
        bool acquired = await cacheHelper.StringSetIfNotExistsAsync(
            ClaimKey(playerId),
            claimId,
            ReservationLifetime);
        return acquired
            ? new MatchingClaimLease(claimId, [playerId])
            : null;
    }

    public Task ReleaseCancellationAsync(MatchingClaimLease claimLease)
    {
        long playerId = claimLease.PlayerIds.Single();
        return cacheHelper.StringDeleteIfEqualsAsync(ClaimKey(playerId), claimLease.ClaimId);
    }

    public async Task CommitAsync(MatchingClaimLease claimLease, long matchingId)
    {
        string matchingIdValue = matchingId.ToString(CultureInfo.InvariantCulture);
        claimLease.MatchingId = matchingId;

        foreach (long playerId in claimLease.PlayerIds)
        {
            bool committed = await cacheHelper.StringSetIfEqualsAsync(
                ClaimKey(playerId),
                claimLease.ClaimId,
                matchingIdValue,
                ActiveClaimLifetime);
            if (!committed)
            {
                throw new InvalidOperationException(
                    $"Matching claim ownership changed before commit for player {playerId}.");
            }
        }
    }

    public async Task RollbackAsync(MatchingClaimLease claimLease)
    {
        foreach (long playerId in claimLease.PlayerIds)
        {
            try
            {
                await cacheHelper.StringDeleteIfEqualsAsync(
                    ClaimKey(playerId),
                    claimLease.ClaimId);
                if (claimLease.MatchingId.HasValue)
                {
                    await cacheHelper.StringDeleteIfEqualsAsync(
                        ClaimKey(playerId),
                        claimLease.MatchingId.Value.ToString(CultureInfo.InvariantCulture));
                }
            }
            catch (Exception ex)
            {
                logger.LogWarning(
                    ex,
                    "Matching claim rollback failed; TTL will release it: PlayerId={PlayerId}",
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
                await cacheHelper.StringDeleteIfEqualsAsync(
                    ClaimKey(playerId),
                    matchingId.ToString(CultureInfo.InvariantCulture));
            }
            else
            {
                // Rolling compatibility for the previous 8-byte lifecycle payload.
                await cacheHelper.KeyDeleteAsync(ClaimKey(playerId));
            }
        }
        catch (Exception ex)
        {
            logger.LogWarning(
                ex,
                "Failed to release matching claim; TTL remains as fallback: PlayerId={PlayerId}, MatchingId={MatchingId}",
                playerId,
                matchingId);
        }
    }

    private static string ClaimKey(long playerId)
    {
        return MatchingHandoffRedisKeys.ClaimKey(playerId);
    }
}

/// <summary>
///     Tracks the exact claim token and players owned by one queue pass until it is committed
///     to a matching id or rolled back.
/// </summary>
internal sealed class MatchingClaimLease(string claimId, List<long> playerIds)
{
    public string ClaimId { get; } = claimId;
    public List<long> PlayerIds { get; } = playerIds;
    public long? MatchingId { get; set; }
}
