using game_server.network;
using game_server.services;
using Microsoft.Extensions.Logging;
using network.common;
using network.common.data;
using network.common.data.models;

namespace game_server;

/// <summary>
///     오브 꼬리 강화 (#232 무한 꼬리, 2026-08-17). 꼬리는 상한 없이 오브를 월드에 그대로 보여 주는
///     이동식 빌드다 (Config.SWARM_ORB_CAPACITY=99는 정상 매치에서 닿지 않는 내부 안전장치 —
///     포화 상태·포화 해소 UI는 퇴역, 자발적 파괴는 OrbSummon의 DESTROY_ORB로 별도 존속).
///     슬롯별 오브를 고르거나 끌어 합성하지 않는다 — 이번 매치에 공개된
///     세 계열(태양·바람·파도)을 직접 강화한다.
///     - 시작: 사람은 오브 0개 + 시작 3오브 값의 소환석 (샌드박스 사람은 태양·바람·파도 한 개씩 고정, 봇은 10개)
///     - 오브 강화 (2026-08-18 유저 결정, 구 "계열 공유 레벨 일괄 강화"): 계열 버튼 한 번 = 그 계열에서
///       몸체에 가장 가까운(열 순번 최소) T3 미만 오브 하나가 한 티어 오른다. 앞 오브가 T3가 되면 그 다음 오브.
///       소환은 늘 T1로 온다 — 티어는 오브마다 따로 산다. 비용 곡선은 계열별 구매 횟수(min(21, 5+2N)).
///     - 강화할 오브(T3 미만)가 없는 계열은 강화 불가(비용 0)
///     이동과 자동 PvE 공격은 이 과정에서 멈추지 않는다.
/// </summary>
internal partial class GameServer
{
    // 샌드박스 고정 시작 세트 (#232 P0-A): 태양·바람·파도 한 개씩 — 세 모양이 다 보이게 (2026-08-17 저녁).
    private static readonly int[] SwarmSandboxStartingOrbs = BuildSwarmSupplyOrbPool();

    private static readonly OrbColor[] SwarmFamilyColors =
        [OrbColor.Red, OrbColor.Green, OrbColor.Blue];

    // 봇 시작 오브 (2026-08-18 유저 지시): 사람은 3개, 봇은 10개로 시작한다 — 사람이 처음부터 긴 꼬리를
    // 상대하고, 절단할 표적이 판 초반부터 있다.
    private const int SwarmBotStartingOrbCount = 10;

    /// <summary>
    ///     시작 지급 — 사람은 오브 0개 + 그 값만큼의 소환석(2026-08-18 유저 지시 "오브 0개로 시작하고 그만큼
    ///     소환석"): 시작 3오브에 해당하는 소환 비용(5+7+9=21)을 석으로 준다. 첫 판단이 "무엇을 소환할까"가 된다.
    ///     샌드박스 사람은 고정 세트 그대로, 봇은 10개.
    /// </summary>
    private void GrantSwarmStartingOrbs(long matchingId, long playerId, GameClientSession? session)
    {
        bool fixedSet = SwarmCrossfireSandbox && session != null;
        if (session != null && !fixedSet)
        {
            int stonesInsteadOfOrbs = 0;
            for (int index = 0; index < Config.SWARM_STARTING_ORB_GRANT_COUNT; index++)
                stonesInsteadOfOrbs += Math.Min(Config.SWARM_GROWTH_COST_CAP, Config.GetSwarmGrowthBaseCost(index));
            matchRuntimes.GetRequired(matchingId).SummonStones.AddStones(playerId, stonesInsteadOfOrbs);
            SendSwarmFamilyLevels(matchingId, playerId, session);
            return;
        }

        int grantCount = session != null ? Config.SWARM_STARTING_ORB_GRANT_COUNT : SwarmBotStartingOrbCount;
        for (int index = 0; index < grantCount; index++)
        {
            int itemId = fixedSet
                ? SwarmSandboxStartingOrbs[index % SwarmSandboxStartingOrbs.Length]
                : SwarmStartingOrbPool[Random.Shared.Next(SwarmStartingOrbPool.Length)];
            if (session != null)
                session.GrantSwarmArenaOrb(itemId);
            else
                matchRuntimes.GetRequired(matchingId).Inventory.TryAddItemWithCapacity(
                    playerId, itemId, Config.SWARM_ORB_CAPACITY, out _);
        }

        SendSwarmFamilyLevels(matchingId, playerId, session);
    }

