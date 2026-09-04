using game_server.network;
using Microsoft.Extensions.Logging.Abstractions;
using network.common;

namespace demo_regression_tests;

public sealed class GameAdmissionClaimLifetimeTests
{
    [Fact]
    public async Task Commit_RenewsClaimForMatchDurationPlusGraceOnly()
    {
        const long matchingId = 51_001;
        const long playerId = 1_001;
        var redis = new InMemoryRedisOperations();
        string admissionStateKey = MatchingHandoffRedisKeys.AdmissionStateKey(matchingId);
        string handoffKey = MatchingHandoffRedisKeys.Key(matchingId);
        string claimKey = MatchingHandoffRedisKeys.ClaimKey(playerId);
        await redis.StringSetAsync(
            admissionStateKey,
            MatchingHandoffRedisKeys.AdmissionPendingState,
            MatchingHandoffRedisKeys.HandoffStateLifetime);
        await redis.StringSetAsync(
            claimKey,
            matchingId,
            MatchingHandoffRedisKeys.AdmissionClaimLifetime);
        var committer = new GameAdmissionStateCommitter(redis, NullLogger.Instance);

        await committer.CommitAsync(matchingId, playerId, [playerId]);

        TimeSpan expectedClaimLifetime =
            TimeSpan.FromSeconds(Config.SWARM_MATCH_DURATION_SECONDS) + TimeSpan.FromMinutes(3);
        Assert.Equal(expectedClaimLifetime, MatchingHandoffRedisKeys.PostAdmissionClaimLifetime);
        Assert.Equal(expectedClaimLifetime, redis.Expiries[claimKey]);
        Assert.Equal(MatchingHandoffRedisKeys.HandoffStateLifetime, redis.Expiries[handoffKey]);
        Assert.Equal(MatchingHandoffRedisKeys.HandoffStateLifetime, redis.Expiries[admissionStateKey]);
    }
}
