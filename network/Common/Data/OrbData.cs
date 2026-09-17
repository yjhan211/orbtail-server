using System;
using System.Collections.Generic;
using System.Linq;
using network.common.data.models;

namespace network.common.data
{
    public enum OrbColor
    {
        None = 0,
        Red = 1,
        Green = 2,
        Blue = 3
    }

    public static class OrbData
    {
        public const float WaveSlowSeconds = 5f;
        public const float WaveSlowMoveSpeedMultiplier = 0.75f;
        private const float WaveSplashRadius = 1.8f;
        private const float WaveTierTwoSplashRadius = 2.2f;
        private const float WaveTierThreeSplashRadius = 2.6f;
        private const float PveNeutralDamageMultiplier = 1f;
        private const float HopeProjectileSpeed = 6f;
        private const float SunFirstAttackBonus = 0.15f;
        private const float SunAdditionalAttackBonus = 0.05f;
        private const float SunAttackBonusCap = 0.40f;
        private const float WindFirstMoveSpeedBonus = 0.06f;
        private const float WindAdditionalMoveSpeedBonus = 0.02f;
        private const float WindMoveSpeedBonusCap = 0.14f;

        public static int GetSwarmPveAttackDamage(int itemId)
        {
            if (!TryGetColorAndTier(itemId, out _, out _))
            {
                return 0;
            }

            return BattleItemCombatData.Get(itemId)?.Damage ?? 0;
        }

        public static float GetSwarmWaveBombRadius(int itemId)
        {
            if (!TryGetColorAndTier(itemId, out var color, out int tier) || color != OrbColor.Blue)
            {
                return 0f;
            }

            return tier >= 3 ? WaveTierThreeSplashRadius :
                tier == 2 ? WaveTierTwoSplashRadius : WaveSplashRadius;
        }

        public static float GetSwarmStatTierWeight(int tier) => BattleItemCombatData.GetStatTierWeight(tier);
        private const int DraftTierTwoAtSeconds = 80;
        private const int DraftTierThreeAtSeconds = 160;

        public static int GetDraftTierByElapsed(double? elapsedSeconds) =>
            elapsedSeconds >= DraftTierThreeAtSeconds ? 3 :
            elapsedSeconds >= DraftTierTwoAtSeconds ? 2 : 1;

        public static float GetSwarmOrbTierScale(int tier) =>
            BattleItemCombatData.GetTierScale(tier);

        public static float GetSwarmTrailDistance(IReadOnlyList<int> orderedTiers, int ordinal)
        {
            float distance = Config.SWARM_ORB_TRAIL_FIRST_OFFSET * TierScaleAt(orderedTiers, 0);
            for (int index = 1; index <= ordinal; index++)
            {
                float previous = TierScaleAt(orderedTiers, index - 1);
                float current = TierScaleAt(orderedTiers, index);
                distance += Config.SWARM_ORB_TRAIL_SPACING * (previous + current) * 0.5f;
            }

            return distance;
        }

        private static float TierScaleAt(IReadOnlyList<int> orderedTiers, int index) =>
            orderedTiers != null && index >= 0 && index < orderedTiers.Count
                ? GetSwarmOrbTierScale(orderedTiers[index])
                : 1f;

        private static int CountLivingOrbs(IEnumerable<InGameItemInfo> items, OrbColor color)
        {
            if (items == null)
            {
                return 0;
            }

            int count = 0;
            foreach (var item in items)
            {
                if (item.Count <= 0 || !TryGetColorAndTier(item.ItemId, out var itemColor, out _) || itemColor != color)
                {
                    continue;
                }

                count += item.Count;
            }

            return count;
        }

        public static bool IsResonating(IEnumerable<InGameItemInfo> items, OrbColor color)
        {
            if (items == null)
            {
                return false;
            }

            var boardItemIds = new List<int>();
            foreach (var item in items)
            {
                for (int i = 0; i < item.Count; i++)
                {
                    boardItemIds.Add(item.ItemId);
                }
            }

            return TryGetDominantPveColor(boardItemIds, out OrbColor dominantColor) && dominantColor == color;
        }

        public static float GetSunPveAttackMultiplier(IEnumerable<InGameItemInfo> items)
        {
            var orbs = items as IReadOnlyCollection<InGameItemInfo> ?? items?.ToList();
            if (!IsResonating(orbs, OrbColor.Red))
            {
                return 1f;
            }

            int count = CountLivingOrbs(orbs, OrbColor.Red);
            float bonus = SunFirstAttackBonus + (count - 1) * SunAdditionalAttackBonus;
            return 1f + Math.Min(SunAttackBonusCap, bonus);
        }

        public static float GetWindMoveSpeedMultiplier(IEnumerable<InGameItemInfo> items)
        {
            var orbs = items as IReadOnlyCollection<InGameItemInfo> ?? items?.ToList();
            if (!IsResonating(orbs, OrbColor.Green))
            {
                return 1f;
            }

            int count = CountLivingOrbs(orbs, OrbColor.Green);
            float bonus = WindFirstMoveSpeedBonus + (count - 1) * WindAdditionalMoveSpeedBonus;
            return 1f + Math.Min(WindMoveSpeedBonusCap, bonus);
        }

        private static readonly OrbColor[] EvolutionColors = { OrbColor.Red, OrbColor.Green, OrbColor.Blue };

        public static bool TryGetColorAndTier(int itemId, out OrbColor color, out int tier)
        {
            if (!BattleItemCombatData.TryGetColorAndTier(itemId, out color, out tier))
            {
                color = OrbColor.None;
                tier = 0;
                return false;
            }

            return true;
        }

