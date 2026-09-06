using Microsoft.Extensions.Logging;
using network.common;
using network.infrastructure.redis;
using StackExchange.Redis;

namespace game_server.network;

/// <summary>
///     Commits a consumed GameServer handoff into Redis admission state.
///     This class owns the retry and exact read-back rules that make ambiguous Redis write responses safe.
/// </summary>
internal sealed class GameAdmissionStateCommitter(IRedisOperations redisOperations, ILogger logger)
{
    private const int ConfirmationAttempts = 3;
    private static readonly TimeSpan ConfirmationRetryDelay = TimeSpan.FromMilliseconds(50);

    /// <summary>
    ///     Renews the player's exact matching reservation, records the player admission marker, and completes
    ///     the match admission only after every expected human marker is visible.
    /// </summary>
    public async Task CommitAsync(
        long matchingId,
        long playerId,
        IReadOnlyCollection<long> expectedHumanPlayerIds)
    {
        string admissionStateKey = MatchingRedisKeys.AdmissionStateKey(matchingId);
        RedisValue admissionState = await redisOperations.StringGetAsync(admissionStateKey);
        if (admissionState.IsNullOrEmpty ||
            !string.Equals(admissionState.ToString(), MatchingRedisKeys.AdmissionPendingState,
                StringComparison.Ordinal))
        {
            throw new InvalidOperationException(
                $"Admission is not pending for match {matchingId}: '{admissionState}'.");
        }

        await RenewMatchingReservationAsync(matchingId, playerId);

        string handoffKey = MatchingRedisKeys.Key(matchingId);
        string admittedField = MatchingRedisKeys.AdmittedPlayerField(playerId);
        await WriteMarkerWithReadBackAsync(
            handoffKey,
            admittedField,
            MatchingRedisKeys.AdmissionReadyValue,
            $"player {playerId} admission for match {matchingId}");

        RedisValue[] admittedFields = expectedHumanPlayerIds
            .Select(MatchingRedisKeys.AdmittedPlayerField)
            .Select(field => (RedisValue)field)
            .ToArray();
        RedisValue[] admittedValues = await redisOperations.HashGetAsync(handoffKey, admittedFields);
        bool allHumansAdmitted = admittedValues.Length == admittedFields.Length &&
                                 admittedValues.All(value =>
                                     !value.IsNullOrEmpty &&
                                     ((byte[])value!).AsSpan().SequenceEqual(
                                         [MatchingRedisKeys.AdmissionReadyValue]));
        if (allHumansAdmitted)
            await CompleteAdmissionStateAsync(admissionStateKey, matchingId);
    }

    private async Task RenewMatchingReservationAsync(long matchingId, long playerId)
    {
        string expectedReservation = matchingId.ToString(System.Globalization.CultureInfo.InvariantCulture);
        Exception? reservationError = null;
        bool reservationRenewed = false;
        for (int attempt = 0; attempt < ConfirmationAttempts; attempt++)
        {
            try
            {
                reservationRenewed = await redisOperations.StringSetIfEqualsAsync(
                    MatchingRedisKeys.ReservationKey(playerId),
                    expectedReservation,
                    expectedReservation,
                    MatchingRedisKeys.PostAdmissionReservationLifetime);
            }
            catch (Exception ex)
            {
                reservationError = ex;
                continue;
            }

            if (!reservationRenewed)
            {
                throw new InvalidOperationException(
                    $"Matching reservation changed before admission for player {playerId} in match {matchingId}.");
            }

            break;
        }

        if (reservationRenewed)
            return;

        RedisValue reservation = await redisOperations.StringGetAsync(MatchingRedisKeys.ReservationKey(playerId));
        if (reservation.IsNullOrEmpty || !string.Equals(reservation.ToString(), expectedReservation, StringComparison.Ordinal))
        {
            throw new InvalidOperationException(
                $"Could not renew the matching reservation for player {playerId} in match {matchingId}.",
                reservationError);
        }

        logger.LogWarning(
            reservationError,
            "Matching reservation renewal response was lost; exact reservation read-back confirmed: PlayerId={PlayerId}, MatchingId={MatchingId}",
            playerId,
            matchingId);
    }

    private async Task CompleteAdmissionStateAsync(string admissionStateKey, long matchingId)
    {
        Exception? lastError = null;
        for (int attempt = 0; attempt < ConfirmationAttempts; attempt++)
        {
            bool canceledStateObserved = false;
            try
            {
                bool completed = await redisOperations.StringSetIfEqualsAsync(
                    admissionStateKey,
                    MatchingRedisKeys.AdmissionPendingState,
                    MatchingRedisKeys.AdmissionCompletedState,
                    MatchingRedisKeys.HandoffStateLifetime);
                if (completed)
                    return;
            }
            catch (Exception ex)
            {
                lastError = ex;
            }

            try
            {
                RedisValue state = await redisOperations.StringGetAsync(admissionStateKey);
                if (!state.IsNullOrEmpty &&
                    string.Equals(state.ToString(), MatchingRedisKeys.AdmissionCompletedState,
                        StringComparison.Ordinal))
                {
                    if (lastError != null)
                    {
                        logger.LogWarning(
                            lastError,
                            "Admission completion response was lost; terminal state read-back confirmed: MatchingId={MatchingId}",
                            matchingId);
                    }

                    return;
                }

                if (!state.IsNullOrEmpty &&
                    string.Equals(state.ToString(), MatchingRedisKeys.AdmissionCanceledState,
                        StringComparison.Ordinal))
                {
                    canceledStateObserved = true;
                }
            }
            catch (Exception ex)
            {
                lastError = ex;
                logger.LogWarning(
                    ex,
                    "Admission terminal-state read-back failed: MatchingId={MatchingId}, Attempt={Attempt}",
                    matchingId,
                    attempt + 1);
            }

            if (canceledStateObserved)
            {
                throw new InvalidOperationException(
                    $"Admission timeout canceled match {matchingId} before completion.");
            }

            await Task.Delay(ConfirmationRetryDelay);
        }

        throw new InvalidOperationException(
            $"Could not confirm admission completion for match {matchingId}.",
            lastError);
    }

    private async Task WriteMarkerWithReadBackAsync(
        string key,
        string field,
        byte value,
        string description)
    {
        Exception? lastError = null;
        for (int attempt = 0; attempt < ConfirmationAttempts; attempt++)
        {
            try
            {
                await redisOperations.HashSetWithExpiryAsync(
                    key,
                    field,
                    [value],
                    MatchingRedisKeys.HandoffStateLifetime);
                return;
            }
            catch (Exception ex)
            {
                lastError = ex;
                try
                {
                    RedisValue marker = await redisOperations.HashGetAsync(key, field);
                    if (!marker.IsNullOrEmpty && ((byte[])marker!).AsSpan().SequenceEqual([value]))
                    {
                        logger.LogWarning(
                            ex,
                            "Redis write response was lost; read-back confirmed {Description}",
                            description);
                        return;
                    }
                }
                catch (Exception readBackError)
                {
                    logger.LogWarning(
                        readBackError,
                        "Redis marker read-back failed for {Description}: Attempt={Attempt}",
                        description,
                        attempt + 1);
                }
            }
        }

        throw new InvalidOperationException($"Could not confirm {description}.", lastError);
    }
}
