using network.common;
using network.common.data.models;

namespace game_server.matches;

/// <summary>개체 하나의 서버 전용 경로·다음 경유점과 이번 틱의 이동 명령. 매치 잠금 안에서 변경한다.</summary>
public sealed class MovementState
{
    public AreaType DestinationArea { get; set; }
    public Cell DestinationCell { get; set; } = new(0, 0);

    public List<Vector3f> Waypoints { get; } = [];
    public int WaypointIndex { get; set; }
    public DateTime NextPathPlanAtUtc { get; set; }
    public float Speed { get; set; }
    public bool FollowPath { get; set; }
    public Vector3f? DodgeDirection { get; set; }
    public DateTime LastProcessedAtUtc { get; set; } = DateTime.UtcNow;
    public Vector3f? DirectTarget { get; set; }
    public bool PlanPathToTarget { get; set; }
    public Vector3f? PositionCorrection { get; set; }

    public void ResetIntent()
    {
        Speed = 0f;
        FollowPath = false;
        DodgeDirection = null;
        DirectTarget = null;
        PlanPathToTarget = false;
        PositionCorrection = null;
    }

    public void Clear()
    {
        Waypoints.Clear();
        WaypointIndex = 0;
        ResetIntent();
    }
}
