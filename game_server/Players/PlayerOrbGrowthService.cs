using game_server.matches;
using Microsoft.Extensions.Logging;
using network.common;
using network.common.data;
using network.common.data.models;

namespace game_server.players;

/// <summary>
///     플레이어와 봇의 소환석 지급·소비, 오브 소환·강화 비용과 성장 행동을 처리한다.
///     소환석과 오브 꼬리는 Player.Orbs가 소유하며, 호출자는 해당 매치 잠금을 보유해야 한다.
/// </summary>
internal sealed class PlayerOrbGrowthService(ILogger<PlayerOrbGrowthService> logger)
{
    private static readonly int[] OrbGroups = [OrbGroupIds.Sun, OrbGroupIds.Wind, OrbGroupIds.Wave];
    private static readonly int[] SummonPool = BuildSummonPool();
    public static IReadOnlyList<int> SummonPoolItemIds => SummonPool;

    private static int[] BuildSummonPool()
    {
        var pool = new List<int>();
        foreach (int orbGroupId in OrbGroups)
        {
            bool enabled = orbGroupId switch
            {
                OrbGroupIds.Sun => Config.SWARM_SUN_ORB_ENABLED,
                OrbGroupIds.Wind => Config.SWARM_WIND_ORB_ENABLED,
                _ => Config.SWARM_WAVE_ORB_ENABLED
            };
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
            throw new InvalidOperationException("Orb growth requires the match lock.");
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
            throw new InvalidOperationException("Orb growth requires the match lock.");
        }

        if (amount < 0 || player.Orbs.SummonStones.StoneCount < amount)
        {
            return false;
        }

        player.Orbs.SummonStones = new SummonStoneStateInfo(player.Orbs.SummonStones.StoneCount - amount, player.Orbs.SummonStones.SuccessfulSummonCount);
        return true;
    }

    internal static void GrantStartingSummonStones(MatchRuntime runtime, Player player)
    {
        if (!Monitor.IsEntered(runtime.MatchLock))
        {
            throw new InvalidOperationException("Orb growth requires the match lock.");
        }

        int startingStones = Config.SWARM_STARTING_STONE_GRANT;
        for (int index = 0; index < Config.SWARM_STARTING_ORB_GRANT_COUNT; index++)
        {
            startingStones += GetGrowthCost(index);
        }
        AddSummonStones(runtime, player, startingStones);
    }

    public SummonResult Summon(MatchRuntime runtime, Player player)
    {
        if (!Monitor.IsEntered(runtime.MatchLock))
        {
            throw new InvalidOperationException("Orb growth requires the match lock.");
        }

        var stones = player.Orbs.SummonStones;
        int cost = SummonStoneStateInfo.CostAfter(stones.SuccessfulSummonCount);
        if (stones.StoneCount < cost)
        {
            return SummonResult.Failed(ErrorCode.INSUFFICIENT_CURRENCY, stones);
        }

        int itemId = SummonPool[Random.Shared.Next(SummonPool.Length)];
        if (!player.Orbs.TryAddOrbWithCapacity(itemId, Config.SWARM_ORB_CAPACITY, out var added) || added == null)
        {
            return SummonResult.Failed(ErrorCode.INVENTORY_FULL, stones);
        }

        player.Orbs.SummonStones = new SummonStoneStateInfo(stones.StoneCount - cost, stones.SuccessfulSummonCount + 1);
        return new SummonResult(true, ErrorCode.SUCCESS, itemId, added, player.Orbs.SummonStones);
    }

