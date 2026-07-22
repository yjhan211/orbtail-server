using game_server.services;
using network.common.data.models;

namespace demo_regression_tests;

public sealed class GameResultRankingResolverTests
{
    [Fact]
    public void Resolve_OrdersBySurvivalKillsDamageAndRecovery()
    {
        var result = GameResultRankingResolver.Resolve(new[]
        {
            CreatePlayer(1, survival: 100, kills: 9, damage: 900, recovery: 900),
            CreatePlayer(2, survival: 200, kills: 1, damage: 100, recovery: 100),
            CreatePlayer(3, survival: 200, kills: 2, damage: 100, recovery: 100),
            CreatePlayer(4, survival: 200, kills: 2, damage: 200, recovery: 100),
            CreatePlayer(5, survival: 200, kills: 2, damage: 200, recovery: 200)
        });

        Assert.Equal(new long[] { 5, 4, 3, 2, 1 }, result.Select(player => player.PlayerId));
        Assert.Equal(new[] { 1, 2, 3, 4, 5 }, result.Select(player => player.Rank));
    }

    [Fact]
    public void Resolve_UsesPlayerIdAsStableFinalTieBreaker()
    {
        var result = GameResultRankingResolver.Resolve(new[]
        {
            CreatePlayer(20, survival: 100, kills: 1, damage: 10, recovery: 5),
            CreatePlayer(10, survival: 100, kills: 1, damage: 10, recovery: 5)
        });

        Assert.Equal(new long[] { 10, 20 }, result.Select(player => player.PlayerId));
    }

    private static GameResultPlayerInfo CreatePlayer(
        long playerId,
        int survival,
        int kills,
        int damage,
        int recovery)
    {
        return new GameResultPlayerInfo
        {
            PlayerId = playerId,
            SurvivalTimeSeconds = survival,
            KillCount = kills,
            TotalDamageDealt = damage,
            TotalRecovery = recovery
        };
    }
}
