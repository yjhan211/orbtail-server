using MessagePack;
using Microsoft.Extensions.Logging;
using network.common;
using network.common.data.models;
using network.gameentry;
using network.infrastructure.redis;
using StackExchange.Redis;

namespace game_server.matches;

/// <summary>
///     GameServer 입장 준비와 입장 기록을 담당한다.
///     manifest·입장 준비·참가자 배정을 검증하고, Store에서 초기화된 매치를 가져온다.
///     참가자가 입장하면 Redis의 매칭 예약을 연장하고 입장 여부를 기록하며,
///     모든 사람이 입장하면 매치 입장 상태를 completed로 변경한다.
/// </summary>
internal sealed class GameMatchEntryService(
    IRedisOperations redisOperations,
    MatchRuntimeStore matchRuntimes,
    ILogger logger,
    GameEntryTicketService ticketService,
    GameServerNodeOptions nodeOptions)
{
    public Task<GameEntryContext?> ConsumeTicketAsync(string? ticket) => ticketService.ConsumeAsync(ticket, nodeOptions.NodeId);

    public async Task<MatchRuntime> PrepareMatchAsync(long matchingId)
    {
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(matchingId);
        try
        {
            string matchingKey = MatchingRedisKeys.Key(matchingId);
            var serialized = await redisOperations.HashGetAsync(matchingKey, MatchingRedisKeys.ManifestField);
            if (serialized.IsNullOrEmpty)
            {
                throw new InvalidOperationException($"Missing match manifest for match {matchingId}.");
            }

            var manifest = MessagePackSerializer.Deserialize<MatchManifest>((byte[])serialized!);
            if (manifest == null)
            {
                throw new InvalidOperationException($"Match manifest is empty for match {matchingId}.");
            }

            int humanCount = manifest.HumanPlayerIds.Count;
            if (humanCount == 0 || manifest.BotCount < 0 || manifest.BotCount > Config.SWARM_PLAYERS_PER_MATCH - humanCount)
            {
                throw new InvalidOperationException("Invalid match participant count.");
            }

            var ready = await redisOperations.HashGetAsync(matchingKey, MatchingRedisKeys.EntryReadyField);
            if (ready.IsNullOrEmpty)
            {
                string entryStateKey = MatchingRedisKeys.EntryStateKey(matchingId);
                var entryState = await redisOperations.StringGetAsync(entryStateKey);
                if (string.Equals(entryState.ToString(), MatchingRedisKeys.EntryCanceledState, StringComparison.Ordinal))
                {
                    throw new InvalidOperationException($"Matching entry was canceled before the handoff became ready for match {matchingId}.");
                }
                throw new InvalidOperationException($"Matching handoff was not committed for match {matchingId}.");
            }

            byte[] value = (byte[])ready!;
            if (value is not [MatchingRedisKeys.EntryReadyValue])
            {
                throw new InvalidOperationException($"Invalid entry marker for match {matchingId}.");
            }

            string expectedReservation = matchingId.ToString(System.Globalization.CultureInfo.InvariantCulture);
            foreach (long humanPlayerId in manifest.HumanPlayerIds)
            {
                string reservationKey = MatchingRedisKeys.ReservationKey(humanPlayerId);
                var reservation = await redisOperations.StringGetAsync(reservationKey);
                if (reservation.IsNullOrEmpty || !string.Equals(reservation.ToString(), expectedReservation, StringComparison.Ordinal))
                {
                    throw new InvalidOperationException($"Matching reservation is not active for player {humanPlayerId} in match {matchingId}.");
                }
            }

            return await matchRuntimes.GetOrCreateAsync(matchingId, manifest);
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "Failed to load match setup: MatchingId={MatchingId}", matchingId);
            throw;
        }
    }

    public async Task RecordEntryAsync(long matchingId, long playerId, IReadOnlyCollection<long> expectedHumanPlayerIds)
    {
        const string pendingState = MatchingRedisKeys.EntryPendingState;
        const string completedState = MatchingRedisKeys.EntryCompletedState;
        const string canceledState = MatchingRedisKeys.EntryCanceledState;
        const byte enteredValue = MatchingRedisKeys.EntryReadyValue;
        var entryStateLifetime = MatchingRedisKeys.EntryStateLifetime;

        string entryStateKey = MatchingRedisKeys.EntryStateKey(matchingId);
        var entryState = await redisOperations.StringGetAsync(entryStateKey);
        if (entryState.IsNullOrEmpty || !string.Equals(entryState.ToString(), pendingState, StringComparison.Ordinal))
        {
            throw new InvalidOperationException($"Entry is not pending for match {matchingId}: '{entryState}'.");
        }

        string reservationKey = MatchingRedisKeys.ReservationKey(playerId);
        string expectedReservation = matchingId.ToString(System.Globalization.CultureInfo.InvariantCulture);
        bool reservationRenewed = await redisOperations.StringSetIfEqualsAsync(
            reservationKey,
            expectedReservation,
            expectedReservation,
            MatchingRedisKeys.PostEntryReservationLifetime);

        if (!reservationRenewed)
        {
            throw new InvalidOperationException($"Matching reservation changed before entry for player {playerId} in match {matchingId}.");
        }

        string matchingKey = MatchingRedisKeys.Key(matchingId);
        string enteredField = MatchingRedisKeys.EnteredPlayerField(playerId);
        try
        {
            await redisOperations.HashSetWithExpiryAsync(matchingKey, enteredField, [enteredValue], entryStateLifetime);
        }
        catch (Exception ex)
        {
            var marker = await redisOperations.HashGetAsync(matchingKey, enteredField);
            if (marker.IsNullOrEmpty || !((byte[])marker!).AsSpan().SequenceEqual([enteredValue]))
            {
                throw new InvalidOperationException($"Could not confirm player {playerId} entry for match {matchingId}.", ex);
            }
            logger.LogWarning(ex, "Player entry write failed, but entry record was confirmed: PlayerId={PlayerId}, MatchingId={MatchingId}", playerId, matchingId);
        }

        var enteredFields = expectedHumanPlayerIds
            .Select(id => (RedisValue)MatchingRedisKeys.EnteredPlayerField(id))
            .ToArray();

        var enteredValues = await redisOperations.HashGetAsync(matchingKey, enteredFields);
        if (enteredValues.Length != enteredFields.Length)
        {
            return;
        }
        foreach (var playerEntry in enteredValues)
        {
            if (playerEntry.IsNullOrEmpty)
            {
                return;
            }
            byte[] value = (byte[])playerEntry!;
            if (value is not [enteredValue])
            {
                return;
            }
        }

        Exception? lastError = null;
        try
        {
            bool completed = await redisOperations.StringSetIfEqualsAsync(entryStateKey, pendingState, completedState, entryStateLifetime);
            if (completed)
            {
                return;
            }
        }
        catch (Exception ex)
        {
            lastError = ex;
        }

        var state = await redisOperations.StringGetAsync(entryStateKey);
        if (string.Equals(state.ToString(), completedState, StringComparison.Ordinal))
        {
            if (lastError != null)
            {
                logger.LogWarning(lastError, "Entry completion write failed, but completed state was confirmed: MatchingId={MatchingId}", matchingId);
            }
            return;
        }

        if (string.Equals(state.ToString(), canceledState, StringComparison.Ordinal))
        {
            throw new InvalidOperationException($"Entry timeout canceled match {matchingId} before completion.");
        }
        throw new InvalidOperationException($"Could not confirm entry completion for match {matchingId}.", lastError);
    }
}
