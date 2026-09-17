using System;
using System.Collections.Generic;
using System.Linq;
using network.common.data.models;

namespace network.common.data
{
    public static class OrbGroupIds
    {
        public const int None = 0;
        public const int Sun = 10700001;
        public const int Wind = 10700002;
        public const int Wave = 10700003;
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
            if (!TryGetOrbGroupAndTier(itemId, out _, out _))
            {
                return 0;
            }

            return BattleItemCombatData.Get(itemId)?.Damage ?? 0;
        }

        public static float GetSwarmWaveBombRadius(int itemId)
        {
            if (!TryGetOrbGroupAndTier(itemId, out var orbGroupId, out int tier) || orbGroupId != OrbGroupIds.Wave)
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

        private static int CountLivingOrbs(IEnumerable<InGameItemInfo> items, int orbGroupId)
        {
            if (items == null)
            {
                return 0;
            }

            int count = 0;
            foreach (var item in items)
            {
                if (item.Count <= 0 || !TryGetOrbGroupAndTier(item.ItemId, out var itemColor, out _) || itemColor != orbGroupId)
                {
                    continue;
                }

                count += item.Count;
            }

            return count;
        }

        public static bool IsResonating(IEnumerable<InGameItemInfo> items, int orbGroupId)
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

            return TryGetDominantPveOrbGroup(boardItemIds, out var dominantGroupId) && dominantGroupId == orbGroupId;
        }

        public static float GetSunPveAttackMultiplier(IEnumerable<InGameItemInfo> items)
        {
            var orbs = items as IReadOnlyCollection<InGameItemInfo> ?? items?.ToList();
            if (!IsResonating(orbs, OrbGroupIds.Sun))
            {
                return 1f;
            }

            int count = CountLivingOrbs(orbs, OrbGroupIds.Sun);
            float bonus = SunFirstAttackBonus + (count - 1) * SunAdditionalAttackBonus;
            return 1f + Math.Min(SunAttackBonusCap, bonus);
        }

        public static float GetWindMoveSpeedMultiplier(IEnumerable<InGameItemInfo> items)
        {
            var orbs = items as IReadOnlyCollection<InGameItemInfo> ?? items?.ToList();
            if (!IsResonating(orbs, OrbGroupIds.Wind))
            {
                return 1f;
            }

            int count = CountLivingOrbs(orbs, OrbGroupIds.Wind);
            float bonus = WindFirstMoveSpeedBonus + (count - 1) * WindAdditionalMoveSpeedBonus;
            return 1f + Math.Min(WindMoveSpeedBonusCap, bonus);
        }

        private static readonly int[] EvolutionGroups = { OrbGroupIds.Sun, OrbGroupIds.Wind, OrbGroupIds.Wave };

        public static bool TryGetOrbGroupAndTier(int itemId, out int orbGroupId, out int tier)
        {
            if (!BattleItemCombatData.TryGetOrbGroupAndTier(itemId, out orbGroupId, out tier))
            {
                orbGroupId = OrbGroupIds.None;
                tier = 0;
                return false;
            }

            return true;
        }

        public static int GetOrbGroupId(int itemId) =>
            TryGetOrbGroupAndTier(itemId, out int orbGroupId, out _) ? orbGroupId : OrbGroupIds.None;

        public static bool TryGetOrbItemId(int orbGroupId, int tier, out int itemId) =>
            BattleItemCombatData.TryGetItemId(orbGroupId, tier, out itemId);

        public static bool IsOrbItem(int itemId) => TryGetOrbGroupAndTier(itemId, out _, out _);

        /// <summary>계열·티어에 해당하는 오브 아이템을 CSV에서 조회한다. 없으면 0.</summary>
        public static int GetItemId(int orbGroupId, int tier) =>
            BattleItemCombatData.TryGetItemId(orbGroupId, tier, out int itemId) ? itemId : 0;

        public static int GetTierOneItemId(int orbGroupId) => GetItemId(orbGroupId, 1);

        public static float GetPvpProjectileImpactDelaySeconds(int itemId, float distance)
        {
            return Math.Max(0.08f, Math.Max(0f, distance) / HopeProjectileSpeed);
        }

        public static bool TryGetDominantPveOrbGroup(
            IEnumerable<int> boardItemIds,
            out int dominantGroupId)
        {
            if (boardItemIds == null)
                throw new ArgumentNullException(nameof(boardItemIds));

            var orbCounts = new Dictionary<int, int>
            {
                [OrbGroupIds.Sun] = 0,
                [OrbGroupIds.Wind] = 0,
                [OrbGroupIds.Wave] = 0
            };
            int occupiedOrbCount = 0;

            foreach (int itemId in boardItemIds)
            {
                if (!TryGetOrbGroupAndTier(itemId, out int orbGroupId, out _) ||
                    !orbCounts.ContainsKey(orbGroupId))
                {
                    continue;
                }

                occupiedOrbCount++;
                orbCounts[orbGroupId]++;
            }

            if (occupiedOrbCount < 2)
            {
                dominantGroupId = OrbGroupIds.None;
                return false;
            }

            var majority = orbCounts
                .Where(pair => pair.Value * 2 > occupiedOrbCount)
                .Select(pair => pair.Key)
                .ToArray();
            if (majority.Length == 1)
            {
                dominantGroupId = majority[0];
                return true;
            }

            dominantGroupId = OrbGroupIds.None;
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


        public static bool TryGetItemId(int orbGroupId, int tier, out int itemId)
        {
            if (orbGroupId == OrbGroupIds.None)
            {
                itemId = 0;
                return false;
            }

            return BattleItemCombatData.TryGetItemId(orbGroupId, tier, out itemId);
        }

        public static bool TryGetActivePair(IEnumerable<int> boardItemIds, out int orbGroupId, out int pairTier)
        {
            if (boardItemIds == null)
            {
                throw new ArgumentNullException(nameof(boardItemIds));
            }

            var itemIds = boardItemIds.ToList();
            foreach (var candidateGroupId in EvolutionGroups)
            {
                if (!HasActivePair(itemIds, candidateGroupId, out pairTier))
                {
                    continue;
                }

                orbGroupId = candidateGroupId;
                return true;
            }

            orbGroupId = OrbGroupIds.None;
            pairTier = 0;
            return false;
        }

        public static bool HasActivePair(IEnumerable<int> boardItemIds, int targetGroupId, out int pairTier)
        {
            if (boardItemIds == null)
            {
                throw new ArgumentNullException(nameof(boardItemIds));
            }

            int matchingCount = 0;
            pairTier = 0;
            foreach (int itemId in boardItemIds)
            {
                if (!TryGetOrbGroupAndTier(itemId, out var orbGroupId, out int tier) || orbGroupId != targetGroupId)
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

        public static bool TryGetActivePair(int equippedItemId, IEnumerable<int> otherBoardItemIds, out int orbGroupId, out int pairTier)
        {
            if (otherBoardItemIds == null)
            {
                throw new ArgumentNullException(nameof(otherBoardItemIds));
            }

            pairTier = 0;
            if (!TryGetOrbGroupAndTier(equippedItemId, out orbGroupId, out _))
            {
                return false;
            }

            foreach (int itemId in otherBoardItemIds)
            {
                if (!TryGetOrbGroupAndTier(itemId, out int candidateGroupId, out int candidateTier) || candidateGroupId != orbGroupId)
                {
                    continue;
                }

                pairTier = Math.Max(pairTier, candidateTier);
            }

            return pairTier > 0;
        }
    }
}
