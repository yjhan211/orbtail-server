using network.common.data.models;

namespace game_server.matches.results;

public static class GameResultRankingResolver
{
    public static List<GameResultPlayerInfo> Resolve(IEnumerable<GameResultPlayerInfo> players, long winnerId = 0)
    {
        var ordered = players
            .Where(player => player != null && player.PlayerId != 0)
            .OrderByDescending(player => winnerId != 0 && player.PlayerId == winnerId)
            // Elimination rank is assigned at the server-authoritative death event.
            // A rank of zero means the player is still alive in an interim result packet.
            .ThenBy(player => player.Rank > 0 ? player.Rank : 0)
            // 오브 수가 승점이다 (#229): 인게임 순위표와 같은 눈금으로 동순위를 가른다.
            // 아래 세 지표(처치·피해·회복)는 스웜에서 상시 0이라 사실상 탈락 순서만 남아 있었다.
            .ThenByDescending(player => player.OrbCount)
            .ThenByDescending(player => player.SurvivalTimeSeconds)
            .ThenByDescending(player => player.KillCount)
            .ThenByDescending(player => player.TotalDamageDealt)
            .ThenByDescending(player => player.TotalRecovery)
            .ThenBy(player => player.PlayerId)
            .ToList();

        for (int i = 0; i < ordered.Count; i++)
            ordered[i].Rank = i + 1;

        return ordered;
    }
}
