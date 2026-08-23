using network.common;
using network.common.data;
using network.common.data.models;

namespace game_server.services;

public static class EmotionAfterimagePveCombatRules
{
    public static bool IsWaveOrb(int weaponItemId) =>
        OrbData.TryGetColorAndTier(weaponItemId, out OrbColor color, out _) &&
        color == OrbColor.Blue;

    public static bool ShouldApplyWaveAreaAttack(int weaponItemId, bool isResonanceProc) =>
        !isResonanceProc && IsWaveOrb(weaponItemId);

    public static bool ShouldEmitWaveProjectilePresentation(bool isWaveAreaSecondary) =>
        !isWaveAreaSecondary;

    public static float GetWaveSplashRadius(int weaponItemId)
    {
        if (!OrbData.TryGetColorAndTier(weaponItemId, out OrbColor color, out int tier) ||
            color != OrbColor.Blue)
        {
            return OrbData.WaveSplashRadius;
        }

        return tier switch
        {
            >= 3 => OrbData.WaveTierThreeSplashRadius,
            2 => OrbData.WaveTierTwoSplashRadius,
            _ => OrbData.WaveSplashRadius
        };
    }

    public static IReadOnlyList<long> FindWaveAreaSecondaryTargetIds(
        IReadOnlyCollection<ProximityCombatActor> combatTargets,
        long attackerPlayerId,
        long primaryTargetId,
        AreaType area,
        int weaponItemId)
    {
        if (combatTargets == null)
            throw new ArgumentNullException(nameof(combatTargets));

        var uniqueTargets = combatTargets
            .Where(target => target.PlayerId != attackerPlayerId && target.Area == area)
            .GroupBy(target => target.PlayerId)
            .Select(group => group.First())
            .ToArray();
        var primaryTarget = uniqueTargets.FirstOrDefault(target => target.PlayerId == primaryTargetId);
        if (primaryTarget.PlayerId == 0)
            return [];

        float radius = GetWaveSplashRadius(weaponItemId);
        float radiusSquared = radius * radius;
        return uniqueTargets
            .Where(target => target.PlayerId != primaryTargetId &&
                             target.MapId == primaryTarget.MapId &&
                             IsWithinRadius(target.Position, primaryTarget.Position, radiusSquared))
            .OrderBy(target => target.PlayerId)
            .Select(target => target.PlayerId)
            .ToArray();
    }

    private static bool IsWithinRadius(Vector3f targetPosition, Vector3f centerPosition, float radiusSquared)
    {
        float x = targetPosition.X - centerPosition.X;
        float y = targetPosition.Y - centerPosition.Y;
        return x * x + y * y <= radiusSquared;
    }

}
