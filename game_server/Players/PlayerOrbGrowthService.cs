using game_server.logging;
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
internal sealed class PlayerOrbGrowthService(
    GameEventLogManager eventLogs,
    ILogger<PlayerOrbGrowthService> logger)
{
    public const int NormalMonsterReward = 1;
    public const int CoreMonsterReward = 3;

    // 회복 오브(107000040)는 퇴역했다. 회복은 새 오브 영입이 맡는다 (#219).
    // 공급 차단 토글(SWARM_SUN/WAVE_ORB_ENABLED=false)이면 소환 풀에서 그 색이 빠진다.
    private static readonly int[] SummonPool = BuildSummonPool();
    private static readonly int[] OpeningAttackPool = SummonPool.Where(itemId => !OrbData.IsRecoveryOrb(itemId)).ToArray();

    /// <summary>시작 소환석 5 — 첫 개봉(비용 5) 한 번을 보장해 개전 직후 드래프트를 먼저 보여준다. 이후는 몹 처치로 번다.</summary>
    public static int InitialSummonStoneCount => 5;

    public static IReadOnlyList<int> SummonPoolItemIds => SummonPool;

    private static int[] BuildSummonPool()
    {
        var pool = new List<int>();
        if (Config.SWARM_SUN_ORB_ENABLED) pool.Add(107000010);
        if (Config.SWARM_WIND_ORB_ENABLED) pool.Add(107000020);
        if (Config.SWARM_WAVE_ORB_ENABLED) pool.Add(107000030);
        return pool.ToArray();
    }

    public static Player.SummonStoneState AddSummonStones(MatchRuntime match, Player player, int amount)
    {
        if (!Monitor.IsEntered(match.MatchLock))
        {
            throw new InvalidOperationException("Summon stone changes require the match lock.");
        }

        if (amount > 0)
        {
            player.SummonStones = player.SummonStones with { StoneCount = checked(player.SummonStones.StoneCount + amount) };
        }
        return player.SummonStones;
    }

    /// <summary>오브 강화 비용을 차감한다. 잔액이 부족하면 변경하지 않는다.</summary>
    internal static bool TrySpendSummonStones(MatchRuntime match, Player player, int amount)
    {
        if (!Monitor.IsEntered(match.MatchLock))
        {
            throw new InvalidOperationException("Summon stone changes require the match lock.");
        }

        if (amount < 0 || player.SummonStones.StoneCount < amount)
        {
            return false;
        }

        player.SummonStones = player.SummonStones with { StoneCount = player.SummonStones.StoneCount - amount };
        return true;
    }

    /// <summary>
    ///     비용을 확인하고 풀에서 뽑은 오브를 지급받아 소환석과 소환 횟수를 갱신한다. 지급이 실패하면 아무것도 바꾸지 않는다.
    /// </summary>
    internal static SummonResult TrySummon(MatchRuntime match, Player player, Func<int, InGameItemInfo?> grantItem)
    {
        if (!Monitor.IsEntered(match.MatchLock))
        {
            throw new InvalidOperationException("Summon stone changes require the match lock.");
        }
        ArgumentNullException.ThrowIfNull(grantItem);

        int cost = player.SummonStones.NextCost;
        if (player.SummonStones.StoneCount < cost)
        {
            return SummonResult.Failed(ErrorCode.INSUFFICIENT_CURRENCY, player.SummonStones);
        }

        int itemId = DrawSummonOrb(player.SummonStones.SuccessfulSummonCount);
        var item = grantItem(itemId);
        if (item == null)
        {
            return SummonResult.Failed(ErrorCode.INVENTORY_FULL, player.SummonStones);
        }

        player.SummonStones = new Player.SummonStoneState(player.SummonStones.StoneCount - cost, player.SummonStones.SuccessfulSummonCount + 1);
        return new SummonResult(true, ErrorCode.SUCCESS, itemId, item, player.SummonStones);
    }

    /// <summary>개전 직후에는 잔상과 싸울 수단이 바로 필요하다. 첫 소환만 공격 오브 풀에서 뽑는다.</summary>
    private static int DrawSummonOrb(int successfulSummonCount)
    {
        int[] pool = successfulSummonCount == 0 ? OpeningAttackPool : SummonPool;
        return pool[Random.Shared.Next(pool.Length)];
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

        long playerId = player.PlayerId;
        var area = player.CurrentArea;
        var attempt = TrySummon(runtime, player, itemId => player.Orbs.TryAddItemWithCapacity(itemId, Config.SWARM_ORB_CAPACITY, out var added) ? added : null);
        if (attempt is { Success: true, AddedItem: not null })
        {
            var inventory = player.Orbs;
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

        if (!TrySpendSummonStones(runtime, player, cost))
        {
            return (false, 0, -1);
        }

        player.IncrementOrbUpgradeCount(orbGroupId);
        var inventory = player.Orbs;
        bool replaced = inventory.TryReplaceOrb(target.ItemUid, upgradedItemId, out _);

        eventLogs.LogSystem(matchingId, $"ORB_UPGRADED player={playerId} bot={playerId < 0} group={orbGroupId} ordinal={ordinal} " + $"uid={target.ItemUid} tier={tier}->{tier + 1} cost={cost} replaced={replaced}");
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

        int cost = player.Orbs.GetOrbScore().OrbCount < Config.SWARM_ORB_CAPACITY ? player.SummonStones.NextCost : int.MaxValue;
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
        InGameItemInfo? AddedItem, Player.SummonStoneState State)
    {
        public static SummonResult Failed(ErrorCode errorCode, Player.SummonStoneState state) =>
            new(false, errorCode, 0, null, state);
    }
}
