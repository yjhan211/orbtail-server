using game_server.network;
using game_server.sessions;
using Microsoft.Extensions.Logging;
using network.common;
using network.common.data;
using network.common.data.models;

namespace game_server.services;

/// <summary>
///     성장 카드의 비용·제안·선택·효과 적용과 봇의 자동 투자를 처리한다.
///     제안 상태와 구매 횟수는 매치 런타임에 보관하고 호출자는 해당 매치 잠금을 보유한다.
///     세션에는 결과를 전달하며 GameServer를 참조하지 않는다.
/// </summary>
internal sealed class MatchGrowthService(
    MatchRuntimeStore matchRuntimes,
    GameEventLogManager eventLogs,
    OrbUpgradeService orbUpgrades,
    ILogger<MatchGrowthService> logger) : IPlayerGrowthHandler
{
    // 방어 카드가 부여하는 외피 보너스. 샌드박스도 같은 값을 사용한다.
    internal const int ArmorDurabilityBonus = 4;
    internal const float BotPreyPowerAdvantage = 1.25f;
    private static readonly int[] SwarmStartingOrbPool = OrbUpgradeService.CreateStartingOrbPool();

    /// <summary>플레이어의 오브 강화 요청을 전용 서비스에 전달한다. 호출자의 매치 잠금을 그대로 사용한다.</summary>
    public (bool Success, int ResultItemId, int TargetOrdinal) HandleUpgradeOrb(GameClientSession session, long matchingId, int action, long targetItemUid, long secondItemUid) =>
        orbUpgrades.HandleUpgradeOrb(session, matchingId, action, targetItemUid, secondItemUid);

    /// <summary>현재 오브 수와 카드별 구매 횟수로 비용을 계산한다. 차감이나 카운터 변경은 하지 않는다.</summary>
    public (int BaseCost, int Surcharge, int FinalCost, int OrbCount,
        int CostSummon, int CostAttack, int CostDefense) GetCostBreakdown(
        long matchingId, long playerId)
    {
        var (orbCount, _) = matchRuntimes.GetRequired(matchingId).GetOrbScore(playerId);
        int CardCost(int cardIndex) => Config.GetSwarmGrowthCardCost(
            matchRuntimes.GetRequired(matchingId).SummonStones.GetGrowthSuccessCount(playerId, cardIndex), orbCount);

        int summon = CardCost(SwarmGrowthOfferState.CardMultiply);
        int attack = CardCost(SwarmGrowthOfferState.CardEnhance);
        int defense = CardCost(SwarmGrowthOfferState.CardArmor);
        int cheapest = Math.Min(summon, Math.Min(attack, defense));
        int growthCount = matchRuntimes.GetRequired(matchingId).SummonStones.GetGrowthSuccessCount(playerId);
        return (
            Config.GetSwarmGrowthBaseCost(growthCount),
            Config.GetSwarmGrowthScoreSurcharge(orbCount),
            cheapest,
            orbCount,
            summon, attack, defense);
    }

    /// <summary>
    ///     제안할 소환 오브와 방어 보너스를 확정한다. 소환은 활성 색의 T1 오브다.
    ///     빈손이면 소환만 제안하고, 보유 오브가 있으면 외피 없는 오브 수와 비용 구간으로 방어 보너스를 정한다.
    ///     공격 강화는 별도 계열 버튼으로 처리하므로 제안의 EnhanceTargetTier는 0이다.
    /// </summary>
    private SwarmGrowthOfferState GenerateSwarmGrowthOffer(
        long matchingId, long playerId, int offerId, int finalCost, int qualityCost, int orbCount,
        int costSummon, int costAttack, int costDefense)
    {
        if (orbCount <= 0)
        {
            int rebuildItemId = SwarmStartingOrbPool[Random.Shared.Next(SwarmStartingOrbPool.Length)];
            return new SwarmGrowthOfferState(
                offerId, finalCost, rebuildItemId,
                EnhanceTargetTier: 0, ArmorCount: 0,
                CostSummon: costSummon, CostAttack: costAttack, CostDefense: costDefense);
        }

        // 소환은 늘 T1: 티어는 오브마다 계열 버튼으로 따로 산다 — 공유 레벨 상속은 퇴역.
        // 품질 티어 RNG도 퇴역.
        _ = qualityCost;
        int spawnItemId = SwarmStartingOrbPool[Random.Shared.Next(SwarmStartingOrbPool.Length)];

        int armorSlots = matchRuntimes.GetRequired(matchingId).Inventory.GetPlayerInventory(playerId).GetOrderedOrbs()
            .Count(item => !matchRuntimes.GetRequired(matchingId).Swarm.TrailCombat.OrbDurabilityBonus.ContainsKey((matchingId, playerId, item.ItemUid)));
        int armorCount = armorSlots <= 0 ? 0 : 1;
        if (armorCount > 0 && armorSlots >= 2)
        {
            int armorRoll = Random.Shared.Next(100);
            if (qualityCost >= 7 && armorRoll < 30 || qualityCost >= 5 && armorRoll < 15)
                armorCount = 2;
        }

        // 6/6 포화 (#232 4단계): 소환 카드가 닫힌다 — 파괴로 빈칸을 만들어야 다시 열린다.
        if (matchRuntimes.GetRequired(matchingId).Inventory.GetPlayerInventory(playerId).GetOrderedOrbs().Count >= Config.SWARM_ORB_CAPACITY)
            spawnItemId = 0;

        return new SwarmGrowthOfferState(
            offerId,
            finalCost,
            spawnItemId,
            // 개별 강화 퇴역 (#232 4단계) — 계열 강화 버튼이 대신한다.
            EnhanceTargetTier: 0,
            armorCount,
            costSummon,
            costAttack,
            costDefense);
    }

    /// <summary>
    ///     봇 성장 틱: 소환석이 비용에 도달하면 봇이 성장 카드를 골라
    ///     즉시 자동 투자한다. 성공한 선택은 다음 카드별 비용 곡선에 바로 반영된다.
    /// </summary>
    // 사람에게 성장 선택지를 보내는 기능은 사용하지 않는다.

    public void ProcessOffers(
        long matchingId,
        List<GameClientSession> aliveSessions, List<BotPlayerState> aliveBots)
    {
        SwarmGrowthOfferCoordinator growthOffers =
            matchRuntimes.GetRequired(matchingId).Swarm.GrowthOfferCoordinator;

        foreach (var bot in aliveBots)
        {
            if (bot.IsSwarmCutDummy)
                continue;
            var (baseCost, surcharge, finalCost, orbCount, costSummon, costAttack, costDefense) =
                GetCostBreakdown(matchingId, bot.PlayerId);
            if (matchRuntimes.GetRequired(matchingId).SummonStones.GetSnapshot(bot.PlayerId).StoneCount < finalCost)
                continue;

            var offer = GenerateSwarmGrowthOffer(
                matchingId, bot.PlayerId, growthOffers.AllocateOfferId(), finalCost, baseCost, orbCount,
                costSummon, costAttack, costDefense);
            // 계열 강화 (#232 4단계): 6/6 포화면 소환이 닫히므로 강화가 봇의 주 지출이 된다.
            // 그 전에도 오브 4개 이상이면 셋에 한 번은 강화를 시도한다 — 카드 정책의 공격 강화
            // 자리를 잇는 셈이다 (개별 강화 카드는 퇴역).
            bool preferFamilyUpgrade = orbCount >= Config.SWARM_ORB_CAPACITY ||
                                       (orbCount >= 4 && Random.Shared.Next(3) == 0);
            if (preferFamilyUpgrade && orbUpgrades.TryUpgradeForBot(matchingId, bot.PlayerId))
                continue;

            int cardIndex = ChooseSwarmBotGrowthCard(
                matchingId, bot, offer, orbCount, aliveSessions, aliveBots);
            if (cardIndex == SwarmGrowthOfferState.CardEnhance)
            {
                orbUpgrades.TryUpgradeForBot(matchingId, bot.PlayerId);
                continue;
            }
            bool applied = ApplySwarmGrowthCard(matchingId, bot.PlayerId, cardIndex, offer);
            if (applied)
            {
                int successCountBefore = matchRuntimes.GetRequired(matchingId).SummonStones.GetGrowthSuccessCount(bot.PlayerId);
                // 카드별 카운터는 봇도 함께 민다 (#229) — 안 그러면 봇만 값이 안 올라
                // 사람보다 싸게 무한 성장하고, 봇 매치로 곡선을 검증할 수도 없다.
                matchRuntimes.GetRequired(matchingId).SummonStones.RecordGrowthSuccess(bot.PlayerId, cardIndex);
                eventLogs.LogSwarmGrowthSelected(
                    matchingId, bot.PlayerId, isBot: true,
                    GetSwarmGrowthCardRole(cardIndex), GetSwarmGrowthCardGrade(cardIndex, offer),
                    baseCost, surcharge, offer.GetCost(cardIndex), successCountBefore, orbCount);
            }
        }
    }

    /// <summary>생존자 최다 오브 수 — 봇 성장·추격 판단의 순위 기준.</summary>
    public int GetTopOrbCount(long matchingId)
    {
        int top = 0;
        foreach (var session in matchRuntimes.GetRequired(matchingId).Sessions.Snapshot())
        {
            if (session.PlayerId.HasValue && !session.IsEliminated)
                top = Math.Max(top, matchRuntimes.GetRequired(matchingId).GetOrbScore(session.PlayerId.Value).OrbCount);
        }

        foreach (var bot in matchRuntimes.GetRequired(matchingId).Bots.GetBots(matchingId))
        {
            if (!bot.IsEliminated && !bot.IsSwarmCutDummy)
                top = Math.Max(top, matchRuntimes.GetRequired(matchingId).GetOrbScore(bot.PlayerId).OrbCount);
        }

        return top;
    }

    /// <summary>처치각 판독 (#226 F): 같은 구역에 확실히 약한(전력 ×1.25 미만) 적이 있는가.</summary>
    private bool HasSwarmPreyInArea(
        long matchingId, BotPlayerState bot,
        List<GameClientSession> aliveSessions, List<BotPlayerState> aliveBots)
    {
        float myPower = matchRuntimes.GetRequired(matchingId).Inventory.GetPlayerInventory(bot.PlayerId).GetOrbPower();
        if (myPower <= 0f)
            return false;

        foreach (var session in aliveSessions)
        {
            if (session.PlayerId.HasValue && session.CurrentArea == bot.CurrentArea &&
                matchRuntimes.GetRequired(matchingId).Inventory.GetPlayerInventory(session.PlayerId.Value).GetOrbPower() *
                BotPreyPowerAdvantage <= myPower)
                return true;
        }

        foreach (var other in aliveBots)
        {
            if (other.PlayerId != bot.PlayerId && !other.IsSwarmCutDummy &&
                other.CurrentArea == bot.CurrentArea &&
                matchRuntimes.GetRequired(matchingId).Inventory.GetPlayerInventory(other.PlayerId).GetOrbPower() *
                BotPreyPowerAdvantage <= myPower)
                return true;
        }

        return false;
    }

    /// <summary>계측용 카드 역할 라벨 (#226 F) — 요약의 역할 분포 집계가 이 문자열을 센다.</summary>
    private static string GetSwarmGrowthCardRole(int cardIndex) => cardIndex switch
    {
        SwarmGrowthOfferState.CardMultiply => "multiply",
        SwarmGrowthOfferState.CardEnhance => "enhance",
        SwarmGrowthOfferState.CardArmor => "armor",
        _ => "unknown"
    };

    /// <summary>계측용 카드 등급 (#226 F) — 생성=지급 티어, 공격=강화 대상 티어(I/II), 방어=내구 증가량(I/II).</summary>
    private static int GetSwarmGrowthCardGrade(int cardIndex, SwarmGrowthOfferState offer)
    {
        switch (cardIndex)
        {
            case SwarmGrowthOfferState.CardMultiply:
                return OrbData.TryGetColorAndTier(offer.SpawnItemId, out _, out int tier)
                    ? tier
                    : 1;
            case SwarmGrowthOfferState.CardEnhance:
                return offer.EnhanceTargetTier;
            case SwarmGrowthOfferState.CardArmor:
                return offer.ArmorCount;
            default:
                return 0;
        }
    }




    /// <summary>
    ///     카드 효과 적용 (사람·봇 공통): 오퍼 시점에 확정된 서술자를 그대로 집행한다.
    ///     비용 차감이 성립할 때만 효과가 나가고, 픽 시점 재검증 실패면 오퍼가 유지된다.
    /// </summary>
    private bool ApplySwarmGrowthCard(
        long matchingId, long playerId, int cardIndex, SwarmGrowthOfferState offer)
    {
        // 차감은 고른 카드의 값으로 (#229): 세 카드가 각자 자기 곡선을 탄다.
        int cost = offer.GetCost(cardIndex);
        var inventory = matchRuntimes.GetRequired(matchingId).Inventory.GetPlayerInventory(playerId);
        switch (cardIndex)
        {
            case SwarmGrowthOfferState.CardMultiply:
                {
                    // 6/6 포화 (#232 4단계): 소환 불가 — 기존 5회 탭 파괴로 빈칸을 만든 뒤 다시 소환한다.
                    if (inventory.GetAllItems().Count >= Config.SWARM_ORB_CAPACITY)
                        return false;
                    if (!matchRuntimes.GetRequired(matchingId).SummonStones.TrySpendStones(playerId, cost, out _))
                        return false;
                    // 소환은 T1 그대로: 티어는 오브마다 따로 산다 — 공유 레벨 상속은 퇴역.
                    inventory.TryAddItemWithCapacity(offer.SpawnItemId, Config.SWARM_ORB_CAPACITY, out _);
                    orbUpgrades.SendFamilyLevels(matchingId, playerId, session: null);
                    return true;
                }
            case SwarmGrowthOfferState.CardEnhance:
                // 개별 오브 공격 강화 퇴역 (#232 4단계): 강화는 계열 공유 레벨(태양·바람·파도 직접
                // 버튼 → C_TO_G_UPGRADE_ORB)이 맡는다. 오퍼도 EnhanceTargetTier 0으로 나간다.
                return false;
            case SwarmGrowthOfferState.CardArmor:
                {
                    if (offer.ArmorCount <= 0)
                        return false;
                    var targets = matchRuntimes.GetRequired(matchingId).Inventory.GetPlayerInventory(playerId).GetOrderedOrbs()
                        .Where(item =>
                            !matchRuntimes.GetRequired(matchingId).Swarm.TrailCombat.OrbDurabilityBonus.ContainsKey((matchingId, playerId, item.ItemUid)))
                        .Take(offer.ArmorCount)
                        .ToList();
                    if (targets.Count == 0)
                        return false;
                    if (!matchRuntimes.GetRequired(matchingId).SummonStones.TrySpendStones(playerId, cost, out _))
                        return false;
                    foreach (var target in targets)
                        matchRuntimes.GetRequired(matchingId).Swarm.TrailCombat.OrbDurabilityBonus[(matchingId, playerId, target.ItemUid)] =
                            ArmorDurabilityBonus;
                    return true;
                }
            default:
                return false;
        }
    }

    private int ChooseSwarmBotGrowthCard(
        long matchingId, BotPlayerState bot, SwarmGrowthOfferState offer, int orbCount,
        List<GameClientSession> aliveSessions, List<BotPlayerState> aliveBots)
    {
        if (orbCount < 5)
            return SwarmGrowthOfferState.CardMultiply;

        int topOrbCount = GetTopOrbCount(matchingId);
        bool isLeader = orbCount >= topOrbCount;
        int leaderGap = topOrbCount - orbCount;
        bool hasPrey = HasSwarmPreyInArea(matchingId, bot, aliveSessions, aliveBots);

        var (multiply, enhance) = leaderGap >= 3 ? (70, 20) :
            isLeader ? (35, 20) :
            hasPrey ? (35, 45) : (45, 30);

        int roll = Random.Shared.Next(100);
        if (roll < multiply)
            return SwarmGrowthOfferState.CardMultiply;
        if (roll < multiply + enhance)
            return offer.EnhanceTargetTier > 0 ? SwarmGrowthOfferState.CardEnhance : SwarmGrowthOfferState.CardMultiply;
        return offer.ArmorCount > 0 ? SwarmGrowthOfferState.CardArmor : SwarmGrowthOfferState.CardMultiply;
    }
}
