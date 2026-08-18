using network.common;
using network.common.data.models;

namespace game_server;

/// <summary>
///     봇 투사체 회피 반사 (#232 §9, 2026-08-18 유저 제보 "봇이 투사체를 안 피하고 그대로 맞고 있어").
///     봇 이동 틱(50ms)이 매 걸음 전에 묻는다: "지금 이 자리를 지나갈 태양 투사체가 있는가?"
///     있으면 그 직선의 수직으로 한 걸음 비켜선다 — 경로는 버리지 않고 다음 틱에 이어 걷는다.
///     판정 기하는 교차사격 서버 판정(바닥면 dy×2, 반폭 + 플레이어 반경)과 같다.
///     대상은 태양 투사체뿐이다: 바람 몸통박치기는 표적의 착지 순간 자리에 떨어져(호밍) 자리로는 못 피하고,
///     파도 물폭탄은 잔상 전용이다.
///     스레드: 모양 목록은 아레나 틱이 쓰고 봇 틱이 읽는다 — 아레나 틱이 갱신 때마다 불변 스냅샷을 발행하고
///     봇은 그 배열만 읽는다.
/// </summary>
public partial class GameServer
{
    // 봇이 반응하는 착탄 예상 시간 상한 — 이보다 멀면 아직 안 움직인다(예고 0.55초 + 비행이라 대개 1초 안팎).
    private const float SwarmBotDodgeHorizonSeconds = 1.5f;
    // 판정 띠 밖으로 이만큼 여유를 더 벌린다 — 띠 가장자리에서 멈추면 몸통 반경만큼 다시 맞는다.
    private const float SwarmBotDodgeMargin = 0.2f;

    public readonly record struct SwarmCrossfireDodgeThreat(
        long MatchingId,
        AreaType Area,
        long OwnerId,
        float OriginX,
        float OriginY,
        float AxisX,
        float AxisY,
        float GroundLength,
        float HalfWidth,
        float SweepSpeed,
        DateTime ArmedAtUtc,
        DateTime ExpiresAtUtc);

    private volatile SwarmCrossfireDodgeThreat[] _swarmCrossfireDodgeSnapshot = [];

    /// <summary>살아 있는 교차사격 모양을 봇 회피용 불변 스냅샷으로 발행한다 — 모양이 늘거나 줄 때마다.</summary>
    private void PublishSwarmCrossfireDodgeSnapshot()
    {
        var threats = new List<SwarmCrossfireDodgeThreat>(_swarmCrossfireShapes.Count);
        foreach (var shape in _swarmCrossfireShapes)
        {
            float ax = shape.End.X - shape.Origin.X;
            float ay = (shape.End.Y - shape.Origin.Y) * SwarmGroundYScale;
            float length = MathF.Sqrt(ax * ax + ay * ay);
            if (length < 0.01f)
                continue;
            threats.Add(new SwarmCrossfireDodgeThreat(
                shape.MatchingId, shape.Area, shape.OwnerId,
                shape.Origin.X, shape.Origin.Y, ax / length, ay / length,
                shape.GroundLength, shape.HalfWidth, shape.SweepSpeed,
                shape.ArmedAtUtc, shape.ExpiresAtUtc));
        }

        _swarmCrossfireDodgeSnapshot = threats.ToArray();
    }

    /// <summary>
    ///     이 봇이 지금 비켜서야 할 방향(월드 단위 벡터). 위협이 없으면 null.
    ///     가장 먼저 닿을 투사체 하나만 본다 — 여러 개가 겹치면 다음 틱에 다시 묻는다.
    /// </summary>
    internal Vector3f? ResolveSwarmBotDodgeDirection(
        long matchingId, long botPlayerId, Vector3f position, AreaType area, DateTime nowUtc) =>
        ResolveSwarmBotDodgeDirection(_swarmCrossfireDodgeSnapshot, matchingId, botPlayerId, position, area, nowUtc);

    /// <summary>순수 기하 — 스냅샷을 인자로 받아 테스트에서 그대로 검증한다.</summary>
    public static Vector3f? ResolveSwarmBotDodgeDirection(
        IReadOnlyList<SwarmCrossfireDodgeThreat> threats,
        long matchingId, long botPlayerId, Vector3f position, AreaType area, DateTime nowUtc)
    {
        if (threats.Count == 0)
            return null;

        float bestTime = float.MaxValue;
        Vector3f? bestDirection = null;
        foreach (var threat in threats)
        {
            if (threat.MatchingId != matchingId || threat.Area != area || threat.OwnerId == botPlayerId)
                continue;
            if (nowUtc >= threat.ExpiresAtUtc)
                continue;

            float rx = position.X - threat.OriginX;
            float ry = (position.Y - threat.OriginY) * SwarmGroundYScale;
            float along = rx * threat.AxisX + ry * threat.AxisY;
            float perp = rx * -threat.AxisY + ry * threat.AxisX;
            float band = threat.HalfWidth + SwarmCrossfirePlayerRadius + SwarmBotDodgeMargin;
            if (MathF.Abs(perp) > band)
                continue;
            if (along < -threat.HalfWidth - SwarmCrossfirePlayerRadius ||
                along > threat.GroundLength + threat.HalfWidth + SwarmCrossfirePlayerRadius)
                continue;

            // 앞머리 위치: 예고 중이면 원점 앞 캡, 발동 뒤면 속도 × 경과.
            double sinceArmed = (nowUtc - threat.ArmedAtUtc).TotalSeconds;
            float front = sinceArmed <= 0d
                ? -threat.HalfWidth
                : -threat.HalfWidth + (float)sinceArmed * threat.SweepSpeed;
            // 이미 지나간 투사체는 위협이 아니다.
            if (front > along + SwarmCrossfirePlayerRadius)
                continue;

            float timeToHit = (float)Math.Max(0d, -sinceArmed) +
                              Math.Max(0f, along - front) / Math.Max(0.01f, threat.SweepSpeed);
            if (timeToHit > SwarmBotDodgeHorizonSeconds || timeToHit >= bestTime)
                continue;

            bestTime = timeToHit;
            // 이미 치우친 쪽으로 빠진다 — 축 위에 딱 서 있으면 봇 id 홀짝으로 갈라 무리가 한쪽으로 몰리지 않게.
            float side = MathF.Abs(perp) < 0.02f ? ((botPlayerId & 1) == 0 ? 1f : -1f) : MathF.Sign(perp);
            // 바닥면 수직 (-ay, ax) → 월드로 되돌린다 (Y는 ÷2).
            float wx = -threat.AxisY * side;
            float wy = threat.AxisX * side / SwarmGroundYScale;
            float wl = MathF.Sqrt(wx * wx + wy * wy);
            if (wl < 0.001f)
                continue;
            bestDirection = new Vector3f(wx / wl, wy / wl, 0f);
        }

        return bestDirection;
    }
}
