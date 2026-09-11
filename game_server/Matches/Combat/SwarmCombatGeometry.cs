using network.common.data.models;

namespace game_server.matches.combat;

/// <summary>오브 공격과 봇 회피가 공유하는 바닥면 기하. 화면 Y축을 보정해 반경을 비교한다.</summary>
internal static class SwarmCombatGeometry
{
    /// <summary>아이소메트릭 바닥면 Y 배율. 판정과 회피가 같은 값을 써야 한다.</summary>
    public const float GroundYScale = 2f;
    /// <summary>플레이어 몸통 반경. 교차사격·칼날·소용돌이 판정과 회피 공용.</summary>
    public const float PlayerRadius = 0.25f;
    public const float MonsterRadius = 0.3f;

    /// <summary>꼬리 절단의 오브 판정 타원 반경. 가로 좁고 세로 후하게 잡아 이웃 오브 오차와 부양 스프라이트를 보정한다.</summary>
    private const float OrbHitRadiusX = 0.42f;
    private const float OrbHitRadiusY = 0.42f;

    public static bool IsWithinGroundRadius(Vector3f center, Vector3f point, float radius)
    {
        float dx = point.X - center.X;
        float dy = (point.Y - center.Y) * GroundYScale;
        return dx * dx + dy * dy <= radius * radius;
    }

    /// <summary>점이 오브 판정 타원 안에 있는지 — 래치 이탈 재무장 판정.</summary>
    public static bool IsInsideOrbHitEllipse(Vector3f point, Vector3f orbHitPoint)
    {
        float dx = (point.X - orbHitPoint.X) / OrbHitRadiusX;
        float dy = (point.Y * GroundYScale - orbHitPoint.Y * GroundYScale) / OrbHitRadiusY;
        return dx * dx + dy * dy <= 1f;
    }

    /// <summary>이동 선분이 오브(점)를 관통했는지 — 정규화(Y×2) 공간의 최근접점을 타원으로 판정한다.</summary>
    public static bool TrySegmentHitsPoint(Vector3f from, Vector3f to, Vector3f point, out float t)
    {
        float ax = from.X;
        float ay = from.Y * GroundYScale;
        float abx = to.X - ax;
        float aby = to.Y * GroundYScale - ay;
        float px = point.X;
        float py = point.Y * GroundYScale;
        float lengthSquared = abx * abx + aby * aby;
        t = lengthSquared > 0.000001f
            ? Math.Clamp(((px - ax) * abx + (py - ay) * aby) / lengthSquared, 0f, 1f)
            : 0f;
        float dx = (ax + abx * t - px) / OrbHitRadiusX;
        float dy = (ay + aby * t - py) / OrbHitRadiusY;
        return dx * dx + dy * dy <= 1f;
    }

    /// <summary>선분 교차 판정 — t는 첫 선분 위의 교차 지점 비율.</summary>
    public static bool TrySegmentIntersection(Vector3f a1, Vector3f a2, Vector3f b1, Vector3f b2, out float t)
    {
        t = 0f;
        float rx = a2.X - a1.X;
        float ry = a2.Y - a1.Y;
        float sx = b2.X - b1.X;
        float sy = b2.Y - b1.Y;
        float denominator = rx * sy - ry * sx;
        if (MathF.Abs(denominator) < 0.000001f)
            return false;

        float qpx = b1.X - a1.X;
        float qpy = b1.Y - a1.Y;
        t = (qpx * sy - qpy * sx) / denominator;
        float u = (qpx * ry - qpy * rx) / denominator;
        return t >= 0f && t <= 1f && u >= 0f && u <= 1f;
    }
}
