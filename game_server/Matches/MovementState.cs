using network.common;
using network.common.data.models;

namespace game_server.matches;

/// <summary>이번 틱의 경로 계획 요청. None이면 확정된 경로를 그대로 사용한다.</summary>
public enum MovementPathRequest
{
    None,
    CellPath, // 셀 중심 경로 (봇)
    Detour, // 직선 이동이 막힌 경우 주기적으로 우회 계획
    WorldPath // 정확한 월드 목적지까지 경로 계획
}

/// <summary>개체의 목적지·확정 경로와 이번 틱 이동 명령. 매치 잠금 안에서 변경하며 목적지 갱신만으로 기존 경로를 교체하지 않는다.</summary>
public sealed class MovementState
{
    // 행동 판단의 목적지와 확정 경로는 별개다. ResetIntent는 둘 다 보존한다.
    public AreaType DestinationArea { get; set; }
    // 셀 목표(봇)와 정밀 월드 목표(몬스터)는 서로 다른 입력이다.
    public Cell? DestinationCell { get; set; }
    public Vector3f? Destination { get; set; }

    public List<Vector3f> Waypoints { get; } = [];
    public int WaypointIndex { get; set; }
    public DateTime NextPathPlanAtUtc { get; set; }
    public DateTime LastProcessedAtUtc { get; set; } = DateTime.UtcNow;

    // 아래 명령은 한 번 실행한 뒤 비운다. 목적지만 남아 있어도 이동하지 않는다.
    // 도주 불가·공격 사거리 내 대기 등 행동 판단이 명시적으로 요청한 정지.
    public bool HoldPosition { get; set; }
    public float Speed { get; set; }
    public bool FollowPath { get; set; }
    public Vector3f? DodgeDirection { get; set; }
    public bool MoveToDestination { get; set; }
    public MovementPathRequest PathRequest { get; set; }
    // 이번 틱에는 해당 구역 경계 전에 멈춘다 (예: 입장 카운트다운).
    public AreaType? StopBeforeArea { get; set; }

    public void ResetIntent()
    {
        HoldPosition = false;
        Speed = 0f;
        FollowPath = false;
        DodgeDirection = null;
        MoveToDestination = false;
        PathRequest = MovementPathRequest.None;
        StopBeforeArea = null;
    }

    public void Clear()
    {
        Waypoints.Clear();
        WaypointIndex = 0;
        ResetIntent();
    }
}
