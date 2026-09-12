using game_server.matches;
using network.common;
using network.common.data;
using network.common.data.models;

namespace game_server.players.bots;

public readonly record struct BotDodgeResult(float DirectionX, float DirectionY, float HoldSeconds);

public static class BotDodgeCalculator
{
    private const float SwarmBotDodgeHorizonSeconds = 1.5f;
    private const float SwarmBotDodgeMargin = 0.2f;
    private const float SwarmBotDodgeHoldSlackSeconds = 0.15f;

    public static BotDodgeResult? CalculateDodge(IReadOnlyList<SwarmCrossfireShape> shapes, long botPlayerId, Vector3f position, AreaType area, DateTime nowUtc)
    {
        if (shapes.Count == 0)
        {
            return null;
        }

        float bestTime = float.MaxValue;
        BotDodgeResult? bestDirection = null;
        foreach (var shape in shapes)
        {
            if (shape.Area != area || shape.OwnerId == botPlayerId)
            {
                continue;
            }

            if (nowUtc >= shape.ExpiresAtUtc)
            {
                continue;
            }

            float axisX = shape.End.X - shape.Origin.X;
            float axisY = (shape.End.Y - shape.Origin.Y) * GroundGeometry.GroundYScale;
            float axisLength = MathF.Sqrt(axisX * axisX + axisY * axisY);
            if (axisLength < 0.01f)
            {
                continue;
            }
            axisX /= axisLength;
            axisY /= axisLength;

            float rx = position.X - shape.Origin.X;
            float ry = (position.Y - shape.Origin.Y) * GroundGeometry.GroundYScale;
            float along = rx * axisX + ry * axisY;
            float perp = rx * -axisY + ry * axisX;
            float band = shape.HalfWidth + GroundGeometry.PlayerRadius + SwarmBotDodgeMargin;
            if (MathF.Abs(perp) > band)
            {
                continue;
            }

            if (along < -shape.HalfWidth - GroundGeometry.PlayerRadius || along > shape.GroundLength + shape.HalfWidth + GroundGeometry.PlayerRadius)
            {
                continue;
            }

            double sinceArmed = (nowUtc - shape.ArmedAtUtc).TotalSeconds;
            float front = sinceArmed <= 0d ? -shape.HalfWidth : -shape.HalfWidth + (float)sinceArmed * shape.SweepSpeed;
            if (front > along + GroundGeometry.PlayerRadius)
            {
                continue;
            }

            float timeToHit = (float)Math.Max(0d, -sinceArmed) + Math.Max(0f, along - front) / Math.Max(0.01f, shape.SweepSpeed);
            if (timeToHit > SwarmBotDodgeHorizonSeconds || timeToHit >= bestTime)
            {
                continue;
            }

            bestTime = timeToHit;
            float side = MathF.Abs(perp) < 0.02f ? ((botPlayerId & 1) == 0 ? 1f : -1f) : MathF.Sign(perp);
            float wx = -axisY * side;
            float wy = axisX * side / GroundGeometry.GroundYScale;
            float wl = MathF.Sqrt(wx * wx + wy * wy);
            if (wl < 0.001f)
            {
                continue;
            }
            float holdSeconds = timeToHit + (2f * GroundGeometry.PlayerRadius) / Math.Max(0.01f, shape.SweepSpeed) + SwarmBotDodgeHoldSlackSeconds;
            bestDirection = new BotDodgeResult(wx / wl, wy / wl, holdSeconds);
        }

        return bestDirection;
    }
}
