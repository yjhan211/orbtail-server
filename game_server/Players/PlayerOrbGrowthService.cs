using game_server.items;
using game_server.logging;
using game_server.matches;
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
    GameEventLogManager eventLogs,
    ILogger<PlayerOrbGrowthService> logger)
{
    internal static void GrantStartingSummonStones(MatchRuntime runtime, Player player)
    {
        if (!Monitor.IsEntered(runtime.MatchLock))
        {
            throw new InvalidOperationException("Orb growth operations require the match lock.");
        }

        int startingStones = Config.SWARM_STARTING_STONE_GRANT;
        for (int index = 0; index < Config.SWARM_STARTING_ORB_GRANT_COUNT; index++)
        {
            startingStones += Math.Min(Config.SWARM_GROWTH_COST_CAP, Config.GetSwarmGrowthBaseCost(index));
        }
        runtime.SummonStones.AddStones(player.PlayerId, startingStones);
    }

    public SummonOrbAttempt Summon(MatchRuntime runtime, Player player)
    {
        if (!Monitor.IsEntered(runtime.MatchLock))
        {
            throw new InvalidOperationException("Orb inventory changes require the match lock.");
        }

        long playerId = player.PlayerId;
        var area = player.CurrentArea;
        var attempt = runtime.SummonStones.TrySummon(playerId, itemId => runtime.Inventory.TryAddItemWithCapacity(playerId, itemId, Config.SWARM_ORB_CAPACITY, out var added) ? added : null);
        if (attempt is { Success: true, AddedItem: not null })
        {
            var inventory = runtime.Inventory.GetPlayerInventory(playerId);
            eventLogs.LogOrbBoardTransition(runtime.MatchingId, playerId, inventory.GetAllItems(), inventory.GetOrderedOrbs().FirstOrDefault()?.ItemId ?? 0, area.ToString(), "summon", isBot: playerId < 0);
        }

        eventLogs.LogOrbSummonAttempt(runtime.MatchingId, playerId, attempt.Success, attempt.ErrorCode, attempt.ItemId, attempt.State.StoneCount, attempt.State.NextCost, attempt.State.SuccessfulSummonCount, area.ToString(), isBot: playerId < 0);
        return attempt;
    }

    public (bool Success, int ResultItemId, int TargetOrdinal) UpgradeOrb(MatchRuntime runtime, Player player, int action, int targetItemId)
    {
        if (!Monitor.IsEntered(runtime.MatchLock))
        {
            throw new InvalidOperationException("Orb inventory changes require the match lock.");
        }

        long matchingId = runtime.MatchingId;
        if (player.PlayerId == 0 || player.IsEliminated || action != Config.ORB_UPGRADE_GROUP || !OrbData.TryGetOrbGroupAndTier(targetItemId, out int orbGroupId, out _))
        {
            return (false, 0, -1);
        }

        long playerId = player.PlayerId;
        int ordinal = FindUpgradeTargetOrdinal(runtime, player, orbGroupId, out var target);
        if (ordinal < 0 || target == null)
        {
            return (false, 0, -1);
        }

        int cost = Math.Min(Config.SWARM_GROWTH_COST_CAP, Config.GetSwarmGrowthBaseCost(player.GetOrbUpgradeCount(orbGroupId)));
        if (cost <= 0 || !OrbData.TryGetOrbGroupAndTier(target.ItemId, out _, out int tier) || !OrbData.TryGetOrbItemId(orbGroupId, tier + 1, out int upgradedItemId))
        {
            return (false, 0, -1);
        }

        if (!runtime.SummonStones.TrySpendStones(playerId, cost, out _))
        {
            return (false, 0, -1);
        }

        player.IncrementOrbUpgradeCount(orbGroupId);
        var inventory = runtime.Inventory.GetPlayerInventory(playerId);
        bool replaced = inventory.TryReplaceOrb(target.ItemUid, upgradedItemId, out _);

        eventLogs.LogSystem(matchingId, $"ORB_UPGRADED player={playerId} bot={playerId < 0} group={orbGroupId} ordinal={ordinal} " + $"uid={target.ItemUid} tier={tier}->{tier + 1} cost={cost} replaced={replaced}");
        logger.LogInformation("Orb upgrade: MatchingId={MatchingId}, PlayerId={PlayerId}, TargetItemId={TargetItemId}, Success={Success}, Result={Result}, Ordinal={Ordinal}", matchingId, playerId, targetItemId, replaced, upgradedItemId, ordinal);
        return (replaced, upgradedItemId, ordinal);
    }

    private int GetGroupLevel(MatchRuntime runtime, Player player, int orbGroupId)
    {
        int highestTier = 1;
        foreach (var item in runtime.Inventory.GetPlayerInventory(player.PlayerId).GetOrderedOrbs())
        {
            if (OrbData.TryGetOrbGroupAndTier(item.ItemId, out int itemGroupId, out int tier) &&
                itemGroupId == orbGroupId && tier > highestTier)
            {
                highestTier = tier;
            }
        }
        return highestTier;
    }

    private int FindUpgradeTargetOrdinal(MatchRuntime runtime, Player player, int orbGroupId, out InGameItemInfo? target)
    {
        var orbs = runtime.Inventory.GetPlayerInventory(player.PlayerId).GetOrderedOrbs();
        for (int ordinal = 0; ordinal < orbs.Count; ordinal++)
        {
            if (!OrbData.TryGetOrbGroupAndTier(orbs[ordinal].ItemId, out int itemGroupId, out int tier) ||
                itemGroupId != orbGroupId || tier >= 3)
            {
                continue;
            }
            target = orbs[ordinal];
            return ordinal;
        }

        target = null;
        return -1;
    }

    public int GetUpgradeCost(MatchRuntime runtime, Player player, int orbGroupId)
    {
        if (!Monitor.IsEntered(runtime.MatchLock))
        {
            throw new InvalidOperationException("Orb growth operations require the match lock.");
        }

        if (FindUpgradeTargetOrdinal(runtime, player, orbGroupId, out _) < 0)
        {
            return 0;
        }
        int purchases = player.GetOrbUpgradeCount(orbGroupId);
        return Math.Min(Config.SWARM_GROWTH_COST_CAP, Config.GetSwarmGrowthBaseCost(purchases));
    }

    public G_TO_C_ORB_UPGRADE_INFO GetOrbUpgradeInfo(MatchRuntime runtime, Player player)
    {
        if (!Monitor.IsEntered(runtime.MatchLock))
        {
            throw new InvalidOperationException("Orb growth operations require the match lock.");
        }

        int sunOrbGroupId = 107000010 / 10;
        int windOrbGroupId = 107000020 / 10;
        int waveOrbGroupId = 107000030 / 10;

        return new G_TO_C_ORB_UPGRADE_INFO
        {
            SunLevel = GetGroupLevel(runtime, player, sunOrbGroupId),
            WindLevel = GetGroupLevel(runtime, player, windOrbGroupId),
            WaveLevel = GetGroupLevel(runtime, player, waveOrbGroupId),
            SunCost = GetUpgradeCost(runtime, player, sunOrbGroupId),
            WindCost = GetUpgradeCost(runtime, player, windOrbGroupId),
            WaveCost = GetUpgradeCost(runtime, player, waveOrbGroupId)
        };
    }

    public int GetNextOrbGrowthCost(MatchRuntime runtime, Player player)
    {
        if (!Monitor.IsEntered(runtime.MatchLock))
        {
            throw new InvalidOperationException("Orb growth operations require the match lock.");
        }

        int cost = runtime.Inventory.GetOrbScore(player.PlayerId).OrbCount < Config.SWARM_ORB_CAPACITY ? runtime.SummonStones.GetSnapshot(player.PlayerId).NextCost : int.MaxValue;
        var upgrades = GetOrbUpgradeInfo(runtime, player);
        foreach (int upgradeCost in new[] { upgrades.SunCost, upgrades.WindCost, upgrades.WaveCost })
        {
            if (upgradeCost > 0)
            {
                cost = Math.Min(cost, upgradeCost);
            }
        }

        return cost;
    }

    public int GetTopOrbCount(MatchRuntime runtime)
    {
        if (!Monitor.IsEntered(runtime.MatchLock))
        {
            throw new InvalidOperationException("Orb growth operations require the match lock.");
        }

        int top = 0;
        foreach (var player in runtime.GetAlivePlayers())
        {
            top = Math.Max(top, runtime.Inventory.GetOrbScore(player.PlayerId).OrbCount);
        }
        return top;
    }
}
