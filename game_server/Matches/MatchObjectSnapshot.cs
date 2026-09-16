using network.common;
using network.common.data.models;

namespace game_server.matches;

/// <summary>이전 틱의 이동 비교 값. 가변 좌표 객체를 참조하지 않는다.</summary>
internal readonly struct MatchObjectSnapshot(GameObjectInfo info)
{
    private readonly MapId _mapId = info.MapId;
    private readonly int _cellX = info.Cell.X;
    private readonly int _cellY = info.Cell.Y;
    public AreaType Area => network.common.data.GameMapData.GetCurrentArea(_mapId, _cellX, _cellY);
    private readonly float _positionX = info.Position.X;
    private readonly float _positionY = info.Position.Y;
    private readonly float _positionZ = info.Position.Z;
    private readonly float _velocityX = info.Velocity.X;
    private readonly float _velocityY = info.Velocity.Y;
    private readonly float _velocityZ = info.Velocity.Z;
    private readonly float _rotation = info.Rotation;

    public bool Matches(GameObjectInfo current)
    {
        // 기존 Vector3f.Equals의 허용 오차를 유지한다. 회전은 기존처럼 정확히 비교한다.
        const float epsilon = 0.00001f;
        return Area == current.Area &&
            Math.Abs(_positionX - current.Position.X) < epsilon &&
            Math.Abs(_positionY - current.Position.Y) < epsilon &&
            Math.Abs(_positionZ - current.Position.Z) < epsilon &&
            Math.Abs(_velocityX - current.Velocity.X) < epsilon &&
            Math.Abs(_velocityY - current.Velocity.Y) < epsilon &&
            Math.Abs(_velocityZ - current.Velocity.Z) < epsilon &&
            _rotation.Equals(current.Rotation);
    }
}
