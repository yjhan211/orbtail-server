using network.common.data.models;

namespace game_server.matches;

/// <summary>개체 하나의 서버 전용 경로와 다음 경유점. 매치 잠금 안에서 변경한다.</summary>
public sealed class MovementState
{
    public List<Vector3f> Waypoints { get; } = [];
    public int WaypointIndex { get; set; }

    public void Clear()
    {
        Waypoints.Clear();
        WaypointIndex = 0;
    }
}
