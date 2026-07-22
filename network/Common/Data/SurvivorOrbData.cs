using System;
using System.Collections.Generic;
using System.Linq;

namespace network.common.data
{

    /// <summary>
    /// Shared color and tier rules for the three Survivor Royale orb lines added by #198.
    /// Legacy guardian orbs (107000003/004/006) intentionally remain outside this board rule.
    /// </summary>
    public enum SurvivorOrbColor
    {
        None = 0,
        Red = 1,
        Green = 2,
        Blue = 3
    }

    public static class SurvivorOrbData
    {
        public const float SunDamageMultiplier = 1.75f;
        public const float SunAttackIntervalMultiplier = 1.25f;
        public const int WaveInitialBurstAttackCount = 3;
        public const float WaveInitialBurstIntervalMultiplier = 0.4f;
        public const float WaveBurstRechargeSeconds = 3f;
        public const int WindMaxTargets = 3;
        public const float WindAdditionalTargetDamageMultiplier = 0.5f;


        public static bool TryGetColorAndTier(int itemId, out SurvivorOrbColor color, out int tier)
        {
            switch (itemId)
            {
                case 107000010: color = SurvivorOrbColor.Red; tier = 1; return true;
                case 107000011: color = SurvivorOrbColor.Red; tier = 2; return true;
                case 107000012: color = SurvivorOrbColor.Red; tier = 3; return true;
                case 107000020: color = SurvivorOrbColor.Green; tier = 1; return true;
                case 107000021: color = SurvivorOrbColor.Green; tier = 2; return true;
                case 107000022: color = SurvivorOrbColor.Green; tier = 3; return true;
                case 107000030: color = SurvivorOrbColor.Blue; tier = 1; return true;
                case 107000031: color = SurvivorOrbColor.Blue; tier = 2; return true;
                case 107000032: color = SurvivorOrbColor.Blue; tier = 3; return true;
                default: color = SurvivorOrbColor.None; tier = 0; return false;
            }
        }

        public static bool IsSurvivorOrb(int itemId) => TryGetColorAndTier(itemId, out _, out _);

        public static int GetCombatDamage(SurvivorOrbColor color, bool isActive, int baseDamage)
        {
            if (!isActive || color != SurvivorOrbColor.Red || baseDamage <= 0)
                return baseDamage;

            return (int)Math.Ceiling(baseDamage * SunDamageMultiplier);
        }

        public static float GetAttackIntervalSeconds(
            SurvivorOrbColor color,
            bool isActive,
            float baseAttackIntervalSeconds)
        {
            if (!isActive || color != SurvivorOrbColor.Red || baseAttackIntervalSeconds <= 0f)
                return baseAttackIntervalSeconds;

            return baseAttackIntervalSeconds * SunAttackIntervalMultiplier;
        }

        public static int GetMaxTargets(SurvivorOrbColor color, bool isActive)
        {
            return isActive && color == SurvivorOrbColor.Green ? WindMaxTargets : 1;
        }


        public static float GetAdditionalTargetDamageMultiplier(SurvivorOrbColor color, bool isActive)
        {
            return isActive && color == SurvivorOrbColor.Green ? WindAdditionalTargetDamageMultiplier : 1f;
        }
        public static bool TryGetActivePair(
            int equippedItemId,
            IEnumerable<int> boardItemIds,
            out SurvivorOrbColor color,
            out int pairTier)
        {
            if (boardItemIds == null)
                throw new ArgumentNullException(nameof(boardItemIds));

            pairTier = 0;
            if (!TryGetColorAndTier(equippedItemId, out color, out _))
                return false;

            var tierCounts = new Dictionary<int, int>();
            foreach (int itemId in boardItemIds)
            {
                if (!TryGetColorAndTier(itemId, out var candidateColor, out int candidateTier) ||
                    candidateColor != color)
                {
                    continue;
                }

                tierCounts.TryGetValue(candidateTier, out int currentCount);
                tierCounts[candidateTier] = currentCount + 1;
            }

            pairTier = tierCounts
                .Where(entry => entry.Value >= 2)
                .Select(entry => entry.Key)
                .DefaultIfEmpty(0)
                .Max();
            return pairTier > 0;
        }
    }
}
