using game_server.services;
using network.common;

namespace demo_regression_tests;

public sealed class MatchStartGateTests
{
    [Fact]
    public void SoloHumanMatchRemainsBlockedUntilHumanIsReady()
    {
        long matchingId = DateTime.UtcNow.Ticks;
        const long playerId = 1;

        try
        {
            // 정원 - 1 = 봇 충원 수 (#223 10인 전환과 함께 움직인다).
            MatchStartGate.RegisterHumanPlayer(matchingId, playerId,
                botCount: Config.SWARM_PLAYERS_PER_MATCH - 1);

            Assert.False(MatchStartGate.IsGameplayActive(matchingId));
            Assert.Equal(-1, MatchStartGate.GetSnapshot(matchingId).RemainingSeconds);

            MatchStartGate.MarkHumanReady(matchingId, playerId);

            var snapshot = MatchStartGate.GetSnapshot(matchingId);
            Assert.InRange(snapshot.RemainingSeconds, 1, 5);
            Assert.False(MatchStartGate.IsGameplayActive(matchingId));
        }
        finally
        {
            MatchStartGate.RemoveMatching(matchingId);
        }
    }

    [Fact]
    public void UnknownMatchIsNotActive()
    {
        // 등록 누락이 "게이트 없이 진행"으로 새지 않는다 (#335).
        long matchingId = DateTime.UtcNow.Ticks + 1;

        Assert.False(MatchStartGate.IsGameplayActive(matchingId));
        Assert.False(MatchStartGate.GetSnapshot(matchingId).IsKnown);
        Assert.Null(MatchStartGate.GetGameplayStartedAtUtc(matchingId));
    }

    [Fact]
    public void BotOnlyMatchIsActiveImmediatelyWithoutCountdown()
    {
        long matchingId = DateTime.UtcNow.Ticks + 2;

        try
        {
            MatchStartGate.RegisterBotOnlyMatch(matchingId);

            Assert.True(MatchStartGate.IsGameplayActive(matchingId));
            Assert.False(MatchStartGate.GetSnapshot(matchingId).IsKnown);
            Assert.False(MatchStartGate.IsAdmissionTimedOut(matchingId, DateTime.UtcNow.AddMinutes(5)));
            Assert.NotNull(MatchStartGate.GetGameplayStartedAtUtc(matchingId));

            MatchStartGate.RemoveMatching(matchingId);
            Assert.False(MatchStartGate.IsGameplayActive(matchingId));
        }
        finally
        {
            MatchStartGate.RemoveMatching(matchingId);
        }
    }
}
