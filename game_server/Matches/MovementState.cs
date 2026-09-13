using network.common.data.models;

namespace game_server.matches;

/// <summary>개체 하나의 서버 전용 경로·다음 경유점과 이번 틱의 이동 명령. 매치 잠금 안에서 변경한다.</summary>
public sealed class MovementState
{
    public List<Vector3f> Waypoints { get; } = [];
    public int WaypointIndex { get; set; }

    // 매 틱 행동 판단이 설정하는 이동 명령. 0이면 이동하지 않는다.
    public float Speed { get; set; }
    public bool FollowPath { get; set; }
    public Vector3f? DodgeDirection { get; set; }
    public DateTime LastProcessedAtUtc { get; set; } = DateTime.UtcNow;
    public Vector3f? DirectTarget { get; set; }
    // 행군 종료 시 벽 안에 남은 위치를 이동 단계에서 보정한다.
    public Vector3f? PositionCorrection { get; set; }

    public void ResetIntent()
    {
        Speed = 0f;
        FollowPath = false;
        DodgeDirection = null;
        DirectTarget = null;
        PositionCorrection = null;
    }

    public void Clear()
    {
        Waypoints.Clear();
        WaypointIndex = 0;
        ResetIntent();
    }
}
