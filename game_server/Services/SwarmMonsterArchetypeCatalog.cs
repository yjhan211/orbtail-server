using network.common.data;

namespace game_server.services;

/// <summary>
///     Provides CSV-backed combat statistics and semantic classifications for every swarm monster archetype.
///     Match runtime state and movement remain owned by <see cref="SwarmMonsterDirector"/>.
/// </summary>
internal static class SwarmMonsterArchetypeCatalog
{
    public const int DefaultMonsterMaxHealth = 12;
    public const float DefaultContactRadius = 0.32f;
    public const float DefaultContactCooldownSeconds = 1f;

    public static bool IsWavePattern(SwarmPattern pattern) => pattern == SwarmPattern.Encircle;

    public static (int MaxHp, int OrbDamage, float AttackRange, float AttackCooldownSeconds, int StoneReward,
        int HeartReward, int BootsReward, int KeyReward)
        GetStats(SwarmMonsterKind kind)
    {
        SwarmMonsterDefinition? definition =
            SwarmMonsterData.Get((int)kind) ?? SwarmMonsterData.Get((int)SwarmMonsterKind.Skeleton);
        if (definition == null)
        {
            return (DefaultMonsterMaxHealth, 1, DefaultContactRadius,
                DefaultContactCooldownSeconds, 1, 0, 0, 0);
        }

        float attackRange = definition.AttackRange > 0f
            ? definition.AttackRange
            : DefaultContactRadius;
        return (definition.MaxHp, definition.OrbDamage, attackRange, definition.AttackCooldownSeconds,
            definition.StoneReward, definition.HeartReward, definition.BootsReward, definition.KeyReward);
    }

    public static float GetContactRadius(SwarmMonsterKind kind) =>
        DefaultContactRadius * (SwarmMonsterData.Get((int)kind)?.ContactRadiusScale ?? 1f);
}
