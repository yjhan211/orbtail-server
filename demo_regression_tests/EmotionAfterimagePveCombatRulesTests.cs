using game_server.services;
using network.common;
using network.common.data;
using network.common.data.models;

namespace demo_regression_tests;

public class EmotionAfterimagePveCombatRulesTests
{
    [Fact]
    public void AllColors_ShareTheSameBaseAttackInterval()
    {
        // #219 M2 공격 문법 통일: 색별 공속 차이 퇴역.
        Assert.Equal(1f, SurvivorOrbData.GetBaseAttackIntervalMultiplier(SurvivorOrbColor.Blue));
        Assert.Equal(1f, SurvivorOrbData.GetBaseAttackIntervalMultiplier(SurvivorOrbColor.Red));
        Assert.Equal(1f, SurvivorOrbData.GetBaseAttackIntervalMultiplier(SurvivorOrbColor.Green));
    }

    [Fact]
    public void AllOrbLines_FireAtTwiceThePreviousRate()
    {
        Assert.Equal(0.5f, SurvivorOrbData.GetAttackIntervalMultiplier(107000003));
        Assert.Equal(0.5f, SurvivorOrbData.GetAttackIntervalMultiplier(107000010));
        Assert.Equal(0.5f, SurvivorOrbData.GetAttackIntervalMultiplier(107000030));
        Assert.Equal(1f, SurvivorOrbData.GetAttackIntervalMultiplier(201000015));
    }

    [Theory]
    [InlineData(107000010, 107000020)]
    [InlineData(107000020, 107000030)]
    [InlineData(107000030, 107000010)]
    [InlineData(107000010, 107000030)]
    [InlineData(107000020, 107000010)]
    [InlineData(107000030, 107000020)]
    [InlineData(107000010, 107000011)]
    [InlineData(107000010, 107000040)]
    public void PveAffinity_IsAlwaysNeutralAfterUnification(int attackerItemId, int monsterRewardItemId)
    {
        // 색 상성(1.5/0.5) 퇴역 — 클론 비목표(상성 금지).
        Assert.Equal(1f,
            SurvivorOrbData.GetPveDamageMultiplier(attackerItemId, monsterRewardItemId));
    }

    [Fact]
    public void WindOrb_NoLongerModifiesDamageOrCadence()
    {
        // 바람 반감·가속 퇴역 — 전 색 동일 데미지·공속.
        Assert.Equal(1f,
            SurvivorOrbData.GetBaseAttackIntervalMultiplier(SurvivorOrbColor.Green));
        Assert.Equal(4, SurvivorOrbData.GetBaseAttackDamage(4, SurvivorOrbColor.Green));
        Assert.Equal(7, SurvivorOrbData.GetBaseAttackDamage(7, SurvivorOrbColor.Green));
        Assert.Equal(10, SurvivorOrbData.GetBaseAttackDamage(10, SurvivorOrbColor.Green));
    }

    [Fact]
    public void WaveAreaAttack_SelectsEveryPlayerAndMonsterAroundTheImpact()
    {
        ProximityCombatActor[] targets =
        [
            CombatTarget(1, 0f, 0f),
            CombatTarget(-100, 2f, 0f),
            CombatTarget(-101, 3.5f, 0f),
            CombatTarget(2, 2f, 1.5f),
            CombatTarget(3, 4f, 0f),
            CombatTarget(4, 2f, 2f, AreaType.Classroom2)
        ];

        var result = EmotionAfterimagePveCombatRules.FindWaveAreaSecondaryTargetIds(
            targets, 1, -100, AreaType.Classroom4, 107000030);

        Assert.Equal([-101, 2], result);
    }

    [Fact]
    public void OnlyNonResonanceWaveOrbs_UseAreaDamage()
    {
        Assert.True(EmotionAfterimagePveCombatRules.IsWaveOrb(107000030));
        Assert.False(EmotionAfterimagePveCombatRules.IsWaveOrb(107000010));
        Assert.True(EmotionAfterimagePveCombatRules.ShouldApplyWaveAreaAttack(107000030, false));
        Assert.False(EmotionAfterimagePveCombatRules.ShouldApplyWaveAreaAttack(107000030, true));
        Assert.False(EmotionAfterimagePveCombatRules.ShouldApplyWaveAreaAttack(107000010, false));
    }

    [Fact]
    public void WaveAreaAttack_ExpandsItsRadiusWithOrbTier()
    {
        Assert.Equal(1.8f, EmotionAfterimagePveCombatRules.GetWaveSplashRadius(107000030));
        Assert.Equal(2.2f, EmotionAfterimagePveCombatRules.GetWaveSplashRadius(107000031));
        Assert.Equal(2.6f, EmotionAfterimagePveCombatRules.GetWaveSplashRadius(107000032));
    }

    [Fact]
    public void WaveAreaAttack_OnlyPrimaryHitEmitsProjectilePresentation()
    {
        Assert.True(EmotionAfterimagePveCombatRules.ShouldEmitWaveProjectilePresentation(false));
        Assert.False(EmotionAfterimagePveCombatRules.ShouldEmitWaveProjectilePresentation(true));
    }

    private static ProximityCombatActor CombatTarget(long playerId, float x, float y,
        AreaType area = AreaType.Classroom4) =>
        new(playerId, area, new Vector3f(x, y, 0f), 0, 0f, 0, 0f, 0f, 0f, MapId.School);
    private static MonsterCombatTarget Target(int monsterId, MapId mapId, AreaType area, float x, float y) =>
        new(monsterId, mapId, area, new Vector3f(x, y, 0f), 107000010);
    [Fact]
    public void DominantPveColor_UsesStrictOrbCountMajorityAcrossTheWholeBoard()
    {
        Assert.True(SurvivorOrbData.TryGetDominantPveColor(
            [107000010, 107000010, 107000032], out var dominantColor));

        Assert.Equal(SurvivorOrbColor.Red, dominantColor);
    }

    [Fact]
    public void DominantPveColor_ActivatesWhenOneColourOwnsMoreThanHalfTheBoard()
    {
        Assert.True(SurvivorOrbData.TryGetDominantPveColor(
            [107000010, 107000010, 107000010, 107000032], out var dominantColor));

        Assert.Equal(SurvivorOrbColor.Red, dominantColor);
    }

    [Fact]
    public void DominantPveColor_StaysNeutralWhenTopTierAndResonanceBothTie()
    {
        Assert.False(SurvivorOrbData.TryGetDominantPveColor(
            [107000010, 107000010, 107000010, 107000020, 107000020, 107000020], out _));
    }

    [Fact]
    public void DominantPveColor_RequiresAMajorityAcrossRecoverySlotsToo()
    {
        Assert.False(SurvivorOrbData.TryGetDominantPveColor(
            [107000010, 107000010, 107000020, 107000040, 107000040], out _));
    }
    [Fact]
    public void BoardWidePveAffinity_AppliesToEveryOrbAttack()
    {
        Assert.True(SurvivorOrbData.TryGetDominantPveColor(
            [107000010, 107000010, 107000010, 107000020], out var dominantColor));

        Assert.Equal(SurvivorOrbColor.Red, dominantColor);
        // 상성 퇴역: 지배색이어도 배율은 중립이다.
        Assert.Equal(1f, SurvivorOrbData.GetPveDamageMultiplier(dominantColor, 107000020));
        Assert.Equal(4, SurvivorOrbData.CalculatePveDamage(dominantColor, 107000020, 4));
    }
}
