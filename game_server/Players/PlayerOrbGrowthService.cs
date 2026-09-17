using game_server.matches;
using Microsoft.Extensions.Logging;
using network.common;
using network.common.data;
using network.common.data.models;

namespace game_server.players;

/// <summary>
///     플레이어와 봇의 소환석 지급·소비, 오브 소환·강화 비용과 성장 행동을 처리한다.
///     소환석 상태는 Player가, 오브 인벤토리는 매치 런타임이 소유하며, 호출자는 해당 매치 잠금을 보유해야 한다.
/// </summary>
internal sealed class PlayerOrbGrowthService(ILogger<PlayerOrbGrowthService> logger)
{
    private static readonly int[] SummonPool = BuildSummonPool();
    public static IReadOnlyList<int> SummonPoolItemIds => SummonPool;
    private static int[] BuildSummonPool()
    {
        (int OrbGroupId, bool Enabled)[] lines =
        {
            (OrbGroupIds.Sun, Config.SWARM_SUN_ORB_ENABLED),
            (OrbGroupIds.Wind, Config.SWARM_WIND_ORB_ENABLED),
            (OrbGroupIds.Wave, Config.SWARM_WAVE_ORB_ENABLED)
        };
        var pool = new List<int>();
        foreach (var (orbGroupId, enabled) in lines)
        {
            if (!enabled)
            {
                continue;
            }
            int itemId = OrbData.GetTierOneItemId(orbGroupId);
            if (itemId <= 0)
            {
                throw new InvalidOperationException($"battle_item_combat.csv has no tier 1 orb for {orbGroupId}.");
            }
            pool.Add(itemId);
        }
        return pool.ToArray();
    }

    public static SummonStoneStateInfo AddSummonStones(MatchRuntime match, Player player, int amount)
    {
        if (!Monitor.IsEntered(match.MatchLock))
        {
            throw new InvalidOperationException("Summon stone changes require the match lock.");
        }

        if (amount > 0)
        {
            player.Orbs.SummonStones = new SummonStoneStateInfo(checked(player.Orbs.SummonStones.StoneCount + amount), player.Orbs.SummonStones.SuccessfulSummonCount);
        }
        return player.Orbs.SummonStones;
    }

    internal static bool TrySpendSummonStones(MatchRuntime match, Player player, int amount)
    {
        if (!Monitor.IsEntered(match.MatchLock))
        {
            throw new InvalidOperationException("Summon stone changes require the match lock.");
        }

        if (amount < 0 || player.Orbs.SummonStones.StoneCount < amount)
        {
            return false;
        }

        player.Orbs.SummonStones = new SummonStoneStateInfo(player.Orbs.SummonStones.StoneCount - amount, player.Orbs.SummonStones.SuccessfulSummonCount);
        return true;
    }

    internal static SummonResult TrySummon(MatchRuntime match, Player player, Func<int, InGameItemInfo?> grantItem)
    {
        if (!Monitor.IsEntered(match.MatchLock))
        {
            throw new InvalidOperationException("Summon stone changes require the match lock.");
        }
        ArgumentNullException.ThrowIfNull(grantItem);

        int cost = SummonStoneStateInfo.CostAfter(player.Orbs.SummonStones.SuccessfulSummonCount);
        if (player.Orbs.SummonStones.StoneCount < cost)
        {
            return SummonResult.Failed(ErrorCode.INSUFFICIENT_CURRENCY, player.Orbs.SummonStones);
        }

        int itemId = DrawSummonOrb();
        var item = grantItem(itemId);
        if (item == null)
        {
            return SummonResult.Failed(ErrorCode.INVENTORY_FULL, player.Orbs.SummonStones);
        }

        player.Orbs.SummonStones = new SummonStoneStateInfo(player.Orbs.SummonStones.StoneCount - cost, player.Orbs.SummonStones.SuccessfulSummonCount + 1);
        return new SummonResult(true, ErrorCode.SUCCESS, itemId, item, player.Orbs.SummonStones);
    }

    private static int DrawSummonOrb()
    {
        return SummonPool[Random.Shared.Next(SummonPool.Length)];
    }

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
        AddSummonStones(runtime, player, startingStones);
    }

    public SummonResult Summon(MatchRuntime runtime, Player player)
    {
        if (!Monitor.IsEntered(runtime.MatchLock))
        {
            throw new InvalidOperationException("Orb inventory changes require the match lock.");
        }

        var attempt = TrySummon(runtime, player, itemId => player.Orbs.TryAddOrbWithCapacity(itemId, Config.SWARM_ORB_CAPACITY, out var added) ? added : null);
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

        int cost = Math.Min(Config.SWARM_GROWTH_COST_CAP, Config.GetSwarmGrowthBaseCost(player.Orbs.GetUpgradeCount(orbGroupId)));
        if (cost <= 0 || !OrbData.TryGetOrbGroupAndTier(target.ItemId, out _, out int tier) || !OrbData.TryGetItemId(orbGroupId, tier + 1, out int upgradedItemId))
        {
            return (false, 0, -1);
        }

        if (!TrySpendSummonStones(runtime, player, cost))
        {
            return (false, 0, -1);
        }

        player.Orbs.IncrementUpgradeCount(orbGroupId);
        var inventory = player.Orbs;
        bool replaced = inventory.TryReplaceOrb(target.ItemUid, upgradedItemId, out _);

        logger.LogInformation("Orb upgrade: MatchingId={MatchingId}, PlayerId={PlayerId}, TargetItemId={TargetItemId}, Success={Success}, Result={Result}, Ordinal={Ordinal}", matchingId, playerId, targetItemId, replaced, upgradedItemId, ordinal);
        return (replaced, upgradedItemId, ordinal);
    }

    private int GetGroupLevel(MatchRuntime runtime, Player player, int orbGroupId)
    {
        int highestTier = 1;
        foreach (var item in player.Orbs.GetOrderedOrbs())
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
        var orbs = player.Orbs.GetOrderedOrbs();
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
        int purchases = player.Orbs.GetUpgradeCount(orbGroupId);
        return Math.Min(Config.SWARM_GROWTH_COST_CAP, Config.GetSwarmGrowthBaseCost(purchases));
    }

    public G_TO_C_ORB_UPGRADE_INFO GetOrbUpgradeInfo(MatchRuntime runtime, Player player)
    {
        if (!Monitor.IsEntered(runtime.MatchLock))
        {
            throw new InvalidOperationException("Orb growth operations require the match lock.");
        }

        int sunOrbGroupId = OrbGroupIds.Sun;
        int windOrbGroupId = OrbGroupIds.Wind;
        int waveOrbGroupId = OrbGroupIds.Wave;

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

        int cost = player.Orbs.GetOrbScore().OrbCount < Config.SWARM_ORB_CAPACITY ? player.Orbs.SummonStones.NextCost : int.MaxValue;
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
            top = Math.Max(top, player.Orbs.GetOrbScore().OrbCount);
        }
        return top;
    }

    public readonly record struct SummonResult(bool Success, ErrorCode ErrorCode, int ItemId,
        InGameItemInfo? AddedItem, SummonStoneStateInfo State)
    {
        public static SummonResult Failed(ErrorCode errorCode, SummonStoneStateInfo state) =>
            new(false, errorCode, 0, null, state);
    }
}
