using game_server.matches;
using game_server.players.bots;
using game_server.logging;
using game_server.orbs;
using network.common;

namespace game_server.players;

/// <summary>봇이 사람과 같은 비용·효과로 오브를 소환하거나 태양·바람·파도 계열을 강화한다. 호출자는 매치 잠금을 보유한다.</summary>
internal sealed class GrowthService(
    MatchRuntimeStore matchRuntimes,
    GameEventLogManager eventLogs,
    OrbUpgradeService orbUpgrades)
{
    private readonly OrbInventoryService _orbInventory = new(eventLogs);

    public int GetNextGrowthCost(long matchingId, long playerId)
    {
        var match = matchRuntimes.GetOrThrow(matchingId);
        int cost = match.Inventory.GetOrbScore(playerId).OrbCount < Config.SWARM_ORB_CAPACITY
            ? match.SummonStones.GetSnapshot(playerId).NextCost : int.MaxValue;
        var upgrades = orbUpgrades.GetOrbUpgradeInfo(matchingId, playerId);
        foreach (int upgradeCost in new[] { upgrades.SunCost, upgrades.WindCost, upgrades.WaveCost })
            if (upgradeCost > 0) cost = Math.Min(cost, upgradeCost);
        return cost;
    }

    public void ProcessBotGrowth(long matchingId, IReadOnlyList<BotPlayerState> aliveBots)
    {
        var match = matchRuntimes.GetOrThrow(matchingId);
        foreach (var bot in aliveBots)
        {
            if (bot.Player.IsEliminated) continue;
            if (match.SummonStones.GetSnapshot(bot.PlayerId).StoneCount < GetNextGrowthCost(matchingId, bot.PlayerId)) continue;
            int orbCount = match.Inventory.GetOrbScore(bot.PlayerId).OrbCount;
            bool preferUpgrade = orbCount >= Config.SWARM_ORB_CAPACITY ||
                                 (orbCount >= 4 && Random.Shared.Next(3) == 0);
            if (preferUpgrade && orbUpgrades.TryUpgradeForBot(matchingId, bot.PlayerId)) continue;

            if (orbCount < Config.SWARM_ORB_CAPACITY &&
                _orbInventory.Summon(match, bot.PlayerId, bot.Player.CurrentArea).Success) continue;

            orbUpgrades.TryUpgradeForBot(matchingId, bot.PlayerId);
        }
    }
    /// <summary>생존자 최다 오브 수 — 봇 성장·추격 판단의 순위 기준.</summary>
    public int GetTopOrbCount(long matchingId)
    {
        var match = matchRuntimes.GetOrThrow(matchingId);
        int top = 0;
        foreach (var player in match.GetAlivePlayers())
            top = Math.Max(top, match.Inventory.GetOrbScore(player.PlayerId).OrbCount);
        return top;
    }

}
