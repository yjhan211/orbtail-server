using network.common;

namespace game_server.matches.combat;

/// <summary>봇이 즉시 선택하고 적용할 성장 카드 구성과 비용.</summary>
// Cost는 대표값(가장 싼 카드)이고, 실제 차감·표시는 카드별 비용이 한다 (#229).
public readonly record struct SwarmGrowthOfferState(
    int Cost, int SpawnItemId, int EnhanceTargetTier, int ArmorCount,
    int CostSummon, int CostAttack, int CostDefense)
{
    // 카드 인덱스 — 클라·서버·로그 공유 (증식/강화/철갑).
    public const int CardMultiply = 0;
    public const int CardEnhance = 1;
    public const int CardArmor = 2;

    public int GetCost(int cardIndex) => cardIndex switch
    {
        CardMultiply => CostSummon,
        CardEnhance => CostAttack,
        CardArmor => CostDefense,
        _ => Cost
    };
}
