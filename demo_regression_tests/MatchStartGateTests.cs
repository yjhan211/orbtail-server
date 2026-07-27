using game_server.services;

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
            MatchStartGate.RegisterHumanPlayer(matchingId, playerId, botCount: 7);

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
    public void MultiplayerMatchWaitsForEveryExpectedHuman()
    {
        long matchingId = DateTime.UtcNow.Ticks + 1;

        try
        {
            MatchStartGate.RegisterHumanPlayer(matchingId, playerId: 1, botCount: 6);
            MatchStartGate.RegisterHumanPlayer(matchingId, playerId: 2, botCount: 6);

            MatchStartGate.MarkHumanReady(matchingId, playerId: 1);
            Assert.Equal(-1, MatchStartGate.GetSnapshot(matchingId).RemainingSeconds);

            MatchStartGate.MarkHumanReady(matchingId, playerId: 2);
            Assert.InRange(MatchStartGate.GetSnapshot(matchingId).RemainingSeconds, 1, 5);
        }
        finally
        {
            MatchStartGate.RemoveMatching(matchingId);
        }
    }
}