        public static int GetOrbGroupId(int itemId) => itemId / 10;

        public static bool TryGetOrbGroupAndTier(int itemId, out int orbGroupId, out int tier)
        {
            if (!TryGetColorAndTier(itemId, out _, out tier))
            {
                orbGroupId = 0;
                return false;
            }

            orbGroupId = GetOrbGroupId(itemId);
            return true;
        }

        public static bool TryGetOrbItemId(int orbGroupId, int tier, out int itemId)
        {
            itemId = 0;
            if (orbGroupId <= 0 || tier is < 1 or > 3)
            {
                return false;
            }

            int candidateItemId = checked(orbGroupId * 10 + tier - 1);
            if (!TryGetOrbGroupAndTier(candidateItemId, out int candidateGroupId, out int candidateTier) ||
                candidateGroupId != orbGroupId || candidateTier != tier)
            {
                return false;
            }

            itemId = candidateItemId;
            return true;
        }

        public static bool IsOrbItem(int itemId) => TryGetColorAndTier(itemId, out _, out _);

        /// <summary>색 라인의 특정 티어 오브 아이템. battle_item_combat.csv가 원천이며 없으면 0.</summary>
        public static int GetItemId(OrbColor color, int tier) =>
            BattleItemCombatData.TryGetItemId(color, tier, out int itemId) ? itemId : 0;

        public static int GetTierOneItemId(OrbColor color) => GetItemId(color, 1);

        public static float GetPvpProjectileImpactDelaySeconds(int itemId, float distance)
        {
            return Math.Max(0.08f, Math.Max(0f, distance) / HopeProjectileSpeed);
        }

        public static bool TryGetDominantPveColor(
            IEnumerable<int> boardItemIds,
            out OrbColor dominantColor)
        {
            if (boardItemIds == null)
                throw new ArgumentNullException(nameof(boardItemIds));

            var orbCounts = new Dictionary<OrbColor, int>
            {
                [OrbColor.Red] = 0,
                [OrbColor.Green] = 0,
                [OrbColor.Blue] = 0
            };
            int occupiedOrbCount = 0;

            foreach (int itemId in boardItemIds)
            {
                if (!TryGetColorAndTier(itemId, out OrbColor color, out _) ||
                    !orbCounts.ContainsKey(color))
                {
                    continue;
                }

                occupiedOrbCount++;
                orbCounts[color]++;
            }

            if (occupiedOrbCount < 2)
            {
                dominantColor = OrbColor.None;
                return false;
            }

            var majority = orbCounts
                .Where(pair => pair.Value * 2 > occupiedOrbCount)
                .Select(pair => pair.Key)
                .ToArray();
            if (majority.Length == 1)
            {
                dominantColor = majority[0];
                return true;
            }

            dominantColor = OrbColor.None;
            return false;
        }

        public static int CalculatePveDamage(int baseDamage, float hitDamageMultiplier = 1f)
        {
            if (baseDamage <= 0 || hitDamageMultiplier <= 0f)
            {
                return 0;
            }

            float damage = baseDamage * hitDamageMultiplier * PveNeutralDamageMultiplier;
            return Math.Max(1, (int)Math.Ceiling(damage));
        }


        public static bool TryGetItemId(OrbColor color, int tier, out int itemId)
        {
            if (color == OrbColor.None)
            {
                itemId = 0;
                return false;
            }

            return BattleItemCombatData.TryGetItemId(color, tier, out itemId);
        }

        public static bool TryGetActivePair(IEnumerable<int> boardItemIds, out OrbColor color, out int pairTier)
        {
            if (boardItemIds == null)
            {
                throw new ArgumentNullException(nameof(boardItemIds));
            }

            var itemIds = boardItemIds.ToList();
            foreach (var candidateColor in EvolutionColors)
            {
                if (!HasActivePair(itemIds, candidateColor, out pairTier))
                {
                    continue;
                }

                color = candidateColor;
                return true;
            }

            color = OrbColor.None;
            pairTier = 0;
            return false;
        }

        public static bool HasActivePair(IEnumerable<int> boardItemIds, OrbColor targetColor, out int pairTier)
        {
            if (boardItemIds == null)
            {
                throw new ArgumentNullException(nameof(boardItemIds));
            }

            int matchingCount = 0;
            pairTier = 0;
            foreach (int itemId in boardItemIds)
            {
                if (!TryGetColorAndTier(itemId, out var color, out int tier) || color != targetColor)
                {
                    continue;
                }

                matchingCount++;
                pairTier = Math.Max(pairTier, tier);
            }

            if (matchingCount >= 2)
            {
                return true;
            }
            pairTier = 0;
            return false;
        }

        public static bool TryGetActivePair(int equippedItemId, IEnumerable<int> otherBoardItemIds, out OrbColor color, out int pairTier)
        {
            if (otherBoardItemIds == null)
            {
                throw new ArgumentNullException(nameof(otherBoardItemIds));
            }

            pairTier = 0;
            if (!TryGetColorAndTier(equippedItemId, out color, out _))
            {
                return false;
            }

            foreach (int itemId in otherBoardItemIds)
            {
                if (!TryGetColorAndTier(itemId, out OrbColor candidateColor, out int candidateTier) || candidateColor != color)
                {
                    continue;
                }

                pairTier = Math.Max(pairTier, candidateTier);
            }

            return pairTier > 0;
        }
    }
}
