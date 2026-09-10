using game_server.matches;
using game_server.matches.entry;
using Microsoft.Extensions.Logging.Abstractions;
using network.common;

namespace demo_regression_tests;

public sealed class GameEntryReservationLifetimeTests
{
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task RecordEntry_ConfirmsMarkerWithoutRetrying(bool writeApplied)
    {
        const long matchingId = 51_003;
        const long playerId = 1_001;
        var redis = new InMemoryRedisOperations();
        string stateKey = MatchingRedisKeys.EntryStateKey(matchingId);
        await redis.StringSetAsync(stateKey, MatchingRedisKeys.EntryPendingState);
        await redis.StringSetAsync(MatchingRedisKeys.ReservationKey(playerId), matchingId);
        redis.HashSetWithExpiryError = new TimeoutException("Entry write response lost.");
        redis.ApplyHashSetBeforeError = writeApplied;
        var recorder = TestGameSessionServices.CreateEntryService(redis, TestGameSessionServices.CreateMatchRuntimeStore(NullLogger.Instance), NullLogger.Instance);

        if (writeApplied)
        {
            await recorder.RecordEntryAsync(matchingId, playerId, [playerId]);
        }
        else
        {
            await Assert.ThrowsAsync<InvalidOperationException>(() =>
                recorder.RecordEntryAsync(matchingId, playerId, [playerId]));
        }

        Assert.Equal(1, redis.HashSetWithExpiryCalls);
        Assert.Equal(writeApplied ? 2 : 1, redis.StringSetIfEqualsCalls);
        Assert.Equal(writeApplied ? MatchingRedisKeys.EntryCompletedState : MatchingRedisKeys.EntryPendingState,
            (await redis.StringGetAsync(stateKey)).ToString());
    }

    [Fact]
    public async Task RecordEntry_StopsWhenReservationRenewalThrows()
    {
        const long matchingId = 51_002;
        const long playerId = 1_001;
        var redis = new InMemoryRedisOperations();
        string stateKey = MatchingRedisKeys.EntryStateKey(matchingId);
        await redis.StringSetAsync(stateKey, MatchingRedisKeys.EntryPendingState);
        await redis.StringSetAsync(MatchingRedisKeys.ReservationKey(playerId), matchingId);
        var failure = new TimeoutException("Reservation renewal failed.");
        redis.StringError = failure;
        var recorder = TestGameSessionServices.CreateEntryService(redis, TestGameSessionServices.CreateMatchRuntimeStore(NullLogger.Instance), NullLogger.Instance);

        var error = await Assert.ThrowsAsync<TimeoutException>(() =>
            recorder.RecordEntryAsync(matchingId, playerId, [playerId]));

        Assert.Same(failure, error);
        Assert.Equal(1, redis.StringSetIfEqualsCalls);
        Assert.Equal(1, redis.StringGetCalls);
        Assert.True((await redis.HashGetAsync(MatchingRedisKeys.Key(matchingId),
            MatchingRedisKeys.EnteredPlayerField(playerId))).IsNullOrEmpty);
        Assert.Equal(MatchingRedisKeys.EntryPendingState, (await redis.StringGetAsync(stateKey)).ToString());
    }

    [Fact]
    public void ReservationKey_UsesMatchingHashTagAndReservationPrefix()
    {
        Assert.Equal("{matching}:reservation:1001", MatchingRedisKeys.ReservationKey(1001));
    }

    [Fact]
    public async Task Commit_RenewsReservationForMatchDurationPlusGraceOnly()
    {
        const long matchingId = 51_001;
        const long playerId = 1_001;
        var redis = new InMemoryRedisOperations();
        string entryStateKey = MatchingRedisKeys.EntryStateKey(matchingId);
        string handoffKey = MatchingRedisKeys.Key(matchingId);
        string reservationKey = MatchingRedisKeys.ReservationKey(playerId);
        await redis.StringSetAsync(
            entryStateKey,
            MatchingRedisKeys.EntryPendingState,
            MatchingRedisKeys.EntryStateLifetime);
        await redis.StringSetAsync(
            reservationKey,
            matchingId,
            MatchingRedisKeys.EntryReservationLifetime);
        var recorder = TestGameSessionServices.CreateEntryService(redis, TestGameSessionServices.CreateMatchRuntimeStore(NullLogger.Instance), NullLogger.Instance);

        await recorder.RecordEntryAsync(matchingId, playerId, [playerId]);

        TimeSpan expectedReservationLifetime =
            TimeSpan.FromSeconds(Config.SWARM_MATCH_DURATION_SECONDS) + TimeSpan.FromMinutes(3);
        Assert.Equal(expectedReservationLifetime, MatchingRedisKeys.PostEntryReservationLifetime);
        Assert.Equal(expectedReservationLifetime, redis.Expiries[reservationKey]);
        Assert.Equal(MatchingRedisKeys.EntryStateLifetime, redis.Expiries[handoffKey]);
        Assert.Equal(MatchingRedisKeys.EntryStateLifetime, redis.Expiries[entryStateKey]);
    }
}
