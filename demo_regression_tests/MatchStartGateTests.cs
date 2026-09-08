using game_server.matches;
using game_server.services;
using network.common;
using network.common.data.models;

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
    public void MatchModeIsOwnedByEachRegisteredMatch()
    {
        long normalMatchingId = DateTime.UtcNow.Ticks + 10;
        long soloMatchingId = normalMatchingId + 1;

        try
        {
            MatchStartGate.RegisterHumanPlayer(normalMatchingId, 1, 1, MatchMode.Normal);
            MatchStartGate.RegisterHumanPlayer(soloMatchingId, 2, 1, MatchMode.SoloMapValidation);

            Assert.False(MatchStartGate.IsSoloMapValidation(normalMatchingId));
            Assert.True(MatchStartGate.IsSoloMapValidation(soloMatchingId));
        }
        finally
        {
            MatchStartGate.RemoveMatching(normalMatchingId);
            MatchStartGate.RemoveMatching(soloMatchingId);
        }
    }

    [Fact]
    public void RegisterHumanPlayerRejectsChangingMatchConfiguration()
    {
        long matchingId = DateTime.UtcNow.Ticks + 20;

        try
        {
            MatchStartGate.RegisterHumanPlayer(matchingId, 1, 1, MatchMode.Normal);

            Assert.Throws<InvalidOperationException>(() =>
                MatchStartGate.RegisterHumanPlayer(matchingId, 2, 1, MatchMode.SoloMapValidation));
        }
        finally
        {
            MatchStartGate.RemoveMatching(matchingId);
        }
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
            Assert.False(MatchStartGate.IsEntryTimedOut(matchingId, DateTime.UtcNow.AddMinutes(5)));
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
