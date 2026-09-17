using game_server.matches;
using Microsoft.Extensions.Logging.Abstractions;
using network.common.data.models;

namespace server_tests;

public sealed class MatchSetupTests
{
    [Fact]
    public void SetupRequiresMatchLockAndCannotBeReplaced()
    {
        var runtime = TestGameSessionServices.CreateMatchRuntimeStore(NullLogger.Instance).GetOrCreate(982001);
        var cells = new Dictionary<long, Cell>();
        PlayerInfo[] roster = [new() { PlayerId = 1 }, new() { PlayerId = -1 }];
        Assert.False(runtime.IsSetupComplete);
        Assert.Throws<InvalidOperationException>(() => runtime.InitializeMatch(default, cells, roster));
        Assert.False(runtime.IsSetupComplete);
        Assert.Empty(runtime.GetPlayerProfiles());

        using var scope = runtime.Enter();
        runtime.InitializeMatch(default, cells, roster);
        Assert.True(runtime.IsSetupComplete);
        Assert.Equal(roster.Select(player => player.PlayerId), runtime.GetPlayerProfiles().Select(player => player.PlayerId));
        Assert.Same(cells, runtime.SpawnCells);
        Assert.Throws<InvalidOperationException>(() => runtime.InitializeMatch(default, cells, []));
        Assert.Equal(roster.Select(player => player.PlayerId), runtime.GetPlayerProfiles().Select(player => player.PlayerId));
    }

    [Fact]
    public void EndedMatchRejectsSetup()
    {
        var runtime = TestGameSessionServices.CreateMatchRuntimeStore(NullLogger.Instance).GetOrCreate(982002);
        using var scope = runtime.Enter();
        runtime.TryMarkEnded();
        Assert.Throws<InvalidOperationException>(() => runtime.InitializeMatch(default, new Dictionary<long, Cell>(), []));
        Assert.False(runtime.IsSetupComplete);
        Assert.Empty(runtime.GetPlayerProfiles());
    }

    [Fact]
    public void InvalidSetupDoesNotPartiallyInitializeOrConsumeRegistration()
    {
        var runtime = TestGameSessionServices.CreateMatchRuntimeStore(NullLogger.Instance).GetOrCreate(982003);
        using var scope = runtime.Enter();
        Assert.Throws<ArgumentNullException>(() => runtime.InitializeMatch(default, new Dictionary<long, Cell>(), null!));
        Assert.False(runtime.IsSetupComplete);
        Assert.Empty(runtime.GetPlayerProfiles());
        runtime.InitializeMatch(default, new Dictionary<long, Cell>(), [new PlayerInfo { PlayerId = 1 }]);
        Assert.True(runtime.IsSetupComplete);
        Assert.Equal(1, Assert.Single(runtime.GetPlayerProfiles()).PlayerId);
    }
}
