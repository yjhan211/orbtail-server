using network.common.data.models;

namespace game_server.matches;

/// <summary>개체의 확정 경로와 이동 진행 상태. 매치 잠금 안에서 변경하며 목적지 갱신만으로 기존 경로를 교체하지 않는다.</summary>
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
}

/// <summary>행동 판단의 결과. 개체 종류와 경로 계획 방법은 포함하지 않는다.</summary>
public readonly record struct MovementRequest(
    Cell? DestinationCell,
    float Speed);

/// <summary>이번 틱에 공통 이동 처리를 수행할 공간 정보·경로 상태·요청과 실행 결과.</summary>
internal sealed record MovementActor(GameObjectInfo ObjectInfo, MovementState Movement, bool IgnoreClosedDoors, MovementRequest Request)
{
    public MovementResult Result { get; set; }
}

/// <summary>공통 이동 실행으로 공간 정보가 변경되었는지와 경로 끝에 도달했는지 나타낸다.</summary>
internal readonly record struct MovementResult(bool Changed, bool ReachedPathEnd);
