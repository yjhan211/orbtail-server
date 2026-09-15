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

    // 목적지는 공격 띠 밖에서 고르고, 이동 경로에는 일반 통행 규칙을 적용한다.
    internal static bool TrySelectDodgeCell(MatchRuntime runtime, Bot bot, DateTime now,
        out Cell target)
    {
        var map = Config.SWARM_MATCH_MAP;
        var area = bot.Player.CurrentArea;
        var current = bot.Player.Cell!;
        var shapes = runtime.SunCrossfireShapes
            .Where(shape => shape.Area == area && shape.OwnerId != bot.PlayerId && now < shape.ExpiresAtUtc)
            .ToArray();
        double safeDistance = runtime.Closures.GetSafeDistance(now);
        bool Outside(Cell cell)
        {
            var position = MapCoordinateConverter.CellToWorld(map, cell);
            return shapes.All(shape => GetBandDepth(shape, position) <= 0f);
        }
        if (now < bot.SwarmDodgeHoldUntilUtc && bot.DodgeTargetCell == current && !runtime.Closures.IsAreaClosed(area) && SwarmPressureField.GetDistance(current) <= safeDistance && Outside(current))
        {
            target = current.Clone();
            return true;
        }
        var candidates = new List<Cell>();
        if (now < bot.SwarmDodgeHoldUntilUtc && bot.DodgeTargetCell is { } held && Outside(held))
            candidates.Add(held);
        // 즉시 회피는 가까운 셀부터 검사한다. 이동 자체는 공통 경로 실행에 맡긴다.
        for (int radius = 1; radius <= 6; radius++)
        {
            for (int x = -radius; x <= radius; x++)
            {
                for (int y = -radius; y <= radius; y++)
                {
                    if (Math.Max(Math.Abs(x), Math.Abs(y)) != radius) continue;
                    var cell = new Cell(current.X + x, current.Y + y);
                    if (GameMapData.IsMoveablePosition(map, cell) && Outside(cell))
                        candidates.Add(cell);
                }
            }
        }
        return MatchMoveService.TrySelectReachableCell(runtime, bot.Player.GameInfo.ObjectInfo,
            candidates, cell => GameMapData.GetCurrentArea(map, cell) == area && SwarmPressureField.GetDistance(cell) <= safeDistance, candidateArea => candidateArea != area || runtime.Closures.IsAreaClosed(candidateArea),
            out target);
    }

    private static float GetBandDepth(SwarmCrossfireShape shape, Vector3f position)
    {
        float dx = shape.End.X - shape.Origin.X;
        float dy = (shape.End.Y - shape.Origin.Y) * GroundGeometry.GroundYScale;
        float length = MathF.Sqrt(dx * dx + dy * dy);
        if (length < 0.01f) return 0f;
        dx /= length;
        dy /= length;
        float rx = position.X - shape.Origin.X;
        float ry = (position.Y - shape.Origin.Y) * GroundGeometry.GroundYScale;
        float along = rx * dx + ry * dy;
        float radius = shape.HalfWidth + GroundGeometry.PlayerRadius + SwarmBotDodgeMargin;
        if (along < -radius || along > shape.GroundLength + radius) return 0f;
        return Math.Max(0f, radius - MathF.Abs(rx * -dy + ry * dx));
    }
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
