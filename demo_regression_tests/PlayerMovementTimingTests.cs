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

    [Fact]
    public void ResponseInterval_IsRecordedSeparatelyAndIsolatedPerPlayer()
    {
        var player = new Player { Profile = new PlayerInfo { PlayerId = 1 } };
        var other = new Player { Profile = new PlayerInfo { PlayerId = 2 } };
        long timestamp = Stopwatch.Frequency;
        Assert.True(player.ShouldSendMoveResponse(timestamp));
        player.RecordMoveResponse(timestamp);
        Assert.False(player.ShouldSendMoveResponse(timestamp + Stopwatch.Frequency / 8));
        Assert.True(player.ShouldSendMoveResponse(timestamp + Stopwatch.Frequency / 4));
        Assert.True(other.ShouldSendMoveResponse(timestamp));
        // 간격 전에 보낸 보정 응답도 다음 응답의 기준 시각이 된다.
        player.RecordMoveResponse(timestamp + Stopwatch.Frequency / 8);
        Assert.False(player.ShouldSendMoveResponse(timestamp + Stopwatch.Frequency / 4));
    }
}
