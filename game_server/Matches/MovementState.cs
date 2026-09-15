using network.common;
using network.common.data.models;

namespace game_server.matches;

/// <summary>개체의 목적지·확정 경로와 이번 틱 이동 명령. 매치 잠금 안에서 변경하며 목적지 갱신만으로 기존 경로를 교체하지 않는다.</summary>
public sealed class MovementState
{
    // 행동 판단의 목적지와 확정 경로는 별개다. ResetIntent는 둘 다 보존한다.
    public AreaType DestinationArea { get; set; }
    // 목적지는 셀로만 보관하고, 실제 이동 경로는 월드 좌표를 사용한다.
    public Cell? DestinationCell { get; set; }

    public List<Vector3f> Waypoints { get; } = [];
    public int WaypointIndex { get; set; }
    public DateTime NextPathPlanAtUtc { get; set; }
    public bool PathBlocked { get; set; }
    public bool ReachedDestination { get; set; }
    public DateTime LastProcessedAtUtc { get; set; } = DateTime.UtcNow;

    // 봇 목표 선택 중의 정지 판단. 요청을 만든 뒤 공통 준비 단계에서 비운다.
    public bool HoldPosition { get; set; }
    // 공통 준비를 통과한 이번 이동만 허용한다. 실행 후 비워 오래된 경로의 재실행을 막는다.
    public bool FollowPath { get; set; }

    public void ResetIntent()
    {
        HoldPosition = false;
        FollowPath = false;
    }

    public void Clear()
    {
        Waypoints.Clear();
        WaypointIndex = 0;
        PathBlocked = false;
        ReachedDestination = false;
        ResetIntent();
    }
}

/// <summary>행동 판단의 결과. 개체 종류와 경로 계획 방법은 포함하지 않는다.</summary>
public readonly record struct MovementRequest(
    Cell? DestinationCell,
    float Speed,
    bool HoldPosition = false,
    AreaType? StopBeforeArea = null,
    Cell? FallbackCell = null,
    Func<Cell, bool>? IsSafeCell = null);
