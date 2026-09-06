using network.common;
using network.common.data.models;
using user_server.matching.creation;

namespace demo_regression_tests;

public sealed class DevMatchOverridesTests
{
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void RulesOnlyOverrideSoloMapValidation(bool solo)
    {
        var options = new DevMatchOverrides(solo);
        Assert.Equal(solo ? 1 : Config.SWARM_PLAYERS_PER_MATCH, options.GamePlayersPerMatch);
        Assert.Equal(solo ? MatchMode.SoloMapValidation : MatchMode.Normal, options.MatchMode);
    }

    [Fact]
    public void FromEnvironmentReadsOnceAndOnlyLiteralOneEnables()
    {
        string? previous = Environment.GetEnvironmentVariable(DevMatchOverrides.SoloMapValidationVariable);
        try
        {
            Environment.SetEnvironmentVariable(DevMatchOverrides.SoloMapValidationVariable, "1");
            var options = DevMatchOverrides.FromEnvironment();
            Environment.SetEnvironmentVariable(DevMatchOverrides.SoloMapValidationVariable, "true");
            Assert.True(options.IsSoloMapValidation);
            Assert.False(DevMatchOverrides.FromEnvironment().IsSoloMapValidation);
        }
        finally
        {
            Environment.SetEnvironmentVariable(DevMatchOverrides.SoloMapValidationVariable, previous);
        }
    }
}
