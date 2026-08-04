using game_server.services;
using network.common.data.models;

namespace demo_regression_tests;

public class MovementValidationPolicyTests
{
    [Fact]
    public void ReceiptDeltaIsClampedToShortAuthoritativeWindow()
    {
        Assert.Equal(MovementValidationPolicy.InitialReceiptDeltaSeconds,
            MovementValidationPolicy.ClampReceiptDeltaSeconds(double.NaN));
        Assert.Equal(MovementValidationPolicy.MinimumReceiptDeltaSeconds,
            MovementValidationPolicy.ClampReceiptDeltaSeconds(0d));
        Assert.Equal(MovementValidationPolicy.MaximumReceiptDeltaSeconds,
            MovementValidationPolicy.ClampReceiptDeltaSeconds(30d));
    }

    [Fact]
    public void NonFinitePositionOrVelocityIsRejectedAtPacketBoundary()
    {
        Assert.True(MovementValidationPolicy.IsFinite(new Vector3f(1f, 2f, 0f)));
        Assert.False(MovementValidationPolicy.IsFinite(new Vector3f(float.NaN, 2f, 0f)));
        Assert.False(MovementValidationPolicy.IsFinite(new Vector3f(1f, float.PositiveInfinity, 0f)));
    }

    [Fact]
    public void BroadcastVelocityCannotExceedAuthoritativeSpeed()
    {
        var clamped = MovementValidationPolicy.ClampVelocity(new Vector3f(30f, 40f, 0f));

        Assert.Equal(MovementValidationPolicy.MaximumSpeedUnitsPerSecond, clamped.Magnitude(), 3);
    }
}
