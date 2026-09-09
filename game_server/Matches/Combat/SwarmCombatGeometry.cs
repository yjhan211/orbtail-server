using game_server.matches.bots;
using network.common.data.models;

namespace game_server.matches.combat;

/// <summary>오브 공격이 공유하는 바닥면 거리 판정. 화면 Y축을 보정해 반경을 비교한다.</summary>
internal static class SwarmCombatGeometry
{
    public const float MonsterRadius = 0.3f;

    public static bool IsWithinGroundRadius(Vector3f center, Vector3f point, float radius)
    {
        float dx = point.X - center.X;
        float dy = (point.Y - center.Y) * SwarmBotDodgePolicy.SwarmGroundYScale;
        return dx * dx + dy * dy <= radius * radius;
    }
}
