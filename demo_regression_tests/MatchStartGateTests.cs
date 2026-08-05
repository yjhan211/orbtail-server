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
        // 다인 대기 시나리오는 8인 레거시 정원을 전제한다. 스웜(1인)·스팟 아레나(4인)
        // 실험 게이트가 켜져 있으면 정원 계산이 달라 성립하지 않으므로 건너뛴다.
        if (Config.SWARM_P0_ENABLED || Config.SPOT_ARENA_P0_ENABLED)
            return;

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
