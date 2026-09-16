using network.common;
using network.common.data;

namespace demo_regression_tests;

public class OrbPveCombatDataTests
{
    public OrbPveCombatDataTests()
    {
        TestGameData.EnsureBattleItemCombatLoaded();
    }

    [Theory]
    [InlineData(107000010, 107000020)]
    [InlineData(107000020, 107000030)]
    [InlineData(107000030, 107000010)]
    [InlineData(107000010, 107000030)]
    [InlineData(107000020, 107000010)]
    [InlineData(107000030, 107000020)]
    [InlineData(107000010, 107000011)]
    public void PveAffinity_IsAlwaysNeutralAfterUnification(int attackerItemId, int monsterRewardItemId)
    {
        // 색 상성(1.5/0.5) 퇴역 — 클론 비목표(상성 금지).
        Assert.Equal(1f,
            OrbData.GetPveDamageMultiplier(attackerItemId, monsterRewardItemId));
    }

    [Fact]
    public void DominantPveColor_UsesStrictOrbCountMajorityAcrossTheWholeBoard()
    {
        Assert.True(OrbData.TryGetDominantPveColor(
            [107000010, 107000010, 107000032], out var dominantColor));

        Assert.Equal(OrbColor.Red, dominantColor);
    }

    [Fact]
    public void DominantPveColor_ActivatesWhenOneColourOwnsMoreThanHalfTheBoard()
    {
        Assert.True(OrbData.TryGetDominantPveColor(
            [107000010, 107000010, 107000010, 107000032], out var dominantColor));

        Assert.Equal(OrbColor.Red, dominantColor);
    }

    [Fact]
    public void DominantPveColor_StaysNeutralWhenTopTierAndResonanceBothTie()
    {
        Assert.False(OrbData.TryGetDominantPveColor(
            [107000010, 107000010, 107000010, 107000020, 107000020, 107000020], out _));
    }

    [Fact]
    public void DominantPveColor_RequiresAMajorityAcrossAllOrbColors()
    {
        Assert.False(OrbData.TryGetDominantPveColor(
            [107000010, 107000010, 107000020, 107000030, 107000030], out _));
    }
    [Fact]
    public void BoardWidePveAffinity_AppliesToEveryOrbAttack()
    {
        Assert.True(OrbData.TryGetDominantPveColor(
            [107000010, 107000010, 107000010, 107000020], out var dominantColor));

        Assert.Equal(OrbColor.Red, dominantColor);
        // 상성 퇴역: 지배색이어도 배율은 중립이다.
        Assert.Equal(1f, OrbData.GetPveDamageMultiplier(dominantColor, 107000020));
        Assert.Equal(4, OrbData.CalculatePveDamage(dominantColor, 107000020, 4));
    }
}
