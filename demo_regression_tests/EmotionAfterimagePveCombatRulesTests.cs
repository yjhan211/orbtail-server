using game_server.services;
using network.common;
using network.common.data;
using network.common.data.models;

namespace demo_regression_tests;

public class EmotionAfterimagePveCombatRulesTests
{
    [Fact]
    public void WaveOrb_UsesSlowerBaseAttackInterval()
    {
        Assert.Equal(1.25f,
            SurvivorOrbData.GetBaseAttackIntervalMultiplier(SurvivorOrbColor.Blue));
        Assert.Equal(1f,
            SurvivorOrbData.GetBaseAttackIntervalMultiplier(SurvivorOrbColor.Red));
        Assert.Equal(2.875f,
            2.3f * SurvivorOrbData.GetBaseAttackIntervalMultiplier(SurvivorOrbColor.Blue), 3);
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
    [InlineData(107000010, 107000020, 1.5f)]
    [InlineData(107000020, 107000030, 1.5f)]
    [InlineData(107000030, 107000010, 1.5f)]
    [InlineData(107000010, 107000030, 0.5f)]
    [InlineData(107000020, 107000010, 0.5f)]
    [InlineData(107000030, 107000020, 0.5f)]
    [InlineData(107000010, 107000011, 1f)]
    [InlineData(107000010, 107000040, 1f)]
    public void PveAffinity_FollowsOrbColorCycle(int attackerItemId, int monsterRewardItemId,
        float expectedMultiplier)
    {
        Assert.Equal(expectedMultiplier,
            SurvivorOrbData.GetPveDamageMultiplier(attackerItemId, monsterRewardItemId));
    }

    [Fact]
    public void WindOrb_HalvesDamageAndDoublesItsBaseCadence()
    {
        Assert.Equal(0.5f, SurvivorOrbData.WindBaseDamageMultiplier);
        Assert.Equal(0.5f,
            SurvivorOrbData.GetBaseAttackIntervalMultiplier(SurvivorOrbColor.Green));
        Assert.Equal(2, SurvivorOrbData.GetBaseAttackDamage(4, SurvivorOrbColor.Green));
        Assert.Equal(3, SurvivorOrbData.GetBaseAttackDamage(7, SurvivorOrbColor.Green));
        Assert.Equal(5, SurvivorOrbData.GetBaseAttackDamage(10, SurvivorOrbColor.Green));
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
            targets, 1, -100, AreaType.Classroom4);

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

    private static ProximityCombatActor CombatTarget(long playerId, float x, float y,
        AreaType area = AreaType.Classroom4) =>
        new(playerId, area, new Vector3f(x, y, 0f), 0, 0f, 0, 0f, 0f, 0f, MapId.School);
    private static MonsterCombatTarget Target(int monsterId, MapId mapId, AreaType area, float x, float y) =>
        new(monsterId, mapId, area, new Vector3f(x, y, 0f), 107000010);
    [Fact]
    public void DominantPveColor_UsesTotalTierAcrossTheWholeBoard()
    {
        Assert.True(SurvivorOrbData.TryGetDominantPveColor(
            [107000010, 107000010, 107000032], out var dominantColor));

        Assert.Equal(SurvivorOrbColor.Blue, dominantColor);
    }

    [Fact]
    public void DominantPveColor_UsesReadyResonanceToBreakATierTie()
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
    public void BoardWidePveAffinity_AppliesToEveryOrbAttack()
    {
        Assert.True(SurvivorOrbData.TryGetDominantPveColor(
            [107000010, 107000010, 107000010, 107000020], out var dominantColor));

        Assert.Equal(SurvivorOrbColor.Red, dominantColor);
        Assert.Equal(1.5f, SurvivorOrbData.GetPveDamageMultiplier(dominantColor, 107000020));
        Assert.Equal(6, SurvivorOrbData.CalculatePveDamage(dominantColor, 107000020, 4));
    }
}
