using game_server.matches;
using network.common.data.models;

namespace demo_regression_tests;

public sealed class MatchResultRankingTests
{
    [Fact]
    public void AssignRankings_OrdersByOrbCount()
    {
        var result = MatchResultService.AssignRankings(new[]
        {
            CreatePlayer(1, orbCount: 1),
            CreatePlayer(2, orbCount: 2),
            CreatePlayer(3, orbCount: 3),
            CreatePlayer(4, orbCount: 4),
            CreatePlayer(5, orbCount: 5)
        });

        Assert.Equal(new long[] { 5, 4, 3, 2, 1 }, result.Select(player => player.PlayerId));
        Assert.Equal(new[] { 1, 2, 3, 4, 5 }, result.Select(player => player.Rank));
    }

    [Fact]
    public void AssignRankings_PutsWinnerFirstEvenWhenOrbCountIsLower()
    {
        var result = MatchResultService.AssignRankings(new[]
        {
            CreatePlayer(10, orbCount: 1),
            CreatePlayer(20, orbCount: 5)
        }, winnerId: 10);

        Assert.Equal(new long[] { 10, 20 }, result.Select(player => player.PlayerId));
        Assert.Equal(new[] { 1, 2 }, result.Select(player => player.Rank));
    }

    [Fact]
    public void AssignRankings_UsesPlayerIdAsStableFinalTieBreaker()
    {
        var result = MatchResultService.AssignRankings(new[]
        {
            CreatePlayer(20, orbCount: 5),
            CreatePlayer(10, orbCount: 5)
        });

        Assert.Equal(new long[] { 10, 20 }, result.Select(player => player.PlayerId));
    }

    [Fact]
    public void AssignRankings_UsesAuthoritativeEliminationRankBeforeOrbCount()
    {
        var result = MatchResultService.AssignRankings(new[]
        {
            CreatePlayer(10, orbCount: 5, rank: 2),
            CreatePlayer(20, orbCount: 5, rank: 0)
        });

        Assert.Equal(new long[] { 20, 10 }, result.Select(player => player.PlayerId));
        Assert.Equal(new[] { 1, 2 }, result.Select(player => player.Rank));
    }

    private static GameResultPlayerInfo CreatePlayer(
        long playerId,
        int orbCount,
        int rank = 0)
    {
        return new GameResultPlayerInfo
        {
            PlayerId = playerId,
            OrbCount = orbCount,
            Rank = rank
        };
    }

    [Fact]
    public void OrbCount_BreaksTiesAmongPlayersWithTheSameRank()
    {
        // #229: 인게임 순위가 오브 수로 매겨지는데 결과표는 처치·피해·회복만 봤다.
        // 스웜에서 그 셋은 상시 0이라 동순위가 PlayerId 순으로 잘렸다.
        var result = MatchResultService.AssignRankings(new[]
        {
            new GameResultPlayerInfo { PlayerId = 1, Rank = 0, OrbCount = 3 },
            new GameResultPlayerInfo { PlayerId = 2, Rank = 0, OrbCount = 8 },
            new GameResultPlayerInfo { PlayerId = 3, Rank = 0, OrbCount = 5 }
        });

        Assert.Equal(new long[] { 2, 3, 1 }, result.Select(player => player.PlayerId).ToArray());
        Assert.Equal(new[] { 1, 2, 3 }, result.Select(player => player.Rank).ToArray());
    }
}
