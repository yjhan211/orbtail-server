using network.common.data.models;

namespace game_server.services;

/// <summary>
///     Movement packet hardening rules shared by the authoritative session path and regression tests.
/// </summary>
public static class MovementValidationPolicy
{
    public const float InitialReceiptDeltaSeconds = 0.05f;
    public const float MinimumReceiptDeltaSeconds = 0.01f;
    public const float MaximumReceiptDeltaSeconds = 0.25f;
    public const float MaximumSpeedUnitsPerSecond = 10f;
    public const float MovementAcknowledgementIntervalSeconds = 0.25f;

    // 50ms 전송이 지터로 몰려도 마지막 이동은 버리지 않고 이 간격 뒤에 처리한다.
    public static readonly TimeSpan MinimumMovementInterval = TimeSpan.FromMilliseconds(10);

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
