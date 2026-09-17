using game_server.players;
using network.common.data.models;

namespace server_tests;

public class MovementValidationPolicyTests
{
    [Fact]
    public void ReceiptDeltaIsClampedToShortAuthoritativeWindow()
    {
        Assert.Equal(PlayerMovementService.InitialReceiptDeltaSeconds,
            PlayerMovementService.ClampMoveDeltaTime(double.NaN));
        Assert.Equal(0f,
            PlayerMovementService.ClampMoveDeltaTime(0d));
        Assert.Equal(0.25f,
            PlayerMovementService.ClampMoveDeltaTime(30d));
    }

    [Fact]
    public void BurstPackets_DoNotReceiveAnArtificialMinimumTimeBudget()
    {
        Assert.Equal(0f, PlayerMovementService.ClampMoveDeltaTime(0d));
        Assert.Equal(0f, PlayerMovementService.ClampMoveDeltaTime(-1d));
        Assert.Equal(0.001f, PlayerMovementService.ClampMoveDeltaTime(0.001d), 6);
        float total = 0f;
        for (int i = 0; i < 100; i++)
            total += PlayerMovementService.ClampMoveDeltaTime(0.001d);
        Assert.Equal(0.1f, total, 5);
    }

    [Fact]
    public void NonFinitePositionOrVelocityIsRejectedAtPacketBoundary()
    {
        Assert.True(PlayerMovementService.IsFinite(new Vector3f(1f, 2f, 0f)));
        Assert.False(PlayerMovementService.IsFinite(new Vector3f(float.NaN, 2f, 0f)));
        Assert.False(PlayerMovementService.IsFinite(new Vector3f(1f, float.PositiveInfinity, 0f)));
    }

}