    public (bool Success, int ResultItemId, int TargetOrdinal) UpgradeOrb(MatchRuntime runtime, Player player, int action, int targetItemId)
    {
        if (!Monitor.IsEntered(runtime.MatchLock))
        {
            throw new InvalidOperationException("Orb growth requires the match lock.");
        }
        if (player.IsEliminated || action != Config.ORB_UPGRADE_GROUP || !OrbData.TryGetOrbGroupAndTier(targetItemId, out int orbGroupId, out _))
        {
            return (false, 0, -1);
        }

        int cost = GetUpgradeCost(runtime, player, orbGroupId);
        int ordinal = FindUpgradeTargetOrdinal(player, orbGroupId, out var target, out int upgradedItemId);
        if (cost <= 0 || target == null || !TrySpendSummonStones(runtime, player, cost))
        {
            return (false, 0, -1);
        }

        player.Orbs.IncrementUpgradeCount(orbGroupId);
        bool replaced = player.Orbs.TryReplaceOrb(target.ItemUid, upgradedItemId, out _);

        logger.LogInformation("Orb upgrade: MatchingId={MatchingId}, PlayerId={PlayerId}, TargetItemId={TargetItemId}, Success={Success}, Result={Result}, Ordinal={Ordinal}", runtime.MatchingId, player.PlayerId, targetItemId, replaced, upgradedItemId, ordinal);
        return (replaced, upgradedItemId, ordinal);
    }

    public int GetUpgradeCost(MatchRuntime runtime, Player player, int orbGroupId)
    {
        if (!Monitor.IsEntered(runtime.MatchLock))
        {
            throw new InvalidOperationException("Orb growth requires the match lock.");
        }

        if (FindUpgradeTargetOrdinal(player, orbGroupId, out _, out _) < 0)
        {
            return 0;
        }
        return GetGrowthCost(player.Orbs.GetUpgradeCount(orbGroupId));
    }

    public G_TO_C_ORB_UPGRADE_INFO GetOrbUpgradeInfo(MatchRuntime runtime, Player player)
    {
        if (!Monitor.IsEntered(runtime.MatchLock))
        {
            throw new InvalidOperationException("Orb growth requires the match lock.");
        }

        return new G_TO_C_ORB_UPGRADE_INFO
        {
            SunLevel = GetGroupLevel(player, OrbGroupIds.Sun),
            WindLevel = GetGroupLevel(player, OrbGroupIds.Wind),
            WaveLevel = GetGroupLevel(player, OrbGroupIds.Wave),
            SunCost = GetUpgradeCost(runtime, player, OrbGroupIds.Sun),
            WindCost = GetUpgradeCost(runtime, player, OrbGroupIds.Wind),
            WaveCost = GetUpgradeCost(runtime, player, OrbGroupIds.Wave)
        };
    }

    public int GetNextOrbGrowthCost(MatchRuntime runtime, Player player)
    {
        if (!Monitor.IsEntered(runtime.MatchLock))
        {
            throw new InvalidOperationException("Orb growth requires the match lock.");
        }

        int cost = player.Orbs.GetOrbScore().OrbCount < Config.SWARM_ORB_CAPACITY ? player.Orbs.SummonStones.NextCost : int.MaxValue;
        foreach (int orbGroupId in OrbGroups)
        {
            int upgradeCost = GetUpgradeCost(runtime, player, orbGroupId);
            if (upgradeCost > 0)
            {
                cost = Math.Min(cost, upgradeCost);
            }
        }
        return cost;
    }

    private static int GetGrowthCost(int purchases) =>
        Math.Min(Config.SWARM_GROWTH_COST_CAP, Config.GetSwarmGrowthBaseCost(purchases));

    private static int GetGroupLevel(Player player, int orbGroupId)
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

    private static int FindUpgradeTargetOrdinal(Player player, int orbGroupId, out InGameItemInfo? target, out int upgradedItemId)
    {
        var orbs = player.Orbs.GetOrderedOrbs();
        for (int ordinal = 0; ordinal < orbs.Count; ordinal++)
        {
            if (!OrbData.TryGetOrbGroupAndTier(orbs[ordinal].ItemId, out int itemGroupId, out int tier) || itemGroupId != orbGroupId)
            {
                continue;
            }
            if (!OrbData.TryGetItemId(orbGroupId, tier + 1, out upgradedItemId))
            {
                continue;
            }
            target = orbs[ordinal];
            return ordinal;
        }

        target = null;
        upgradedItemId = 0;
        return -1;
    }

    public readonly record struct SummonResult(bool Success, ErrorCode ErrorCode, int ItemId,
        InGameItemInfo? AddedItem, SummonStoneStateInfo State)
    {
        public static SummonResult Failed(ErrorCode errorCode, SummonStoneStateInfo state) =>
            new(false, errorCode, 0, null, state);
    }
}
