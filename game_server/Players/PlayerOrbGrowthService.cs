using game_server.items;
using game_server.matches;
using game_server.players.bots;
using game_server.logging;
using game_server.orbs;
using Microsoft.Extensions.Logging;
using network.common;
using network.common.data;
using network.common.data.models;

namespace game_server.players;

/// <summary>
///     플레이어와 봇의 오브 소환·강화 비용을 계산하고 성장 행동을 처리한다.
///     오브와 소환석 상태는 매치 런타임이 소유하며, 호출자는 해당 매치 잠금을 보유해야 한다.
/// </summary>
internal sealed class PlayerOrbGrowthService(
    MatchRuntimeStore matchRuntimes,
    GameEventLogManager eventLogs,
    ILogger<PlayerOrbGrowthService> logger)
{
    private static readonly OrbColor[] FamilyColors = [OrbColor.Red, OrbColor.Green, OrbColor.Blue];

    internal static void GrantStartingResources(MatchRuntime runtime, long playerId)
    {
        int startingStones = Config.SWARM_STARTING_STONE_GRANT;
        for (int index = 0; index < Config.SWARM_STARTING_ORB_GRANT_COUNT; index++)
        {
            startingStones += Math.Min(Config.SWARM_GROWTH_COST_CAP, Config.GetSwarmGrowthBaseCost(index));
        }
        runtime.SummonStones.AddStones(playerId, startingStones);
    }

    private int GetFamilyLevel(long matchingId, long playerId, OrbColor color)
    {
        int best = 1;
        foreach (var item in matchRuntimes.GetOrThrow(matchingId).Inventory.GetPlayerInventory(playerId).GetOrderedOrbs())
        {
            if (OrbData.TryGetColorAndTier(item.ItemId, out var itemColor, out int tier) && itemColor == color && tier > best)
            {
                best = tier;
            }
        }
        return best;
    }

    private int FindUpgradeTargetOrdinal(long matchingId, long playerId, OrbColor color, out InGameItemInfo? target)
    {
        var orbs = matchRuntimes.GetOrThrow(matchingId).Inventory.GetPlayerInventory(playerId).GetOrderedOrbs();
        for (int ordinal = 0; ordinal < orbs.Count; ordinal++)
        {
            if (!OrbData.TryGetColorAndTier(orbs[ordinal].ItemId, out var itemColor, out int tier) || itemColor != color || tier >= 3)
            {
                continue;
            }
            target = orbs[ordinal];
            return ordinal;
        }

        target = null;
        return -1;
    }

    private int GetUpgradeCost(long matchingId, long playerId, OrbColor color, OrbUpgradeState orbBoard)
    {
        if (FindUpgradeTargetOrdinal(matchingId, playerId, color, out _) < 0)
        {
            return 0;
        }
        int purchases = orbBoard.GetFamilyUpgradeCount(playerId, color);
        return Math.Min(Config.SWARM_GROWTH_COST_CAP, Config.GetSwarmGrowthBaseCost(purchases));
    }

    private bool TryUpgrade(long matchingId, long playerId, OrbColor color, out int resultItemId, out int targetOrdinal)
    {
        resultItemId = 0;
        targetOrdinal = -1;
        if (!FamilyColors.Contains(color))
        {
            return false;
        }

        int ordinal = FindUpgradeTargetOrdinal(matchingId, playerId, color, out var target);
        if (ordinal < 0 || target == null)
        {
            return false;
        }
        var orbBoard = matchRuntimes.GetOrThrow(matchingId).OrbUpgrades;
        int cost = GetUpgradeCost(matchingId, playerId, color, orbBoard);
        if (cost <= 0)
        {
            return false;
        }

        if (!OrbData.TryGetColorAndTier(target.ItemId, out _, out int tier) || !OrbData.TryGetItemId(color, tier + 1, out int upgradedItemId))
        {
            return false;
        }

        if (!matchRuntimes.GetOrThrow(matchingId).SummonStones.TrySpendStones(playerId, cost, out _))
        {
            return false;
        }

        orbBoard.IncrementFamilyUpgradeCount(playerId, color);
        var inventory = matchRuntimes.GetOrThrow(matchingId).Inventory.GetPlayerInventory(playerId);
        bool replaced = inventory.TryReplaceOrb(target.ItemUid, upgradedItemId, out _);
        resultItemId = upgradedItemId;
        targetOrdinal = ordinal;

        eventLogs.LogSystem(matchingId, $"ORB_UPGRADED player={playerId} bot={playerId < 0} family={color} ordinal={ordinal} " + $"uid={target.ItemUid} tier={tier}->{tier + 1} cost={cost} replaced={replaced}");
        return replaced;
    }

    public G_TO_C_ORB_UPGRADE_INFO GetOrbUpgradeInfo(long matchingId, long playerId)
    {
        var orbBoard = matchRuntimes.GetOrThrow(matchingId).OrbUpgrades;
        return new G_TO_C_ORB_UPGRADE_INFO
        {
            SunLevel = GetFamilyLevel(matchingId, playerId, OrbColor.Red),
            WindLevel = GetFamilyLevel(matchingId, playerId, OrbColor.Green),
            WaveLevel = GetFamilyLevel(matchingId, playerId, OrbColor.Blue),
            SunCost = GetUpgradeCost(matchingId, playerId, OrbColor.Red, orbBoard),
            WindCost = GetUpgradeCost(matchingId, playerId, OrbColor.Green, orbBoard),
            WaveCost = GetUpgradeCost(matchingId, playerId, OrbColor.Blue, orbBoard)
        };
    }

    public (bool Success, int ResultItemId, int TargetOrdinal) HandleUpgradeOrb(Player player, long matchingId, int action, long targetUid, long secondUid)
    {
        _ = secondUid;
        if (player.PlayerId == 0 || player.IsEliminated)
        {
            return (false, 0, -1);
        }

        long playerId = player.PlayerId;
        bool success = false;
        int resultItemId = 0;
        int targetOrdinal = -1;
        if (action == Config.ORB_UPGRADE_FAMILY)
        {
            success = TryUpgrade(matchingId, playerId, (OrbColor)targetUid, out resultItemId, out targetOrdinal);
        }

        logger.LogInformation("Orb upgrade: MatchingId={MatchingId}, PlayerId={PlayerId}, Action={Action}, Target={Target}, Success={Success}, Result={Result}, Ordinal={Ordinal}", matchingId, playerId, action, targetUid, success, resultItemId, targetOrdinal);
        return (success, resultItemId, targetOrdinal);
    }

    private bool TryUpgradeForBot(long matchingId, long playerId)
    {
        var favorite = matchRuntimes.GetOrThrow(matchingId).Inventory.GetPlayerInventory(playerId).GetOrderedOrbs()
            .Select(item => OrbData.TryGetColorAndTier(item.ItemId, out var color, out _)
                ? color
                : OrbColor.None)
            .Where(color => color != OrbColor.None)
            .GroupBy(color => color)
            .OrderByDescending(group => group.Count())
            .Select(group => group.Key)
            .FirstOrDefault();
        if (favorite == OrbColor.None)
            return false;

        var orbBoard = matchRuntimes.GetOrThrow(matchingId).OrbUpgrades;
        if (GetUpgradeCost(matchingId, playerId, favorite, orbBoard) <= 0)
        {
            favorite = FamilyColors.FirstOrDefault(color => GetUpgradeCost(matchingId, playerId, color, orbBoard) > 0);
            if (favorite == OrbColor.None)
            {
                return false;
            }
        }

        return TryUpgrade(matchingId, playerId, favorite, out _, out _);
    }

    public SummonOrbAttempt Summon(MatchRuntime runtime, long playerId, AreaType area)
    {
        RequireLock(runtime);
        var attempt = runtime.SummonStones.TrySummon(playerId, itemId => runtime.Inventory.TryAddItemWithCapacity(playerId, itemId, Config.SWARM_ORB_CAPACITY, out var added) ? added : null);
        if (attempt is { Success: true, AddedItem: not null })
        {
            var inventory = runtime.Inventory.GetPlayerInventory(playerId);
            eventLogs.LogOrbBoardTransition(runtime.MatchingId, playerId, inventory.GetAllItems(), inventory.GetOrderedOrbs().FirstOrDefault()?.ItemId ?? 0, area.ToString(), "summon", isBot: playerId < 0);
        }

        eventLogs.LogOrbSummonAttempt(runtime.MatchingId, playerId, attempt.Success, attempt.ErrorCode, attempt.ItemId, attempt.State.StoneCount, attempt.State.NextCost, attempt.State.SuccessfulSummonCount, area.ToString(), isBot: playerId < 0);
        return attempt;
    }

    public void Grant(MatchRuntime runtime, long playerId, int itemId)
    {
        RequireLock(runtime);
        runtime.Inventory.GetPlayerInventory(playerId).TryAddItemWithCapacity(itemId, Config.SWARM_ORB_CAPACITY, out _);
    }

    private static void RequireLock(MatchRuntime runtime)
    {
        if (!Monitor.IsEntered(runtime.MatchLock))
        {
            throw new InvalidOperationException("Orb inventory changes require the match lock.");
        }
    }

    public int GetNextOrbGrowthCost(long matchingId, long playerId)
    {
        var match = matchRuntimes.GetOrThrow(matchingId);
        int cost = match.Inventory.GetOrbScore(playerId).OrbCount < Config.SWARM_ORB_CAPACITY ? match.SummonStones.GetSnapshot(playerId).NextCost : int.MaxValue;
        var upgrades = GetOrbUpgradeInfo(matchingId, playerId);
        foreach (int upgradeCost in new[] { upgrades.SunCost, upgrades.WindCost, upgrades.WaveCost })
        {
            if (upgradeCost > 0)
            {
                cost = Math.Min(cost, upgradeCost);
            }
        }

        return cost;
    }

    public void ProcessBotOrbGrowth(long matchingId, IReadOnlyList<BotPlayerState> aliveBots)
    {
        var match = matchRuntimes.GetOrThrow(matchingId);
        foreach (var bot in aliveBots)
        {
            if (bot.Player.IsEliminated)
            {
                continue;
            }

            if (match.SummonStones.GetSnapshot(bot.PlayerId).StoneCount <
                GetNextOrbGrowthCost(matchingId, bot.PlayerId))
            {
                continue;
            }
            int orbCount = match.Inventory.GetOrbScore(bot.PlayerId).OrbCount;
            bool preferUpgrade = orbCount >= Config.SWARM_ORB_CAPACITY || (orbCount >= 4 && Random.Shared.Next(3) == 0);
            if (preferUpgrade && TryUpgradeForBot(matchingId, bot.PlayerId))
            {
                continue;
            }

            if (orbCount < Config.SWARM_ORB_CAPACITY && Summon(match, bot.PlayerId, bot.Player.CurrentArea).Success)
            {
                continue;
            }
            TryUpgradeForBot(matchingId, bot.PlayerId);
        }
    }
    public int GetTopOrbCount(long matchingId)
    {
        var match = matchRuntimes.GetOrThrow(matchingId);
        int top = 0;
        foreach (var player in match.GetAlivePlayers())
        {
            top = Math.Max(top, match.Inventory.GetOrbScore(player.PlayerId).OrbCount);
        }
        return top;
    }
}
