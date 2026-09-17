using game_server.players;
using network.common.data.models;

namespace game_server.matches;

public sealed class MovementState
{
    public List<Cell> Waypoints { get; } = [];
    public int WaypointIndex { get; set; }
    public DateTime NextPathPlanAtUtc { get; set; }
    public DateTime LastProcessedAtUtc { get; set; } = DateTime.UtcNow;

    public void Clear()
    {
        Waypoints.Clear();
        WaypointIndex = 0;
    }

    public void ClearAndReplan()
    {
        Clear();
        NextPathPlanAtUtc = DateTime.MinValue;
    }
}

public readonly record struct MovementRequest(
    Cell? DestinationCell,
    float Speed);

internal sealed record MovementActor(GameObjectInfo ObjectInfo, MovementState Movement, bool IgnoreClosedDoors, MovementRequest Request, Player? Player = null)
{
    public MovementResult Result { get; set; }
}

internal readonly record struct MovementResult(bool Changed, bool ReachedPathEnd);
