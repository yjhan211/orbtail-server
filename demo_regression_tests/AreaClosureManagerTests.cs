using game_server.services;
using Microsoft.Extensions.Logging.Abstractions;
using network.common;

namespace demo_regression_tests;

public class AreaClosureManagerTests
{
    [Fact]
    public void InitializeMatching_StartsWithNoClosedAreas()
    {
        var config = new MatchingConfigService(null!, NullLogger.Instance);
        var manager = new AreaClosureManager(NullLogger.Instance, config);
        const long matchingId = 193001;

        var state = manager.InitializeMatching(matchingId);

        Assert.Empty(state.ClosedAreas);
        Assert.False(manager.IsAreaClosed(matchingId, AreaType.Gym));
        Assert.False(manager.IsAreaClosed(matchingId, AreaType.Storage));
        Assert.False(manager.IsAreaClosed(matchingId, AreaType.Ground));
    }
}
