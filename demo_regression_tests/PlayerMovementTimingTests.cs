using game_server.players;
using game_server.matches;
using network.common.data.models;
using System.Diagnostics;
using Microsoft.Extensions.Logging.Abstractions;

namespace demo_regression_tests;

public sealed class PlayerMovementTimingTests
{
    private static PlayerMovementService Create() =>
        new(new MovementValidationService(NullLogger<MovementValidationService>.Instance),
            TestGameEventLogs.Create(), NullLogger.Instance);

    [Fact]
    public void MoveDelta_UsesPreviousProcessingTimeAndClampsLongGaps()
    {
        var movement = Create();
        var player = new Player { Profile = new PlayerInfo { PlayerId = 1 } };
        long timestamp = Stopwatch.Frequency;
        Assert.Equal(0.05f, movement.CalculateMoveDeltaTime(player, timestamp));
        Assert.Equal(0.125f, movement.CalculateMoveDeltaTime(player, timestamp + Stopwatch.Frequency / 8));
        Assert.Equal(0f, movement.CalculateMoveDeltaTime(player, timestamp + Stopwatch.Frequency / 8));
        Assert.Equal(0.25f, movement.CalculateMoveDeltaTime(player, timestamp * 3));
    }

    [Fact]
    public void ResponseInterval_IsRecordedSeparatelyAndIsolatedPerPlayer()
    {
        var movement = Create();
        var player = new Player { Profile = new PlayerInfo { PlayerId = 1 } };
        var other = new Player { Profile = new PlayerInfo { PlayerId = 2 } };
        long timestamp = Stopwatch.Frequency;
        Assert.True(movement.ShouldSendMoveResponse(player, timestamp));
        movement.RecordMoveResponse(player, timestamp);
        Assert.False(movement.ShouldSendMoveResponse(player, timestamp + Stopwatch.Frequency / 8));
        Assert.True(movement.ShouldSendMoveResponse(player, timestamp + Stopwatch.Frequency / 4));
        Assert.True(movement.ShouldSendMoveResponse(other, timestamp));
        // 간격 전에 보낸 보정 응답도 다음 응답의 기준 시각이 된다.
        movement.RecordMoveResponse(player, timestamp + Stopwatch.Frequency / 8);
        Assert.False(movement.ShouldSendMoveResponse(player, timestamp + Stopwatch.Frequency / 4));
    }
}
