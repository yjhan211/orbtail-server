using network.common;
using network.common.data;
using network.common.data.models;

namespace game_server.matches;

/// <summary>봇·몬스터의 경로 진행과 문 통과를 처리한다. 개체별 경로는 MovementState가 소유한다.</summary>
internal sealed class MatchMovementService
{
    public Vector3f Move(MatchRuntime runtime, MovementState movement, Vector3f position,
        float distanceBudget, bool ignoreClosedDoors = false,
        Func<Vector3f, bool>? canEnter = null)
    {
        if (!Monitor.IsEntered(runtime.MatchLock))
        {
            throw new InvalidOperationException("Movement requires the match lock.");
        }
        if (runtime.IsEnded)
        {
            throw new InvalidOperationException("Cannot move after the match has ended.");
        }
        var mapId = Config.SWARM_MATCH_MAP;
        var route = movement.Waypoints;
        int index = movement.WaypointIndex;
        var current = new Vector3f(position.X, position.Y, 0f);
        while (distanceBudget > 0f && index < route.Count)
        {
            var target = route[index];
            float dx = target.X - current.X;
            float dy = target.Y - current.Y;
            float distance = MathF.Sqrt(dx * dx + dy * dy);
            if (distance == 0f)
            {
                index++;
                continue;
            }

            var fromCell = MapCoordinateConverter.WorldToCell(mapId, current);
            var targetCell = MapCoordinateConverter.WorldToCell(mapId, target);
            var fromArea = GameMapData.GetCurrentArea(mapId, fromCell);
            var targetArea = GameMapData.GetCurrentArea(mapId, targetCell);
            var door = GameDoorData.GetDoorForTransition(fromArea, targetArea, fromCell, targetCell);
            if (door != null && !ignoreClosedDoors && !runtime.Doors.IsDoorOpen(door.DoorId))
            {
                break;
            }
            if (canEnter != null && !canEnter(target))
            {
                break;
            }
            if (!MapPathfinder.IsSegmentWalkable(mapId, current, target))
            {
                break;
            }

            float step = Math.Min(distanceBudget, distance);
            var next = Vector3f.MoveTowardsXY(current, target, step);
            if (canEnter != null && !canEnter(next))
            {
                break;
            }
            current = next;
            distanceBudget -= step;
            if (step == distance)
            {
                current = new Vector3f(target.X, target.Y, 0f);
                index++;
            }
        }
        movement.WaypointIndex = index;
        return current;
    }

}
