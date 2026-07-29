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
    public void WaveSplashDamage_CombinesHalfDamageWithAffinity()
    {
        Assert.Equal(6, SurvivorOrbData.CalculatePveDamage(107000030, 107000010, 4));
        Assert.Equal(3, SurvivorOrbData.CalculatePveDamage(107000030, 107000010, 4, 0.5f));
        Assert.Equal(2, SurvivorOrbData.CalculatePveDamage(107000030, 107000020, 4));
        Assert.Equal(1, SurvivorOrbData.CalculatePveDamage(107000030, 107000020, 4, 0.5f));
    }

    [Fact]
    public void WaveSplash_SelectsAtMostTwoNearestMonstersInTheSameArea()
    {
        MonsterCombatTarget[] targets =
        [
            Target(100, MapId.School, AreaType.Classroom4, 0f, 0f),
            Target(104, MapId.School, AreaType.Classroom4, 1f, 0f),
            Target(102, MapId.School, AreaType.Classroom4, 1f, 0f),
            Target(103, MapId.School, AreaType.Classroom4, 1.5f, 0f),
            Target(105, MapId.School, AreaType.Classroom4, 1.81f, 0f),
            Target(106, MapId.School, AreaType.Classroom2, 0.5f, 0f),
            Target(107, MapId.None, AreaType.Classroom4, 0.5f, 0f)
        ];

        var result = EmotionAfterimagePveCombatRules.FindWaveSplashTargets(targets, 100);

        Assert.Equal([102, 104], result.Select(target => target.MonsterId));
    }

    [Fact]
    public void OnlyWaveOrbs_EnableMonsterSplash()
    {
        Assert.True(EmotionAfterimagePveCombatRules.IsWaveOrb(107000030));
        Assert.False(EmotionAfterimagePveCombatRules.IsWaveOrb(107000010));
        Assert.False(EmotionAfterimagePveCombatRules.IsWaveOrb(107000040));
    }

    [Fact]
    public void WaveSplash_IsLimitedToNonResonanceMonsterAttacks()
    {
        Assert.True(EmotionAfterimagePveCombatRules.ShouldApplyWaveSplash(107000030, -100, false));
        Assert.False(EmotionAfterimagePveCombatRules.ShouldApplyWaveSplash(107000030, 100, false));
        Assert.False(EmotionAfterimagePveCombatRules.ShouldApplyWaveSplash(107000030, -100, true));
        Assert.False(EmotionAfterimagePveCombatRules.ShouldApplyWaveSplash(107000010, -100, false));
    }

    private static MonsterCombatTarget Target(int monsterId, MapId mapId, AreaType area, float x, float y) =>
        new(monsterId, mapId, area, new Vector3f(x, y, 0f), 107000010);
    [Fact]
    public void DominantPveColor_UsesTotalTierAcrossTheWholeBoard()
    {
        Assert.True(SurvivorOrbData.TryGetDominantPveColor(
            [107000010, 107000010, 107000022], out var dominantColor));

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
