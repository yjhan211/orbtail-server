using network.common.data.models;

namespace game_server.combat;

/// <summary>오브 공격과 봇 회피가 공유하는 바닥면 기하. 화면 Y축을 보정해 반경을 비교한다.</summary>
internal static class SwarmCombatGeometry
{
    /// <summary>아이소메트릭 바닥면 Y 배율. 판정과 회피가 같은 값을 써야 한다.</summary>
    public const float GroundYScale = 2f;
    /// <summary>플레이어 몸통 반경. 교차사격·칼날·소용돌이 판정과 회피 공용.</summary>
    public const float PlayerRadius = 0.25f;
    public const float MonsterRadius = 0.3f;

    public static bool IsWithinGroundRadius(Vector3f center, Vector3f point, float radius)
    {
        float dx = point.X - center.X;
        float dy = (point.Y - center.Y) * GroundYScale;
        return dx * dx + dy * dy <= radius * radius;
    }
}
