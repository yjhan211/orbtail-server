using network.common;
using network.common.data;

namespace demo_regression_tests;

public class OrbPveCombatDataTests
{
    public OrbPveCombatDataTests()
    {
        TestGameData.EnsureBattleItemCombatLoaded();
    }

    [Fact]
    public void DominantPveColor_UsesStrictOrbCountMajorityAcrossTheWholeBoard()
    {
        Assert.True(OrbData.TryGetDominantOrbGroup(
            [107000010, 107000010, 107000032], out var dominantGroupId));

        Assert.Equal(OrbGroupIds.Sun, dominantGroupId);
    }

    [Fact]
    public void DominantPveColor_ActivatesWhenOneColourOwnsMoreThanHalfTheBoard()
    {
        Assert.True(OrbData.TryGetDominantOrbGroup(
            [107000010, 107000010, 107000010, 107000032], out var dominantGroupId));

        Assert.Equal(OrbGroupIds.Sun, dominantGroupId);
    }

    [Fact]
    public void DominantPveColor_StaysNeutralWhenTopTierAndResonanceBothTie()
    {
        Assert.False(OrbData.TryGetDominantOrbGroup(
            [107000010, 107000010, 107000010, 107000020, 107000020, 107000020], out _));
    }

    [Fact]
    public void DominantPveColor_RequiresAMajorityAcrossAllOrbColors()
    {
        Assert.False(OrbData.TryGetDominantOrbGroup(
            [107000010, 107000010, 107000020, 107000030, 107000030], out _));
    }
}
