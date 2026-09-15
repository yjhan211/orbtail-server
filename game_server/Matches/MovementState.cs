using network.common;
using network.common.data.models;

namespace game_server.matches;

/// <summary>개체의 목적지·확정 경로. 매치 잠금 안에서 변경하며 목적지 갱신만으로 기존 경로를 교체하지 않는다.</summary>
public sealed class MovementState
{
    // 행동 판단의 목적지와 확정 경로는 별개다.
    public AreaType DestinationArea { get; set; }
    // 목적지와 경로는 셀로 보관하고, 이동 실행 시 다음 셀만 월드 좌표로 변환한다.
    public Cell? DestinationCell { get; set; }

    public List<Cell> Waypoints { get; } = [];
    public int WaypointIndex { get; set; }
    public DateTime NextPathPlanAtUtc { get; set; }
    public DateTime LastProcessedAtUtc { get; set; } = DateTime.UtcNow;

    public void Clear()
    {
        Waypoints.Clear();
        WaypointIndex = 0;
    }
}

/// <summary>행동 판단의 결과. 개체 종류와 경로 계획 방법은 포함하지 않는다.</summary>
public readonly record struct MovementRequest(
    Cell? DestinationCell,
    float Speed,
    bool HoldPosition = false,
    AreaType? StopBeforeArea = null);