    /// <summary>
    ///     계열 대표 레벨 = 보유 오브 중 최고 티어(없으면 1). 표시용 — 구매하는 "공유 레벨"은 퇴역했고
    ///     티어는 오브마다 따로 오른다.
    /// </summary>
    private int GetSwarmFamilyLevel(long matchingId, long playerId, OrbColor color)
    {
        int best = 1;
        foreach (var item in GetSwarmTrailOrbs(matchingId, playerId))
        {
            if (OrbData.TryGetColorAndTier(item.ItemId, out var itemColor, out int tier) &&
                itemColor == color && tier > best)
                best = tier;
        }

        return best;
    }

    /// <summary>
    ///     강화 대상 = 그 계열에서 몸체에 가장 가까운(열 순번 최소) T3 미만 오브. 열 순번은 GetSwarmTrailOrbs
    ///     순서(ItemUid 오름차순) — 클라 슬롯 순번·예고 패킷의 OwnerOrbOrdinal과 같은 자다. 없으면 -1.
    /// </summary>
    private int FindSwarmUpgradeTargetOrdinal(
        long matchingId, long playerId, OrbColor color, out InGameItemInfo? target)
    {
        var orbs = GetSwarmTrailOrbs(matchingId, playerId);
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
    private int GetSwarmFamilyUpgradeCost(long matchingId, long playerId, OrbColor color) =>
        GetSwarmFamilyUpgradeCost(
            matchingId, playerId, color, matchRuntimes.GetRequired(matchingId).Swarm.OrbBoard);

    private int GetSwarmFamilyUpgradeCost(
        long matchingId, long playerId, OrbColor color, SwarmOrbBoardState orbBoard)
    {
        if (FindSwarmUpgradeTargetOrdinal(matchingId, playerId, color, out _) < 0)
            return 0;
        int purchases = orbBoard.GetFamilyUpgradeCount(playerId, color);
        return Math.Min(Config.SWARM_GROWTH_COST_CAP, Config.GetSwarmGrowthBaseCost(purchases));
    }

    /// <summary>
    ///     오브 강화 (2026-08-18 유저 결정): 그 계열에서 몸체에 가장 가까운 T3 미만 오브 하나만 한 티어 올린다.
    ///     ItemUid·열 순번은 유지되고 ItemId만 바뀐다. 강화할 오브 없음·소환석 부족이면 거절.
    ///     targetOrdinal = 오른 오브의 열 순번 — 클라가 그 오브 위에 강화 이펙트를 띄운다.
    /// </summary>
    private bool TryUpgradeSwarmFamily(
        long matchingId, long playerId, OrbColor color, GameClientSession? session,
        out int resultItemId, out int targetOrdinal)
    {
        resultItemId = 0;
        targetOrdinal = -1;
        if (!SwarmFamilyColors.Contains(color))
            return false;

        int ordinal = FindSwarmUpgradeTargetOrdinal(matchingId, playerId, color, out var target);
        if (ordinal < 0 || target == null)
            return false;
        SwarmOrbBoardState orbBoard = matchRuntimes.GetRequired(matchingId).Swarm.OrbBoard;
        int cost = GetSwarmFamilyUpgradeCost(matchingId, playerId, color, orbBoard);
        if (cost <= 0)
            return false;
        if (!OrbData.TryGetColorAndTier(target.ItemId, out _, out int tier) ||
            !OrbData.TryGetItemId(color, tier + 1, out int upgradedItemId))
            return false;
        if (!matchRuntimes.GetRequired(matchingId).SummonStones.TrySpendStones(playerId, cost, out _))
            return false;

        orbBoard.IncrementFamilyUpgradeCount(playerId, color);

        var inventory = matchRuntimes.GetRequired(matchingId).Inventory.GetPlayerInventory(playerId);
        bool replaced = inventory.TryReplaceOrb(target.ItemUid, upgradedItemId, out _);
        resultItemId = upgradedItemId;
        targetOrdinal = ordinal;

        session?.SendInGameInventoryList();
        SendSwarmFamilyLevels(matchingId, playerId, session);
        eventLogs.LogSystem(
            matchingId,
            $"ORB_UPGRADED player={playerId} bot={session == null} family={color} ordinal={ordinal} " +
            $"uid={target.ItemUid} tier={tier}->{tier + 1} cost={cost} replaced={replaced}");
        return replaced;
    }

    /// <summary>계열 레벨·비용 스냅샷 전송 — 시작·강화·오브 증감 때.</summary>
    private void SendSwarmFamilyLevels(long matchingId, long playerId, GameClientSession? session)
    {
        SwarmOrbBoardState orbBoard = matchRuntimes.GetRequired(matchingId).Swarm.OrbBoard;
        session?.SendSwarmFamilyLevels(
            GetSwarmFamilyLevel(matchingId, playerId, OrbColor.Red),
            GetSwarmFamilyLevel(matchingId, playerId, OrbColor.Green),
            GetSwarmFamilyLevel(matchingId, playerId, OrbColor.Blue),
            GetSwarmFamilyUpgradeCost(matchingId, playerId, OrbColor.Red, orbBoard),
            GetSwarmFamilyUpgradeCost(matchingId, playerId, OrbColor.Green, orbBoard),
            GetSwarmFamilyUpgradeCost(matchingId, playerId, OrbColor.Blue, orbBoard));
    }

    /// <summary>
    ///     클라 결정 요청 (사람 전용) — 이 GameServer instance가 생성한 session delegate를 통해
    ///     ordered publication의 authoritative prepare 안에서 호출된다. 실행된 판정은 항상 응답하며
    ///     거절이면 Success=false다.
    /// </summary>
    private void HandleSwarmOrbDecision(
        GameClientSession session, long matchingId, int action, long targetUid, long secondUid)
    {
        _ = secondUid;
        if (!session.PlayerId.HasValue || session.IsEliminated)
            return;
        long playerId = session.PlayerId.Value;

        bool success = false;
        int resultItemId = 0;
        int targetOrdinal = -1;
        if (action == Config.SWARM_ORB_DECISION_FAMILY_UPGRADE)
            success = TryUpgradeSwarmFamily(
                matchingId, playerId, (OrbColor)targetUid, session, out resultItemId, out targetOrdinal);

        session.SendSwarmOrbDecisionResult(action, success, resultItemId, targetUid, targetOrdinal);
        logger.LogInformation(
            "Swarm orb decision: MatchingId={MatchingId}, PlayerId={PlayerId}, Action={Action}, Target={Target}, Success={Success}, Result={Result}, Ordinal={Ordinal}",
            matchingId, playerId, action, targetUid, success, resultItemId, targetOrdinal);
    }

    /// <summary>
    ///     봇 오브 강화 (#232 4단계 최소): 가장 많이 보유한 계열의 앞 오브부터 올린다. 성장 카드에서 공격 강화가
    ///     퇴역한 자리를 이 호출이 잇는다 — 8단계에서 상황 판단으로 바꾼다.
    /// </summary>
    private bool TryUpgradeSwarmFamilyForBot(long matchingId, long playerId)
    {
        var favorite = GetSwarmTrailOrbs(matchingId, playerId)
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
        SwarmOrbBoardState orbBoard = matchRuntimes.GetRequired(matchingId).Swarm.OrbBoard;
        if (GetSwarmFamilyUpgradeCost(matchingId, playerId, favorite, orbBoard) <= 0)
        {
            favorite = SwarmFamilyColors.FirstOrDefault(color =>
                GetSwarmFamilyUpgradeCost(matchingId, playerId, color, orbBoard) > 0);
            if (favorite == OrbColor.None)
                return false;
        }

        return TryUpgradeSwarmFamily(matchingId, playerId, favorite, session: null, out _, out _);
    }
}
