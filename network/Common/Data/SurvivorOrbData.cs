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
        public const float RecoveryTickSeconds = 5f;
        public const int WindMaxTargets = 3;
        public const float WindAdditionalTargetDamageMultiplier = 0.5f;

        private static readonly SurvivorOrbColor[] EvolutionColors =
            new[] { SurvivorOrbColor.Red, SurvivorOrbColor.Green, SurvivorOrbColor.Blue };

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
        public static bool TryGetRecoveryTier(int itemId, out int tier)
        {
            tier = itemId switch
            {
                107000040 => 1,
                107000041 => 2,
                107000042 => 3,
                _ => 0
            };
            return tier > 0;
        }

        public static bool IsRecoveryOrb(int itemId) => TryGetRecoveryTier(itemId, out _);

        public static int GetRecoveryAmount(int itemId) =>
            TryGetRecoveryTier(itemId, out int tier) ? tier : 0;

        /// <summary>
        /// Validates a P1 merge and randomly evolves its colour. The server calls this only after
        /// receiving the two inputs; clients must not predict the result.
        /// </summary>
        public static bool CanMerge(int inputA, int inputB)
        {
            return TryGetColorAndTier(inputA, out SurvivorOrbColor colorA, out int tierA) &&
                   TryGetColorAndTier(inputB, out SurvivorOrbColor colorB, out int tierB) &&
                   colorA == colorB && tierA == tierB && tierA < 3;
        }

        public static bool TryGetRandomMergeOutput(int inputA, int inputB, Random random, out int outputItemId)
        {
            if (random == null) throw new ArgumentNullException(nameof(random));
            outputItemId = 0;
            if (!CanMerge(inputA, inputB) || !TryGetColorAndTier(inputA, out _, out int tier))
                return false;

            return TryGetItemId(EvolutionColors[random.Next(EvolutionColors.Length)], tier + 1, out outputItemId);
        }

        public static bool TryGetItemId(SurvivorOrbColor color, int tier, out int itemId)
        {
            itemId = (color, tier) switch
            {
                (SurvivorOrbColor.Red, 1) => 107000010,
                (SurvivorOrbColor.Red, 2) => 107000011,
                (SurvivorOrbColor.Red, 3) => 107000012,
                (SurvivorOrbColor.Green, 1) => 107000020,
                (SurvivorOrbColor.Green, 2) => 107000021,
                (SurvivorOrbColor.Green, 3) => 107000022,
                (SurvivorOrbColor.Blue, 1) => 107000030,
                (SurvivorOrbColor.Blue, 2) => 107000031,
                (SurvivorOrbColor.Blue, 3) => 107000032,
                _ => 0
            };
            return itemId > 0;
        }

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

        public static int GetMaxTargets(SurvivorOrbColor color, bool isActive) =>
            isActive && color == SurvivorOrbColor.Green ? WindMaxTargets : 1;

        public static float GetAdditionalTargetDamageMultiplier(SurvivorOrbColor color, bool isActive) =>
            isActive && color == SurvivorOrbColor.Green ? WindAdditionalTargetDamageMultiplier : 1f;

        /// <summary>
        /// Resolves the first active resonance directly from the orb board.
        /// Equipment state is intentionally ignored; every owned orb is active.
        /// </summary>
        public static bool TryGetActivePair(
            IEnumerable<int> boardItemIds,
            out SurvivorOrbColor color,
            out int pairTier)
        {
            if (boardItemIds == null)
                throw new ArgumentNullException(nameof(boardItemIds));

            var itemIds = boardItemIds.ToList();
            foreach (SurvivorOrbColor candidateColor in EvolutionColors)
            {
                if (!HasActivePair(itemIds, candidateColor, out pairTier))
                    continue;

                color = candidateColor;
                return true;
            }

            color = SurvivorOrbColor.None;
            pairTier = 0;
            return false;
        }

        public static bool HasActivePair(
            IEnumerable<int> boardItemIds,
            SurvivorOrbColor targetColor,
            out int pairTier)
        {
            if (boardItemIds == null)
                throw new ArgumentNullException(nameof(boardItemIds));

            int matchingCount = 0;
            pairTier = 0;
            foreach (int itemId in boardItemIds)
            {
                if (!TryGetColorAndTier(itemId, out SurvivorOrbColor color, out int tier) ||
                    color != targetColor)
                    continue;

                matchingCount++;
                pairTier = Math.Max(pairTier, tier);
            }

            if (matchingCount >= 2) return true;
            pairTier = 0;
            return false;
        }

        /// <summary>
        /// Resonance is active when the equipped orb has at least one other orb of the same colour.
        /// The equipped item must be excluded from <paramref name="otherBoardItemIds"/> by the caller.
        /// <paramref name="pairTier"/> is retained for legacy callers and reports the highest support tier.
        /// </summary>
        public static bool TryGetActivePair(
            int equippedItemId,
            IEnumerable<int> otherBoardItemIds,
            out SurvivorOrbColor color,
            out int pairTier)
        {
            if (otherBoardItemIds == null)
                throw new ArgumentNullException(nameof(otherBoardItemIds));

            pairTier = 0;
            if (!TryGetColorAndTier(equippedItemId, out color, out _))
                return false;

            foreach (int itemId in otherBoardItemIds)
            {
                if (!TryGetColorAndTier(itemId, out SurvivorOrbColor candidateColor, out int candidateTier) ||
                    candidateColor != color)
                {
                    continue;
                }

                pairTier = Math.Max(pairTier, candidateTier);
            }

            return pairTier > 0;
        }
    }
}
