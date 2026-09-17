using System.Diagnostics;
using game_server.players;
using network.common.data.models;

namespace server_tests;

public sealed class PlayerMovementTimingTests
{
    [Fact]
    public void MoveDelta_UsesPreviousProcessingTimeAndClampsLongGaps()
    {
        var player = new Player(new PlayerInfo { PlayerId = 1 });
        long timestamp = Stopwatch.Frequency;
        Assert.Equal(0.05f, TestGameSessionServices.CreateMovementService().CalculateMoveDeltaTime(player, timestamp));
        Assert.Equal(0.125f, TestGameSessionServices.CreateMovementService().CalculateMoveDeltaTime(player, timestamp + Stopwatch.Frequency / 8));
        Assert.Equal(0f, TestGameSessionServices.CreateMovementService().CalculateMoveDeltaTime(player, timestamp + Stopwatch.Frequency / 8));
        Assert.Equal(0.25f, TestGameSessionServices.CreateMovementService().CalculateMoveDeltaTime(player, timestamp * 3));
    }


}
