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
    public void BurstPackets_DoNotReceiveAnArtificialMinimumTimeBudget()
    {
        Assert.Equal(0f, MovementValidationPolicy.ClampReceiptDeltaSeconds(0d));
        Assert.Equal(0f, MovementValidationPolicy.ClampReceiptDeltaSeconds(-1d));
        Assert.Equal(0.001f, MovementValidationPolicy.ClampReceiptDeltaSeconds(0.001d), 6);
        float total = 0f;
        for (int i = 0; i < 100; i++)
            total += MovementValidationPolicy.ClampReceiptDeltaSeconds(0.001d);
        Assert.Equal(0.1f, total, 5);
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
