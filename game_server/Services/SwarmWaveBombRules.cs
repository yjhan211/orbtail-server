using network.common.data;
using network.common.data.models;

namespace game_server.services
{
    public readonly record struct SwarmWaveOrbContribution(int Ordinal, int ItemId);

    public readonly record struct SwarmWaveBombTarget(long TargetId, Vector3f Position);

    public readonly record struct SwarmWaveBombPlan(
        Vector3f Position,
        int Damage,
        float Radius,
        int SourceOrdinal,
        int SourceItemId);

    /// <summary>
    ///     #229 fixed-position Wave bomb planning. Target order is supplied by the caller;
    ///     each orb takes a distinct target first, while visual telegraphs are capped and
    ///     later contributions are folded into the existing plans in order.
    /// </summary>
    public static class SwarmWaveBombRules
    {
        public const int MaxVisibleTelegraphs = 6;

        public static IReadOnlyList<SwarmWaveBombPlan> BuildPlans(
            IReadOnlyList<SwarmWaveOrbContribution> orbs,
            IReadOnlyList<SwarmWaveBombTarget> orderedTargets,
            float attackMultiplier)
        {
            if (orbs == null)
                throw new ArgumentNullException(nameof(orbs));
            if (orderedTargets == null)
                throw new ArgumentNullException(nameof(orderedTargets));
            if (orbs.Count == 0 || orderedTargets.Count == 0)
                return [];

            int visibleCount = Math.Min(MaxVisibleTelegraphs, orbs.Count);
            var plans = new List<SwarmWaveBombPlan>(visibleCount);
            float safeMultiplier = Math.Max(0f, attackMultiplier);

            for (int index = 0; index < orbs.Count; index++)
            {
                var orb = orbs[index];
                int baseDamage = OrbData.GetSwarmPveAttackDamage(orb.ItemId);
                float radius = OrbData.GetSwarmWaveBombRadius(orb.ItemId);
                if (baseDamage <= 0 || radius <= 0f)
                    continue;

                int damage = Math.Max(1, (int)MathF.Round(baseDamage * safeMultiplier));
                if (plans.Count < visibleCount)
                {
                    var target = orderedTargets[plans.Count % orderedTargets.Count];
                    plans.Add(new SwarmWaveBombPlan(
                        target.Position,
                        damage,
                        radius,
                        orb.Ordinal,
                        orb.ItemId));
                    continue;
                }

                int planIndex = index % visibleCount;
                var plan = plans[planIndex];
                plans[planIndex] = plan with
                {
                    Damage = plan.Damage + damage,
                    Radius = Math.Max(plan.Radius, radius)
                };
            }

            return plans;
        }
    }
}
