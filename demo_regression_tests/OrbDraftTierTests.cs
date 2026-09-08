using network.common.data;

namespace demo_regression_tests;

public sealed class OrbDraftTierTests
{
    [Theory]
    [InlineData(null, 1)]
    [InlineData(0d, 1)]
    [InlineData(79d, 1)]
    [InlineData(80d, 2)]
    [InlineData(159d, 2)]
    [InlineData(160d, 3)]
    public void DraftTier_UsesElapsedTimeOrDefaultsBeforeStart(double? elapsedSeconds, int expected)
    {
        Assert.Equal(expected, OrbData.GetDraftTierByElapsed(elapsedSeconds));
    }
}
