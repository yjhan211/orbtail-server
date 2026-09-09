using game_server;
using game_server.matches.logging;
using game_server.matches.states;
using game_server.matches;
using game_server.sessions;
using Microsoft.Extensions.Logging;
using network.common;
using network.common.data;
using network.common.data.models;

namespace game_server.matches.orbs;

/// <summary>
///     사람·봇의 시작 자원 지급과 오브 강화, 레벨·비용 계산을 처리한다.
///     인벤토리와 구매 횟수는 매치 런타임이 소유하고, 호출자는 해당 매치 잠금을 보유한다.
///     전송은 호출자가 담당하며 이 서비스는 세션의 전송 메서드를 호출하지 않는다.
/// </summary>
internal sealed class OrbUpgradeService(
    MatchRuntimeStore matchRuntimes,
    GameEventLogManager eventLogs,
    GameServerDevOptions devOptions,
    ILogger<OrbUpgradeService> logger)
{
    internal static int[] CreateStartingOrbPool()
    {
        var pool = new List<int>();
        if (Config.SWARM_SUN_ORB_ENABLED) pool.Add(107000010);
        if (Config.SWARM_WIND_ORB_ENABLED) pool.Add(107000020);
        if (Config.SWARM_WAVE_ORB_ENABLED) pool.Add(107000030);
        return pool.ToArray();
    }

    // 공급이 활성화된 색만 시작 지급과 재건에 사용한다.
    private static readonly int[] StartingOrbPool = CreateStartingOrbPool();

    private static readonly OrbColor[] FamilyColors =
        [OrbColor.Red, OrbColor.Green, OrbColor.Blue];

    // 봇은 긴 꼬리를 가지고 시작해 초반부터 절단할 표적을 제공한다.
    private const int BotStartingOrbCount = 10;

    /// <summary>
    ///     사람은 시작 오브 대신 소환 비용만큼의 소환석을 받는다.
    ///     교차사격 샌드박스에서는 사람에게 고정 색 순서로 오브를 주고, 봇에게는 시작 오브를 직접 준다.
    /// </summary>
    public void GrantStartingOrbs(long matchingId, long playerId, bool isBot)
    {
        bool fixedSet = devOptions.CrossfireSandbox && !isBot;
        if (!isBot && !fixedSet)
        {
            int stonesInsteadOfOrbs = 0;
            for (int index = 0; index < Config.SWARM_STARTING_ORB_GRANT_COUNT; index++)
                stonesInsteadOfOrbs += Math.Min(Config.SWARM_GROWTH_COST_CAP, Config.GetSwarmGrowthBaseCost(index));
            matchRuntimes.GetOrThrow(matchingId).SummonStones.AddStones(playerId, stonesInsteadOfOrbs);
            return;
        }

        int grantCount = !isBot ? Config.SWARM_STARTING_ORB_GRANT_COUNT : BotStartingOrbCount;
        for (int index = 0; index < grantCount; index++)
        {
            int itemId = fixedSet
                ? StartingOrbPool[index % StartingOrbPool.Length]
                : StartingOrbPool[Random.Shared.Next(StartingOrbPool.Length)];
            matchRuntimes.GetOrThrow(matchingId).Inventory.TryAddItemWithCapacity(
                playerId, itemId, Config.SWARM_ORB_CAPACITY, out _);
        }

    }

    /// <summary>
    ///     계열 대표 레벨 = 보유 오브 중 최고 티어(없으면 1). 표시용 — 구매하는 "공유 레벨"은 퇴역했고
    ///     티어는 오브마다 따로 오른다.
    /// </summary>
    private int GetFamilyLevel(long matchingId, long playerId, OrbColor color)
    {
        int best = 1;
        foreach (var item in matchRuntimes.GetOrThrow(matchingId).Inventory.GetPlayerInventory(playerId).GetOrderedOrbs())
        {
            if (OrbData.TryGetColorAndTier(item.ItemId, out var itemColor, out int tier) &&
                itemColor == color && tier > best)
                best = tier;
        }

        return best;
    }

    /// <summary>
    ///     해당 계열에서 몸체에 가장 가까운 T3 미만 오브를 찾는다.
    ///     GetOrderedOrbs의 ItemUid 순서가 클라이언트 슬롯 순번과 같으며 대상이 없으면 -1을 반환한다.
    /// </summary>
    private int FindUpgradeTargetOrdinal(
        long matchingId, long playerId, OrbColor color, out InGameItemInfo? target)
    {
        var orbs = matchRuntimes.GetOrThrow(matchingId).Inventory.GetPlayerInventory(playerId).GetOrderedOrbs();
        for (int ordinal = 0; ordinal < orbs.Count; ordinal++)
        {
            if (!OrbData.TryGetColorAndTier(orbs[ordinal].ItemId, out var itemColor, out int tier) ||
                itemColor != color || tier >= 3)
                continue;
            target = orbs[ordinal];
            return ordinal;
        }

        target = null;
        return -1;
    }

    /// <summary>이 계열의 다음 강화 비용. 강화할 오브(T3 미만)가 없으면 0(강화 불가).</summary>

    private int GetUpgradeCost(
        long matchingId, long playerId, OrbColor color, OrbUpgradeState orbBoard)
    {
        if (FindUpgradeTargetOrdinal(matchingId, playerId, color, out _) < 0)
            return 0;
        int purchases = orbBoard.GetFamilyUpgradeCount(playerId, color);
        return Math.Min(Config.SWARM_GROWTH_COST_CAP, Config.GetSwarmGrowthBaseCost(purchases));
    }

    /// <summary>
    ///     해당 계열에서 몸체에 가장 가까운 T3 미만 오브 하나를 한 티어 올린다.
    ///     ItemUid·열 순번은 유지되고 ItemId만 바뀐다. 강화할 오브 없음·소환석 부족이면 거절.
    ///     targetOrdinal = 오른 오브의 열 순번 — 클라가 그 오브 위에 강화 이펙트를 띄운다.
    /// </summary>
    private bool TryUpgrade(
        long matchingId, long playerId, OrbColor color, GameClientSession? session,
        out int resultItemId, out int targetOrdinal)
    {
        resultItemId = 0;
        targetOrdinal = -1;
        if (!FamilyColors.Contains(color))
            return false;

        int ordinal = FindUpgradeTargetOrdinal(matchingId, playerId, color, out var target);
        if (ordinal < 0 || target == null)
            return false;
        OrbUpgradeState orbBoard = matchRuntimes.GetOrThrow(matchingId).OrbUpgrades;
        int cost = GetUpgradeCost(matchingId, playerId, color, orbBoard);
        if (cost <= 0)
            return false;
        if (!OrbData.TryGetColorAndTier(target.ItemId, out _, out int tier) ||
            !OrbData.TryGetItemId(color, tier + 1, out int upgradedItemId))
            return false;
        if (!matchRuntimes.GetOrThrow(matchingId).SummonStones.TrySpendStones(playerId, cost, out _))
            return false;

        orbBoard.IncrementFamilyUpgradeCount(playerId, color);

        var inventory = matchRuntimes.GetOrThrow(matchingId).Inventory.GetPlayerInventory(playerId);
        bool replaced = inventory.TryReplaceOrb(target.ItemUid, upgradedItemId, out _);
        resultItemId = upgradedItemId;
        targetOrdinal = ordinal;

        eventLogs.LogSystem(
            matchingId,
            $"ORB_UPGRADED player={playerId} bot={session == null} family={color} ordinal={ordinal} " +
            $"uid={target.ItemUid} tier={tier}->{tier + 1} cost={cost} replaced={replaced}");
        return replaced;
    }

    /// <summary>현재 계열별 레벨·비용 데이터를 반환한다. 상태 변경과 전송은 하지 않는다.</summary>
    public G_TO_C_ORB_UPGRADE_INFO GetOrbUpgradeInfo(long matchingId, long playerId)
    {
        OrbUpgradeState orbBoard = matchRuntimes.GetOrThrow(matchingId).OrbUpgrades;
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

    /// <summary>
    ///     세션이 매치 잠금 안에서 호출하는 강화 요청 처리.
    ///     강화 결과와 대상 순번을 반환한다. 요청 응답 패킷은 호출한 핸들러가 전송한다.
    /// </summary>
    public (bool Success, int ResultItemId, int TargetOrdinal) HandleUpgradeOrb(
        GameClientSession session, long matchingId, int action, long targetUid, long secondUid)
    {
        _ = secondUid;
        if (!session.PlayerId.HasValue || session.IsEliminated)
            return (false, 0, -1);
        long playerId = session.PlayerId.Value;

        bool success = false;
        int resultItemId = 0;
        int targetOrdinal = -1;
        if (action == Config.ORB_UPGRADE_FAMILY)
            success = TryUpgrade(
                matchingId, playerId, (OrbColor)targetUid, session, out resultItemId, out targetOrdinal);

        logger.LogInformation(
            "Orb upgrade: MatchingId={MatchingId}, PlayerId={PlayerId}, Action={Action}, Target={Target}, Success={Success}, Result={Result}, Ordinal={Ordinal}",
            matchingId, playerId, action, targetUid, success, resultItemId, targetOrdinal);
        return (success, resultItemId, targetOrdinal);
    }

    /// <summary>
    ///     봇은 가장 많이 보유한 계열부터 강화하고, 해당 계열이 모두 T3이면 다른 계열을 찾는다.
    /// </summary>
    public bool TryUpgradeForBot(long matchingId, long playerId)
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

        // 최다 보유 계열이 전부 T3이면 강화 가능한 다른 계열을 찾는다.
        OrbUpgradeState orbBoard = matchRuntimes.GetOrThrow(matchingId).OrbUpgrades;
        if (GetUpgradeCost(matchingId, playerId, favorite, orbBoard) <= 0)
        {
            favorite = FamilyColors.FirstOrDefault(color =>
                GetUpgradeCost(matchingId, playerId, color, orbBoard) > 0);
            if (favorite == OrbColor.None)
                return false;
        }

        return TryUpgrade(matchingId, playerId, favorite, session: null, out _, out _);
    }
}
