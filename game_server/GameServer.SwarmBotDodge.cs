using game_server.services;
using network.common;
using network.common.data.models;

namespace game_server;

/// <summary>
///     봇 투사체 회피 — 스냅샷 발행부. 판정(순수 기하)은 Services/SwarmBotDodgePolicy가 담당한다 (#297).
///     스레드: 모양 목록은 아레나 틱이 쓰고 봇 틱이 읽는다 — 아레나 틱이 갱신 때마다 불변 스냅샷을 발행하고
///     봇은 그 배열만 읽는다.
/// </summary>
public partial class GameServer
{
    /// <summary>살아 있는 교차사격 모양을 봇 회피용 불변 스냅샷으로 발행한다 — 모양이 늘거나 줄 때마다.</summary>
    private void PublishSwarmCrossfireDodgeSnapshot()
    {
        var threats = new List<SwarmBotDodgePolicy.SwarmCrossfireDodgeThreat>(_swarmCrossfireShapes.Count);
        foreach (var shape in _swarmCrossfireShapes)
        {
            float ax = shape.End.X - shape.Origin.X;
            float ay = (shape.End.Y - shape.Origin.Y) * SwarmGroundYScale;
            float length = MathF.Sqrt(ax * ax + ay * ay);
            if (length < 0.01f)
                continue;
            threats.Add(new SwarmBotDodgePolicy.SwarmCrossfireDodgeThreat(
                shape.MatchingId, shape.Area, shape.OwnerId,
                shape.Origin.X, shape.Origin.Y, ax / length, ay / length,
                shape.GroundLength, shape.HalfWidth, shape.SweepSpeed,
                shape.ArmedAtUtc, shape.ExpiresAtUtc));
        }

        _swarmCrossfireDodgeSnapshot = threats.ToArray();
    }

    /// <summary>이 봇이 지금 비켜서야 할 방향 — 최신 스냅샷 기준 (BotPlayerManager에 배선).</summary>
    internal SwarmBotDodgeAdvice? ResolveSwarmBotDodgeDirection(
        long matchingId, long botPlayerId, Vector3f position, AreaType area, DateTime nowUtc) =>
        SwarmBotDodgePolicy.ResolveSwarmBotDodgeDirection(
            _swarmCrossfireDodgeSnapshot, matchingId, botPlayerId, position, area, nowUtc);
}
