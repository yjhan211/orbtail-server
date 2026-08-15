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
    public void Resolve_PutsWinnerFirstEvenWhenStatisticsSortLower()
    {
        var result = GameResultRankingResolver.Resolve(new[]
        {
            CreatePlayer(10, survival: 99, kills: 1, damage: 100, recovery: 10),
            CreatePlayer(20, survival: 100, kills: 3, damage: 300, recovery: 30)
        }, winnerId: 10);

        Assert.Equal(new long[] { 10, 20 }, result.Select(player => player.PlayerId));
        Assert.Equal(new[] { 1, 2 }, result.Select(player => player.Rank));
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

    [Fact]
    public void Resolve_UsesAuthoritativeEliminationRankBeforeStatistics()
    {
        var result = GameResultRankingResolver.Resolve(new[]
        {
            CreatePlayer(10, survival: 200, kills: 9, damage: 900, recovery: 900, rank: 2),
            CreatePlayer(20, survival: 1, kills: 0, damage: 0, recovery: 0, rank: 0)
        });

        Assert.Equal(new long[] { 20, 10 }, result.Select(player => player.PlayerId));
        Assert.Equal(new[] { 1, 2 }, result.Select(player => player.Rank));
    }

    private static GameResultPlayerInfo CreatePlayer(
        long playerId,
        int survival,
        int kills,
        int damage,
        int recovery,
        int rank = 0)
    {
        return new GameResultPlayerInfo
        {
            PlayerId = playerId,
            SurvivalTimeSeconds = survival,
            KillCount = kills,
            TotalDamageDealt = damage,
            TotalRecovery = recovery,
            Rank = rank
        };
    }

    [Fact]
    public void OrbCount_BreaksTiesAmongPlayersWithTheSameRank()
    {
        // #229: 인게임 순위가 오브 수로 매겨지는데 결과표는 처치·피해·회복만 봤다.
        // 스웜에서 그 셋은 상시 0이라 동순위가 PlayerId 순으로 잘렸다.
        var result = GameResultRankingResolver.Resolve(new[]
        {
            new GameResultPlayerInfo { PlayerId = 1, Rank = 0, OrbCount = 3, SurvivalTimeSeconds = 200 },
            new GameResultPlayerInfo { PlayerId = 2, Rank = 0, OrbCount = 8, SurvivalTimeSeconds = 200 },
            new GameResultPlayerInfo { PlayerId = 3, Rank = 0, OrbCount = 5, SurvivalTimeSeconds = 200 }
        });

        Assert.Equal(new long[] { 2, 3, 1 }, result.Select(player => player.PlayerId).ToArray());
        Assert.Equal(new[] { 1, 2, 3 }, result.Select(player => player.Rank).ToArray());
    }
}
