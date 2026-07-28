using network.common.data;

namespace game_server.services;

public static class EmotionAfterimagePveCombatRules
{
    public static bool IsWaveOrb(int weaponItemId) =>
        SurvivorOrbData.TryGetColorAndTier(weaponItemId, out SurvivorOrbColor color, out _) &&
        color == SurvivorOrbColor.Blue;

    public static bool ShouldApplyWaveSplash(int weaponItemId, long targetPlayerId, bool isResonanceProc) =>
        targetPlayerId < 0 && !isResonanceProc && IsWaveOrb(weaponItemId);

    public static IReadOnlyList<MonsterCombatTarget> FindWaveSplashTargets(
        IReadOnlyCollection<MonsterCombatTarget> aliveTargets,
        int primaryMonsterId)
    {
        if (aliveTargets == null)
            throw new ArgumentNullException(nameof(aliveTargets));
        if (primaryMonsterId <= 0)
            return [];

        MonsterCombatTarget primary = default;
        foreach (var target in aliveTargets)
        {
            if (target.MonsterId != primaryMonsterId)
                continue;

            primary = target;
            break;
        }

        if (primary.MonsterId == 0)
            return [];

        float radiusSquared = SurvivorOrbData.WaveSplashRadius * SurvivorOrbData.WaveSplashRadius;
        var candidates = new List<(MonsterCombatTarget Target, float DistanceSquared)>();
        foreach (var target in aliveTargets)
        {
            if (target.MonsterId == primaryMonsterId ||
                target.MapId != primary.MapId ||
                target.Area != primary.Area)
            {
                continue;
            }

            float x = target.Position.X - primary.Position.X;
            float y = target.Position.Y - primary.Position.Y;
            float distanceSquared = x * x + y * y;
            if (distanceSquared > radiusSquared)
                continue;

            candidates.Add((target, distanceSquared));
        }

        candidates.Sort(static (left, right) =>
        {
            int distanceComparison = left.DistanceSquared.CompareTo(right.DistanceSquared);
            return distanceComparison != 0
                ? distanceComparison
                : left.Target.MonsterId.CompareTo(right.Target.MonsterId);
        });

        return candidates
            .Take(SurvivorOrbData.WaveSplashMaxSecondaryTargets)
            .Select(candidate => candidate.Target)
            .ToArray();
    }
}
