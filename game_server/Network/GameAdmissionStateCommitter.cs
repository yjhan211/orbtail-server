using Microsoft.Extensions.Logging;
using network.common;
using network.interfaces;
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
    ///     Renews the player's exact matching claim, records the player admission marker, and completes
    ///     the match admission only after every expected human marker is visible.
    /// </summary>
    public async Task CommitAsync(
        long matchingId,
        long playerId,
        IReadOnlyCollection<long> expectedHumanPlayerIds)
    {
        string admissionStateKey = MatchingHandoffRedisKeys.AdmissionStateKey(matchingId);
        RedisValue admissionState = await redisOperations.StringGetAsync(admissionStateKey);
        if (admissionState.IsNullOrEmpty ||
            !string.Equals(admissionState.ToString(), MatchingHandoffRedisKeys.AdmissionPendingState,
                StringComparison.Ordinal))
        {
            throw new InvalidOperationException(
                $"Admission is not pending for match {matchingId}: '{admissionState}'.");
        }

        await RenewMatchingClaimAsync(matchingId, playerId);

        string handoffKey = MatchingHandoffRedisKeys.Key(matchingId);
        string admittedField = MatchingHandoffRedisKeys.AdmittedPlayerField(playerId);
        await WriteMarkerWithReadBackAsync(
            handoffKey,
            admittedField,
            MatchingHandoffRedisKeys.AdmissionReadyValue,
            $"player {playerId} admission for match {matchingId}");

        RedisValue[] admittedFields = expectedHumanPlayerIds
            .Select(MatchingHandoffRedisKeys.AdmittedPlayerField)
            .Select(field => (RedisValue)field)
            .ToArray();
        RedisValue[] admittedValues = await redisOperations.HashGetAsync(handoffKey, admittedFields);
        bool allHumansAdmitted = admittedValues.Length == admittedFields.Length &&
                                 admittedValues.All(value =>
                                     !value.IsNullOrEmpty &&
                                     ((byte[])value!).AsSpan().SequenceEqual(
                                         [MatchingHandoffRedisKeys.AdmissionReadyValue]));
        if (allHumansAdmitted)
            await CompleteAdmissionStateAsync(admissionStateKey, matchingId);
    }

    private async Task RenewMatchingClaimAsync(long matchingId, long playerId)
    {
        string expectedClaim = matchingId.ToString(System.Globalization.CultureInfo.InvariantCulture);
        Exception? claimError = null;
        bool claimRenewed = false;
        for (int attempt = 0; attempt < ConfirmationAttempts; attempt++)
        {
            try
            {
                claimRenewed = await redisOperations.StringSetIfEqualsAsync(
                    MatchingHandoffRedisKeys.ClaimKey(playerId),
                    expectedClaim,
                    expectedClaim,
                    MatchingHandoffRedisKeys.PostAdmissionClaimLifetime);
            }
            catch (Exception ex)
            {
                claimError = ex;
                continue;
            }

            if (!claimRenewed)
            {
                throw new InvalidOperationException(
                    $"Matching claim changed before admission for player {playerId} in match {matchingId}.");
            }

            break;
        }

        if (claimRenewed)
            return;

        RedisValue claim = await redisOperations.StringGetAsync(MatchingHandoffRedisKeys.ClaimKey(playerId));
        if (claim.IsNullOrEmpty || !string.Equals(claim.ToString(), expectedClaim, StringComparison.Ordinal))
        {
            throw new InvalidOperationException(
                $"Could not renew the matching claim for player {playerId} in match {matchingId}.",
                claimError);
        }

        logger.LogWarning(
            claimError,
            "Matching claim renewal response was lost; exact claim read-back confirmed: PlayerId={PlayerId}, MatchingId={MatchingId}",
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
                    MatchingHandoffRedisKeys.AdmissionPendingState,
                    MatchingHandoffRedisKeys.AdmissionCompletedState,
                    MatchingHandoffRedisKeys.HandoffStateLifetime);
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
                    string.Equals(state.ToString(), MatchingHandoffRedisKeys.AdmissionCompletedState,
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
                    string.Equals(state.ToString(), MatchingHandoffRedisKeys.AdmissionCanceledState,
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
                    MatchingHandoffRedisKeys.HandoffStateLifetime);
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
