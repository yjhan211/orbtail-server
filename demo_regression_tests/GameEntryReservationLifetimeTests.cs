using game_server.network;
using Microsoft.Extensions.Logging.Abstractions;
using network.common;

namespace demo_regression_tests;

public sealed class GameEntryReservationLifetimeTests
{
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
            MatchingRedisKeys.HandoffStateLifetime);
        await redis.StringSetAsync(
            reservationKey,
            matchingId,
            MatchingRedisKeys.EntryReservationLifetime);
        var committer = new GameEntryStateCommitter(redis, NullLogger.Instance);

        await committer.CommitAsync(matchingId, playerId, [playerId]);

        TimeSpan expectedReservationLifetime =
            TimeSpan.FromSeconds(Config.SWARM_MATCH_DURATION_SECONDS) + TimeSpan.FromMinutes(3);
        Assert.Equal(expectedReservationLifetime, MatchingRedisKeys.PostEntryReservationLifetime);
        Assert.Equal(expectedReservationLifetime, redis.Expiries[reservationKey]);
        Assert.Equal(MatchingRedisKeys.HandoffStateLifetime, redis.Expiries[handoffKey]);
        Assert.Equal(MatchingRedisKeys.HandoffStateLifetime, redis.Expiries[entryStateKey]);
    }
}
