using game_server.network;
using game_server.services;
using Microsoft.Extensions.Logging;
using network.common;
using network.common.data;
using network.common.data.models;

namespace game_server;

/// <summary>
///     6칸 빌드 (#232 4단계). 꼬리는 최대 6개 오브를 월드에 그대로 보여 주는 이동식 빌드판이다.
///     슬롯별 오브를 고르거나 끌어 합성하지 않는다 — 이번 매치에 공개된 세 계열(태양·바람·파도)을
///     직접 강화하고, 6칸이 찼을 때만 원하지 않는 오브를 파괴해 구성을 다시 만든다.
///     - 시작: 무작위 T1 공격 오브 3개 + 소환석 5 (샌드박스 사람은 태양·바람·파도 한 개씩 고정)
///     - 계열 강화: 그 계열의 공유 레벨 T1→T2→T3. 현재 보유 오브가 전부 갱신되고 이후 소환도 그 레벨로 등장
///     - 미보유 계열은 강화 불가. 마지막 오브를 파괴해도 구매한 레벨은 매치 끝까지 유지
///     - 6/6 포화: 소환 불가 — 기존 5회 탭 파괴(환급 = 오브당 소환석 1 고정)로 빈칸을 만든다
///     이동과 자동 PvE 공격은 이 과정에서 멈추지 않는다.
/// </summary>
public partial class GameServer
{
    // 샌드박스 고정 시작 세트 (#232 P0-A): 태양·바람·파도 한 개씩 — 세 모양이 다 보이게 (2026-08-17 저녁).
    private static readonly int[] SwarmSandboxStartingOrbs = [107000010, 107000020, 107000030];

    private static readonly SurvivorOrbColor[] SwarmFamilyColors =
        [SurvivorOrbColor.Red, SurvivorOrbColor.Green, SurvivorOrbColor.Blue];

    // 계열 공유 레벨 (matchingId, playerId, color) → 1~3. 없으면 1.
    private readonly Dictionary<(long MatchingId, long PlayerId, SurvivorOrbColor Color), int>
        _swarmFamilyLevels = new();

    // 계열별 강화 구매 횟수 — 비용 곡선(min(21, 5+2N))의 N. 소환 카드 곡선과 독립이다.
    private readonly Dictionary<(long MatchingId, long PlayerId, SurvivorOrbColor Color), int>
        _swarmFamilyUpgradeCounts = new();

    /// <summary>시작 오브 지급 — 사람·봇 공통. 샌드박스 사람만 고정 세트.</summary>
    private void GrantSwarmStartingOrbs(long matchingId, long playerId, GameClientSession? session)
    {
        bool fixedSet = SwarmCrossfireSandbox && session != null;
        for (int index = 0; index < Config.SWARM_STARTING_ORB_GRANT_COUNT; index++)
        {
            int itemId = fixedSet
                ? SwarmSandboxStartingOrbs[index % SwarmSandboxStartingOrbs.Length]
                : SwarmStartingOrbPool[Random.Shared.Next(SwarmStartingOrbPool.Length)];
            if (session != null)
                session.GrantSwarmArenaOrb(itemId);
            else
                _inGameInventoryManager.TryAddItemWithCapacity(
                    matchingId, playerId, itemId, Config.SWARM_ORB_CAPACITY, out _);
        }

        SendSwarmFamilyLevels(matchingId, playerId, session);
    }

    private int GetSwarmFamilyLevel(long matchingId, long playerId, SurvivorOrbColor color) =>
        _swarmFamilyLevels.TryGetValue((matchingId, playerId, color), out int level) ? level : 1;

    /// <summary>이 계열의 다음 강화 비용. 미보유·T3이면 0(강화 불가).</summary>
    private int GetSwarmFamilyUpgradeCost(long matchingId, long playerId, SurvivorOrbColor color)
    {
        if (GetSwarmFamilyLevel(matchingId, playerId, color) >= 3)
            return 0;
        bool owned = GetSwarmTrailOrbs(matchingId, playerId).Any(item =>
            SurvivorOrbData.TryGetColorAndTier(item.ItemId, out var itemColor, out _) && itemColor == color);
        if (!owned)
            return 0;
        int purchases = _swarmFamilyUpgradeCounts.TryGetValue((matchingId, playerId, color), out int count)
            ? count
            : 0;
        return Math.Min(Config.SWARM_GROWTH_COST_CAP, Config.GetSwarmGrowthBaseCost(purchases));
    }

    /// <summary>소환할 오브를 그 계열의 현재 공유 레벨로 보정한다.</summary>
    private int ApplySwarmFamilyLevelToItem(long matchingId, long playerId, int itemId)
    {
        if (!SurvivorOrbData.TryGetColorAndTier(itemId, out var color, out _))
            return itemId;
        int level = GetSwarmFamilyLevel(matchingId, playerId, color);
        return SurvivorOrbData.TryGetItemId(color, level, out int leveled) ? leveled : itemId;
    }

