using network.common.data.models;

namespace game_server.services;

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
