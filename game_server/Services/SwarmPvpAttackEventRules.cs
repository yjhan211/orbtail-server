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

        public static double GetIntervalSeconds(OrbColor color)
        {
            return color switch
            {
                OrbColor.Red => 2.4d,
                OrbColor.Green => 1.6d,
                OrbColor.Blue => 2.8d,
                _ => double.PositiveInfinity
            };
        }

        public static double GetTelegraphSeconds(OrbColor color)
        {
            return color switch
            {
                OrbColor.Red => 0.45d,
                OrbColor.Green => 0.22d,
                OrbColor.Blue => 0.55d,
                _ => 0d
            };
        }

        public static double GetLaunchSpacingSeconds(OrbColor color)
        {
            return color switch
            {
                OrbColor.Red => 0.08d,
                OrbColor.Green => 0.06d,
                _ => 0d
            };
        }

        public static int GetPerOrbEventDamage(OrbColor color, int tier)
        {
            int clampedTier = Math.Clamp(tier, 1, 3);
            return color switch
            {
                OrbColor.Red => clampedTier switch { 1 => 8, 2 => 14, _ => 20 },
                OrbColor.Green => clampedTier switch { 1 => 5, 2 => 9, _ => 13 },
                OrbColor.Blue => clampedTier switch { 1 => 10, 2 => 17, _ => 24 },
                _ => 0
            };
        }

        public static int GetDamageCap(OrbColor color)
        {
            return color == OrbColor.Blue ? 126 : 105;
        }

        public static int CapDamage(OrbColor color, int damage)
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

        public static int GetVisualProjectileCount(OrbColor color, int participatingOrbCount)
        {
            int projectilesPerOrb = color == OrbColor.Green ? 3 : 1;
            return Math.Min(MaxProjectileVisuals, Math.Max(0, participatingOrbCount) * projectilesPerOrb);
        }
    }
}
