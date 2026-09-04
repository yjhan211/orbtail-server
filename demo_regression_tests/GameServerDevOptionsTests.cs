using game_server.services;
using Microsoft.Extensions.Configuration;

namespace demo_regression_tests;

public sealed class GameServerDevOptionsTests
{
    [Fact]
    public void FromConfiguration_ReadsFlagsOnceAndOnlyLiteralOneEnables()
    {
        IConfiguration configuration = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                [GameServerDevOptions.DisableGameEndVariable] = "1",
                [GameServerDevOptions.CrossfireSandboxVariable] = "true"
            })
            .Build();

        GameServerDevOptions options = GameServerDevOptions.FromConfiguration(configuration);
        configuration[GameServerDevOptions.DisableGameEndVariable] = "0";

        Assert.True(options.DisableGameEnd);
        Assert.False(options.CrossfireSandbox);
        Assert.Equal(new[] { GameServerDevOptions.DisableGameEndVariable }, options.EnabledVariableNames());
    }

    [Fact]
    public void Validate_RejectsDevelopmentFlagsOutsideDevelopment()
    {
        var options = new GameServerDevOptions { DisableGameEnd = true };

        InvalidOperationException error = Assert.Throws<InvalidOperationException>(() => options.Validate(false));

        Assert.Contains(GameServerDevOptions.DisableGameEndVariable, error.Message);
    }

    [Theory]
    [InlineData(true, true, false, false)]
    [InlineData(false, false, true, true)]
    public void Validate_RejectsConflictingSandboxFlags(
        bool crossfireSandbox,
        bool cutDummy,
        bool disableMonsters,
        bool soloMonsters)
    {
        var options = new GameServerDevOptions
        {
            CrossfireSandbox = crossfireSandbox,
            CutDummy = cutDummy,
            DisableMonsters = disableMonsters,
            SoloMonsters = soloMonsters
        };

        Assert.Throws<InvalidOperationException>(() => options.Validate(true));
    }
}
