using game_server.players;
using System.Diagnostics;
using Microsoft.Extensions.Logging.Abstractions;

namespace demo_regression_tests;

public sealed class PlayerMovementTimingTests
{
    private static PlayerMovementService Create()
    {
        var session = TestGameSessionServices.CreateRecipientSession();
        TestGameSessionServices.BindMatch(session, 1);
        return session.PlayerMovement;
    }

    [Fact]
    public void MoveDelta_UsesPreviousProcessingTimeAndClampsLongGaps()
    {
        var movement = Create();
        long timestamp = Stopwatch.Frequency;
        Assert.Equal(0.05f, movement.CalculateMoveDeltaTime(timestamp));
        Assert.Equal(0.125f, movement.CalculateMoveDeltaTime(timestamp + Stopwatch.Frequency / 8));
        Assert.Equal(0f, movement.CalculateMoveDeltaTime(timestamp + Stopwatch.Frequency / 8));
        Assert.Equal(0.25f, movement.CalculateMoveDeltaTime(timestamp * 3));
    }

    [Fact]
    public void ResponseInterval_IsRecordedSeparatelyAndIsolatedPerPlayer()
    {
        var movement = Create();
        var other = Create();
        long timestamp = Stopwatch.Frequency;
        Assert.True(movement.ShouldSendMoveResponse(timestamp));
        movement.RecordMoveResponse(timestamp);
        Assert.False(movement.ShouldSendMoveResponse(timestamp + Stopwatch.Frequency / 8));
        Assert.True(movement.ShouldSendMoveResponse(timestamp + Stopwatch.Frequency / 4));
        Assert.True(other.ShouldSendMoveResponse(timestamp));
        // 간격 전에 보낸 보정 응답도 다음 응답의 기준 시각이 된다.
        movement.RecordMoveResponse(timestamp + Stopwatch.Frequency / 8);
        Assert.False(movement.ShouldSendMoveResponse(timestamp + Stopwatch.Frequency / 4));
    }
}
