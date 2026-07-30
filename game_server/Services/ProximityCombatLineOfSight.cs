using network.common;
using network.common.data;
using network.common.data.models;

namespace game_server.services;

/// <summary>
///     서버 권위 자동전투의 발사 전 지역·벽 차폐 판정.
///     발사가 확정된 뒤의 유도탄은 기존 규칙대로 장애물과 무관하게 명중한다.
/// </summary>
public static class ProximityCombatLineOfSight
{
    private const int MaxCellDelta = 256;

    public static bool CanTarget(ProximityCombatActor attacker, ProximityCombatActor target)
    {
        if (attacker.MapId == MapId.None || attacker.MapId != target.MapId ||
            attacker.Cell is null || target.Cell is null)
        {
            return false;
        }

        var attackerArea = GameMapData.GetCurrentArea(attacker.MapId, attacker.Cell);
        var targetArea = GameMapData.GetCurrentArea(target.MapId, target.Cell);
        if (attackerArea == AreaType.None || targetArea == AreaType.None ||
            attackerArea != attacker.Area || targetArea != target.Area ||
            attackerArea != targetArea)
        {
            return false;
        }

        return HasClearPath(attacker.MapId, attacker.Cell, target.Cell);
    }

    public static bool HasClearPath(MapId mapId, Cell from, Cell to)
    {
        if (mapId == MapId.None)
            return false;

        int deltaX = to.X - from.X;
        int deltaY = to.Y - from.Y;
        int cellCountX = Math.Abs(deltaX);
        int cellCountY = Math.Abs(deltaY);
        if (cellCountX > MaxCellDelta || cellCountY > MaxCellDelta)
            return false;

        int stepX = Math.Sign(deltaX);
        int stepY = Math.Sign(deltaY);
        int x = from.X;
        int y = from.Y;
        int progressedX = 0;
        int progressedY = 0;

        if (!IsTransparent(mapId, x, y))
            return false;

        while (progressedX < cellCountX || progressedY < cellCountY)
        {
            long decision = (1L + 2L * progressedX) * cellCountY -
                            (1L + 2L * progressedY) * cellCountX;

            if (decision == 0)
            {
                // 정확히 타일 모서리를 지나는 선이 두 벽 셀 사이를 비집고 통과하지 못하게 한다.
                if (!IsTransparent(mapId, x + stepX, y) ||
                    !IsTransparent(mapId, x, y + stepY))
                {
                    return false;
                }

                x += stepX;
                y += stepY;
                progressedX++;
                progressedY++;
            }
            else if (decision < 0)
            {
                x += stepX;
                progressedX++;
            }
            else
            {
                y += stepY;
                progressedY++;
            }

            if (!IsTransparent(mapId, x, y))
                return false;
        }

        return true;
    }

    public static Cell WorldPositionToCell(MapId mapId, Vector3f position) =>
        MapCoordinateConverter.WorldToCell(mapId, position);

    private static bool IsTransparent(MapId mapId, int x, int y)
    {
        return GameMapData.IsMoveablePosition(mapId, new Cell(x, y));
    }
}
