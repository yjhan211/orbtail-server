using game_server.players.bots;
using network.common;
using network.common.data;
using network.common.data.models;

namespace game_server.matches.monsters;

internal static class MonsterNavigation
{
    public static float MonsterMoveSpeed => SwarmConfigData.GetFloat("SWARM_MONSTER_MOVE_SPEED", 4.2f);
    private const float RouteSampleStep = 0.35f;
    private const int DoorwayBlockedSampleTolerance = 10;
    private const double MarchBudgetSlackMultiplier = 1.8d;
    private const double MarchBudgetMinimumSeconds = 8d;

    internal static double ComputeMarchBudgetSeconds(Vector3f start, IReadOnlyList<Vector3f> route)
    {
        double length = 0d;
        var previous = start;
        for (int index = 0; index < route.Count; index++)
        {
            float dx = route[index].X - previous.X;
            float dy = route[index].Y - previous.Y;
            length += MathF.Sqrt(dx * dx + dy * dy);
            previous = route[index];
        }

        return Math.Max(MarchBudgetMinimumSeconds, length / MonsterMoveSpeed * MarchBudgetSlackMultiplier);
    }

    internal static bool TryPlanRoute(AreaType fromArea, Vector3f from, AreaType toArea, Vector3f to, Func<AreaType, bool>? isAreaBlocked, out List<Vector3f> route)
    {
        route = null!;
        var steps = BotPathfinder.FindPath(Config.SWARM_MATCH_MAP, fromArea, MapCoordinateConverter.WorldToCell(Config.SWARM_MATCH_MAP, from), toArea, MapCoordinateConverter.WorldToCell(Config.SWARM_MATCH_MAP, to), isAreaBlocked);
        if (steps == null || steps.Count == 0)
        {
            return false;
        }

        var planned = new List<Vector3f>(steps.Count + 2) { from };
        foreach (var step in steps)
        {
            planned.Add(MapCoordinateConverter.CellToWorld(Config.SWARM_MATCH_MAP, step.Cell));
        }
        planned.Add(to);

        for (int index = 1; index < planned.Count; index++)
        {
            if (!IsSegmentWalkable(planned[index - 1], planned[index]))
            {
                return false;
            }
        }
        planned.RemoveAt(0);
        route = planned;
        return true;
    }

    internal static bool IsSegmentWalkable(Vector3f from, Vector3f to)
    {
        float dx = to.X - from.X;
        float dy = to.Y - from.Y;
        float distance = MathF.Sqrt(dx * dx + dy * dy);
        int samples = Math.Max(1, (int)MathF.Ceiling(distance / RouteSampleStep));
        int blockedRun = 0;
        for (int index = 1; index <= samples; index++)
        {
            float t = index / (float)samples;
            var point = new Vector3f(from.X + dx * t, from.Y + dy * t, 0f);
            if (GameMapData.IsMoveablePosition(Config.SWARM_MATCH_MAP, MapCoordinateConverter.WorldToCell(Config.SWARM_MATCH_MAP, point)))
            {
                blockedRun = 0;
                continue;
            }

            if (++blockedRun > DoorwayBlockedSampleTolerance)
            {
                return false;
            }
        }

        return true;
    }

    private static bool IsWalkableInArea(Vector3f position, AreaType area)
    {
        var cell = MapCoordinateConverter.WorldToCell(Config.SWARM_MATCH_MAP, position);
        return GameMapData.IsMoveablePosition(Config.SWARM_MATCH_MAP, cell) && GameMapData.GetCurrentArea(Config.SWARM_MATCH_MAP, cell) == area;
    }

    internal static Vector3f ClampToAreaWalkable(Vector3f position, Vector3f center, AreaType area)
    {
        if (IsWalkableInArea(position, area))
        {
            return position;
        }

        for (float t = 0.1f; t <= 1f; t += 0.1f)
        {
            var candidate = new Vector3f(position.X + (center.X - position.X) * t, position.Y + (center.Y - position.Y) * t, 0f);
            if (IsWalkableInArea(candidate, area))
            {
                return candidate;
            }
        }

        return center;
    }

    internal static Vector3f ClampToWalkable(Vector3f position, Vector3f center)
    {
        if (GameMapData.IsMoveablePosition(Config.SWARM_MATCH_MAP, MapCoordinateConverter.WorldToCell(Config.SWARM_MATCH_MAP, position)))
        {
            return position;
        }

        for (float t = 0.1f; t <= 1f; t += 0.1f)
        {
            var candidate = new Vector3f(position.X + (center.X - position.X) * t, position.Y + (center.Y - position.Y) * t, 0f);
            if (GameMapData.IsMoveablePosition(Config.SWARM_MATCH_MAP, MapCoordinateConverter.WorldToCell(Config.SWARM_MATCH_MAP, candidate)))
            {
                return candidate;
            }
        }
        return center;
    }
}
