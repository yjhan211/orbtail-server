using System.Diagnostics;
using game_server.players;
using network.common.data.models;

namespace demo_regression_tests;

public sealed class PlayerMovementTimingTests
{
    [Fact]
    public void MoveDelta_UsesPreviousProcessingTimeAndClampsLongGaps()
    {
        var player = new Player { Profile = new PlayerInfo { PlayerId = 1 } };
        long timestamp = Stopwatch.Frequency;
        Assert.Equal(0.05f, player.CalculateMoveDeltaTime(timestamp));
        Assert.Equal(0.125f, player.CalculateMoveDeltaTime(timestamp + Stopwatch.Frequency / 8));
        Assert.Equal(0f, player.CalculateMoveDeltaTime(timestamp + Stopwatch.Frequency / 8));
        Assert.Equal(0.25f, player.CalculateMoveDeltaTime(timestamp * 3));
    }


}
