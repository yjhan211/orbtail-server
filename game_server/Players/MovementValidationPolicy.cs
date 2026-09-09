using network.common.data.models;

namespace game_server.players;

/// <summary>
///     Movement packet hardening rules shared by the authoritative session path and regression tests.
/// </summary>
public static class MovementValidationPolicy
{
    public const float InitialReceiptDeltaSeconds = 0.05f;
    public const float MinimumReceiptDeltaSeconds = 0f;
    public const float MaximumReceiptDeltaSeconds = 0.25f;
    public const float MaximumSpeedUnitsPerSecond = 10f;
    public const float MovementAcknowledgementIntervalSeconds = 0.25f;

    public static float ClampReceiptDeltaSeconds(double elapsedSeconds)
    {
        if (double.IsNaN(elapsedSeconds) || double.IsInfinity(elapsedSeconds))
            return InitialReceiptDeltaSeconds;

        return Math.Clamp((float)elapsedSeconds, MinimumReceiptDeltaSeconds, MaximumReceiptDeltaSeconds);
    }

    public static bool IsFinite(Vector3f value) =>
        float.IsFinite(value.X) && float.IsFinite(value.Y) && float.IsFinite(value.Z);

    public static Vector3f ClampVelocity(Vector3f velocity)
    {
        float speed = velocity.Magnitude();
        return speed > MaximumSpeedUnitsPerSecond
            ? velocity.Normalized() * MaximumSpeedUnitsPerSecond
            : velocity;
    }
}
