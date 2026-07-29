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
        Blue = 3,
        Recovery = 4
    }

    public static class SurvivorOrbData
    {
        public const float RecoveryTickSeconds = 5f;
        public const float WindChargeSeconds = 2f;
        public const float WindMoveSpeedMultiplier = 1.2f;
        public const float WindAttackRangeMultiplier = 1.2f;
        public const float WindAttackIntervalMultiplier = 0.85f;
        public const float WindProjectileSpeedMultiplier = 1.25f;
        public const float SunMarkLifetimeSeconds = 3f;
        public const int SunMarkTriggerCount = 3;
        public const float SunBurstDamageMultiplier = 0.75f;
        public const float SunBurstRadius = 2.4f;
        public const float SunFiveBurstRadiusMultiplier = 1.5f;
        public const int SunFiveBurstMaxTargets = 2;
        public const float WaveHitWindowSeconds = 1.5f;
        public const float WaveCounterCooldownSeconds = 6f;
        public const float WaveCounterDamageMultiplier = 1.25f;
        public const float WaveSlowSeconds = 1.5f;
        public const float WaveSlowMoveSpeedMultiplier = 0.65f;
        public const float WaveBaseAttackIntervalMultiplier = 1.25f;
        // The current room-horde pace needs each orb to fire twice as often.
        public const float OrbAttackIntervalMultiplier = 0.5f;
        public const float WaveSplashRadius = 1.8f;
        public const int WaveSplashMaxSecondaryTargets = 2;
        public const float WaveSplashSecondaryDamageMultiplier = 0.5f;
        public const float PveAdvantageDamageMultiplier = 1.5f;
        public const float PveNeutralDamageMultiplier = 1f;
        public const float PveDisadvantageDamageMultiplier = 0.5f;

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

        public static float GetBaseAttackIntervalMultiplier(SurvivorOrbColor color) =>
            color == SurvivorOrbColor.Blue ? WaveBaseAttackIntervalMultiplier : 1f;

        public static float GetAttackIntervalMultiplier(int itemId) => itemId switch
        {
            107000003 or 107000004 or 107000006 => OrbAttackIntervalMultiplier,
            _ when IsSurvivorOrb(itemId) => OrbAttackIntervalMultiplier,
            _ => 1f
        };

        public static float GetPveDamageMultiplier(int attackerItemId, int monsterRewardItemId)
        {
            if (!TryGetColorAndTier(attackerItemId, out SurvivorOrbColor attackerColor, out _) ||
                !TryGetColorAndTier(monsterRewardItemId, out SurvivorOrbColor targetColor, out _))
                return PveNeutralDamageMultiplier;

            return GetPveDamageMultiplier(attackerColor, targetColor);
        }

        public static float GetPveDamageMultiplier(SurvivorOrbColor attackerColor, int monsterRewardItemId)
        {
            if (!TryGetColorAndTier(monsterRewardItemId, out SurvivorOrbColor targetColor, out _))
                return PveNeutralDamageMultiplier;

            return GetPveDamageMultiplier(attackerColor, targetColor);
        }

        public static float GetPveDamageMultiplier(SurvivorOrbColor attackerColor, SurvivorOrbColor targetColor)
        {
            if (attackerColor is SurvivorOrbColor.None or SurvivorOrbColor.Recovery ||
                targetColor is SurvivorOrbColor.None or SurvivorOrbColor.Recovery)
                return PveNeutralDamageMultiplier;

            return (attackerColor, targetColor) switch
            {
                (SurvivorOrbColor.Red, SurvivorOrbColor.Green) => PveAdvantageDamageMultiplier,
                (SurvivorOrbColor.Green, SurvivorOrbColor.Blue) => PveAdvantageDamageMultiplier,
                (SurvivorOrbColor.Blue, SurvivorOrbColor.Red) => PveAdvantageDamageMultiplier,
                (SurvivorOrbColor.Red, SurvivorOrbColor.Blue) => PveDisadvantageDamageMultiplier,
                (SurvivorOrbColor.Green, SurvivorOrbColor.Red) => PveDisadvantageDamageMultiplier,
                (SurvivorOrbColor.Blue, SurvivorOrbColor.Green) => PveDisadvantageDamageMultiplier,
                _ => PveNeutralDamageMultiplier
            };
        }

        /// <summary>
        /// Resolves a board-wide PvE affinity. Total tier is the primary score;
        /// a ready resonance resolves an otherwise tied top score.
        /// </summary>
        public static bool TryGetDominantPveColor(
            IEnumerable<int> boardItemIds,
            out SurvivorOrbColor dominantColor)
        {
            if (boardItemIds == null)
                throw new ArgumentNullException(nameof(boardItemIds));

            var tierTotals = new Dictionary<SurvivorOrbColor, int>
            {
                [SurvivorOrbColor.Red] = 0,
                [SurvivorOrbColor.Green] = 0,
                [SurvivorOrbColor.Blue] = 0
            };
            var orbCounts = new Dictionary<SurvivorOrbColor, int>
            {
                [SurvivorOrbColor.Red] = 0,
                [SurvivorOrbColor.Green] = 0,
                [SurvivorOrbColor.Blue] = 0
            };

            foreach (int itemId in boardItemIds)
            {
                if (!TryGetColorAndTier(itemId, out SurvivorOrbColor color, out int tier) ||
                    !tierTotals.ContainsKey(color))
                    continue;

                tierTotals[color] += tier;
                orbCounts[color]++;
            }

            int highestTierTotal = tierTotals.Values.Max();
            if (highestTierTotal <= 0)
            {
                dominantColor = SurvivorOrbColor.None;
                return false;
            }

            var tiedColors = tierTotals
                .Where(pair => pair.Value == highestTierTotal)
                .Select(pair => pair.Key)
                .ToArray();
            if (tiedColors.Length == 1)
            {
                dominantColor = tiedColors[0];
                return true;
            }

            var resonantTiedColors = tiedColors
                .Where(color => IsPveResonanceReady(color, orbCounts[color]))
                .ToArray();
            if (resonantTiedColors.Length == 1)
            {
                dominantColor = resonantTiedColors[0];
                return true;
            }

            dominantColor = SurvivorOrbColor.None;
            return false;
        }

        public static bool IsPveResonanceReady(SurvivorOrbColor color, int orbCount) =>
            color == SurvivorOrbColor.Red ? orbCount >= 3 :
            color is SurvivorOrbColor.Green or SurvivorOrbColor.Blue && orbCount >= 2;
        public static int CalculatePveDamage(
            int attackerItemId,
            int monsterRewardItemId,
            int baseDamage,
            float hitDamageMultiplier = 1f)
        {
            if (baseDamage <= 0 || hitDamageMultiplier <= 0f)
                return 0;

            float damage = baseDamage * hitDamageMultiplier *
                           GetPveDamageMultiplier(attackerItemId, monsterRewardItemId);
            return Math.Max(1, (int)Math.Ceiling(damage));
        }

        public static int CalculatePveDamage(
            SurvivorOrbColor attackerColor,
            int monsterRewardItemId,
            int baseDamage,
            float hitDamageMultiplier = 1f)
        {
            if (baseDamage <= 0 || hitDamageMultiplier <= 0f)
                return 0;

            float damage = baseDamage * hitDamageMultiplier *
                           GetPveDamageMultiplier(attackerColor, monsterRewardItemId);
            return Math.Max(1, (int)Math.Ceiling(damage));
        }
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
            TryGetRecoveryTier(itemId, out int tier)
                ? tier switch
                {
                    1 => 3,
                    2 => 7,
                    3 => 14,
                    _ => 0
                }
                : 0;

        /// <summary>
        /// Validates a P1 merge and randomly evolves its colour. The server calls this only after
        /// receiving the two inputs; clients must not predict the result.
        /// </summary>
        public static bool CanMerge(int inputA, int inputB)
        {
            if (TryGetRecoveryTier(inputA, out int recoveryTierA) &&
                TryGetRecoveryTier(inputB, out int recoveryTierB))
            {
                return recoveryTierA == recoveryTierB && recoveryTierA < 3;
            }

            return TryGetColorAndTier(inputA, out SurvivorOrbColor colorA, out int tierA) &&
                   TryGetColorAndTier(inputB, out SurvivorOrbColor colorB, out int tierB) &&
                   colorA == colorB && tierA == tierB && tierA < 3;
        }

        public static bool TryGetRandomMergeOutput(int inputA, int inputB, Random random, out int outputItemId)
        {
            if (random == null) throw new ArgumentNullException(nameof(random));
            outputItemId = 0;
            if (!CanMerge(inputA, inputB))
                return false;

            if (TryGetRecoveryTier(inputA, out int recoveryTier))
            {
                outputItemId = recoveryTier switch
                {
                    1 => 107000041,
                    2 => 107000042,
                    _ => 0
                };
                return outputItemId > 0;
            }

            if (!TryGetColorAndTier(inputA, out _, out int tier))
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

        public static int GetSunResonanceStage(int sunCount)
        {
            if (sunCount >= 5) return 5;
            if (sunCount >= 3) return 3;
            return sunCount >= 1 ? 1 : 0;
        }
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
