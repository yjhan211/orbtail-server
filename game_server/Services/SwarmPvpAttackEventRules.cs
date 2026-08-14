using network.common.data;

namespace game_server.services
{
    /// <summary>
    ///     #227 6단계의 속성별 PvP 공격 이벤트 수치. 티어는 한 번의 위력만 바꾸고
    ///     이벤트 주기는 바꾸지 않는다.
    /// </summary>
    public static class SwarmPvpAttackEventRules
    {
        public const float SunWindRange = 6f;
        public const float WaveParticipationRange = 3.1f;
        public const double CrossAttributeGapSeconds = 0.35d;
        public const int MaxProjectileVisuals = 12;

        public static double GetIntervalSeconds(SurvivorOrbColor color)
        {
            return color switch
            {
                SurvivorOrbColor.Red => 2.4d,
                SurvivorOrbColor.Green => 1.6d,
                SurvivorOrbColor.Blue => 2.8d,
                _ => double.PositiveInfinity
            };
        }

        public static double GetTelegraphSeconds(SurvivorOrbColor color)
        {
            return color switch
            {
                SurvivorOrbColor.Red => 0.45d,
                SurvivorOrbColor.Green => 0.22d,
                SurvivorOrbColor.Blue => 0.55d,
                _ => 0d
            };
        }

        public static double GetLaunchSpacingSeconds(SurvivorOrbColor color)
        {
            return color switch
            {
                SurvivorOrbColor.Red => 0.08d,
                SurvivorOrbColor.Green => 0.06d,
                _ => 0d
            };
        }

        public static int GetPerOrbEventDamage(SurvivorOrbColor color, int tier)
        {
            int clampedTier = Math.Clamp(tier, 1, 3);
            return color switch
            {
                SurvivorOrbColor.Red => clampedTier switch { 1 => 8, 2 => 14, _ => 20 },
                SurvivorOrbColor.Green => clampedTier switch { 1 => 5, 2 => 9, _ => 13 },
                SurvivorOrbColor.Blue => clampedTier switch { 1 => 10, 2 => 17, _ => 24 },
                _ => 0
            };
        }

        public static int GetDamageCap(SurvivorOrbColor color)
        {
            return color == SurvivorOrbColor.Blue ? 126 : 105;
        }

        public static int CapDamage(SurvivorOrbColor color, int damage)
        {
            return Math.Clamp(damage, 0, GetDamageCap(color));
        }

        public static int GetWaveRadiusTier(int highestTier)
        {
            return Math.Clamp(highestTier, 1, 3);
        }

        public static float GetWaveRadius(int highestTier)
        {
            return GetWaveRadiusTier(highestTier) switch
            {
                1 => 2.6f,
                2 => 3.0f,
                _ => 3.4f
            };
        }

        public static int GetVisualProjectileCount(SurvivorOrbColor color, int participatingOrbCount)
        {
            int projectilesPerOrb = color == SurvivorOrbColor.Green ? 3 : 1;
            return Math.Min(MaxProjectileVisuals, Math.Max(0, participatingOrbCount) * projectilesPerOrb);
        }
    }
}