    /// <summary>
    ///     계열 강화 (#232 4단계): 공유 레벨 +1, 현재 보유한 같은 계열 오브 전부 일괄 갱신,
    ///     이후 소환도 새 레벨로 등장. 미보유·T3·소환석 부족이면 거절.
    /// </summary>
    private bool TryUpgradeSwarmFamily(
        long matchingId, long playerId, SurvivorOrbColor color, GameClientSession? session, out int resultItemId)
    {
        resultItemId = 0;
        if (!SwarmFamilyColors.Contains(color))
            return false;

        int cost = GetSwarmFamilyUpgradeCost(matchingId, playerId, color);
        if (cost <= 0)
            return false;
        if (!_summonStoneManager.TrySpendStones(matchingId, playerId, cost, out _))
            return false;

        int newLevel = GetSwarmFamilyLevel(matchingId, playerId, color) + 1;
        _swarmFamilyLevels[(matchingId, playerId, color)] = newLevel;
        _swarmFamilyUpgradeCounts[(matchingId, playerId, color)] =
            (_swarmFamilyUpgradeCounts.TryGetValue((matchingId, playerId, color), out int count) ? count : 0) + 1;
        SurvivorOrbData.TryGetItemId(color, newLevel, out resultItemId);

        // 보유 오브 일괄 갱신 — ItemUid·열 순번은 유지되고 ItemId만 바뀐다.
        var inventory = _inGameInventoryManager.GetPlayerInventory(matchingId, playerId);
        int updated = 0;
        foreach (var item in GetSwarmTrailOrbs(matchingId, playerId))
        {
            if (!SurvivorOrbData.TryGetColorAndTier(item.ItemId, out var itemColor, out int itemTier) ||
                itemColor != color || itemTier >= newLevel)
                continue;
            if (inventory.TryReplaceSurvivorOrb(item.ItemUid, resultItemId, out _))
                updated++;
        }

        session?.SendInGameInventoryList();
        SendSwarmFamilyLevels(matchingId, playerId, session);
        _gameEventLogManager.LogSystem(
            matchingId,
            $"ORB_FAMILY_UPGRADED player={playerId} bot={session == null} family={color} " +
            $"level={newLevel} cost={cost} updatedOrbs={updated}");
        return true;
    }

    /// <summary>계열 레벨·비용 스냅샷 전송 — 시작·강화·오브 증감 때.</summary>
    private void SendSwarmFamilyLevels(long matchingId, long playerId, GameClientSession? session)
    {
        session?.SendSwarmFamilyLevels(
            GetSwarmFamilyLevel(matchingId, playerId, SurvivorOrbColor.Red),
            GetSwarmFamilyLevel(matchingId, playerId, SurvivorOrbColor.Green),
            GetSwarmFamilyLevel(matchingId, playerId, SurvivorOrbColor.Blue),
            GetSwarmFamilyUpgradeCost(matchingId, playerId, SurvivorOrbColor.Red),
            GetSwarmFamilyUpgradeCost(matchingId, playerId, SurvivorOrbColor.Green),
            GetSwarmFamilyUpgradeCost(matchingId, playerId, SurvivorOrbColor.Blue));
    }

    /// <summary>클라 결정 요청 (사람 전용). 결과는 항상 응답한다 — 거절이면 Success=false.</summary>
    private void HandleSwarmOrbDecision(
        GameClientSession session, long matchingId, int action, long targetUid, long secondUid)
    {
        _ = secondUid;
        if (!session.PlayerId.HasValue || session.IsEliminated)
            return;
        long playerId = session.PlayerId.Value;

        bool success = false;
        int resultItemId = 0;
        if (action == Config.SWARM_ORB_DECISION_FAMILY_UPGRADE)
            success = TryUpgradeSwarmFamily(
                matchingId, playerId, (SurvivorOrbColor)targetUid, session, out resultItemId);

        session.SendSwarmOrbDecisionResult(action, success, resultItemId, targetUid);
        logger.LogInformation(
            "Swarm orb decision: MatchingId={MatchingId}, PlayerId={PlayerId}, Action={Action}, Target={Target}, Success={Success}, Result={Result}",
            matchingId, playerId, action, targetUid, success, resultItemId);
    }

    /// <summary>
    ///     봇 계열 강화 (#232 4단계 최소): 가장 많이 보유한 계열을 강화한다. 성장 카드에서 공격 강화가
    ///     퇴역한 자리를 이 호출이 잇는다 — 8단계에서 상황 판단으로 바꾼다.
    /// </summary>
    private bool TryUpgradeSwarmFamilyForBot(long matchingId, long playerId)
    {
        var favorite = GetSwarmTrailOrbs(matchingId, playerId)
            .Select(item => SurvivorOrbData.TryGetColorAndTier(item.ItemId, out var color, out _)
                ? color
                : SurvivorOrbColor.None)
            .Where(color => color != SurvivorOrbColor.None)
            .GroupBy(color => color)
            .OrderByDescending(group => group.Count())
            .Select(group => group.Key)
            .FirstOrDefault();
        if (favorite == SurvivorOrbColor.None)
            return false;

        // 최다 보유 계열이 이미 T3이면 강화 가능한 다른 계열을 찾는다.
        if (GetSwarmFamilyUpgradeCost(matchingId, playerId, favorite) <= 0)
        {
            favorite = SwarmFamilyColors.FirstOrDefault(color =>
                GetSwarmFamilyUpgradeCost(matchingId, playerId, color) > 0);
            if (favorite == SurvivorOrbColor.None)
                return false;
        }

        return TryUpgradeSwarmFamily(matchingId, playerId, favorite, session: null, out _);
    }

    private void ClearSwarmOrbBoardState(long matchingId)
    {
        foreach (var key in _swarmFamilyLevels.Keys.Where(key => key.MatchingId == matchingId).ToList())
            _swarmFamilyLevels.Remove(key);
        foreach (var key in _swarmFamilyUpgradeCounts.Keys.Where(key => key.MatchingId == matchingId).ToList())
            _swarmFamilyUpgradeCounts.Remove(key);
    }
}
