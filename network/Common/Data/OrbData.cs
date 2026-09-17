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
        public static int GetAttackDamage(int itemId)
        {
            if (!TryGetOrbGroupAndTier(itemId, out _, out _))
            {
                return 0;
            }
            return BattleItemCombatData.Get(itemId)?.Damage ?? 0;
        }

        public static int GetAttackDamage(int itemId, float attackMultiplier, float seriesMultiplier) =>
            Math.Max(1, (int)MathF.Round(GetAttackDamage(itemId) * attackMultiplier * seriesMultiplier));

        /// <summary>태양 선의 판정 폭(바닥면). 서버 쓸기 판정과 클라 예고 표시가 같은 값을 쓴다.</summary>
        public static float GetSunWidth(int itemId)
        {
            if (!TryGetOrbGroupAndTier(itemId, out int orbGroupId, out int tier) || orbGroupId != OrbGroupIds.Sun)
            {
                return 0f;
            }
            return Config.TierValue(Config.SWARM_SUN_WIDTH_BY_TIER, tier);
        }

        public static float GetWaveVortexRadius(int itemId)
        {
            if (!TryGetOrbGroupAndTier(itemId, out int orbGroupId, out int tier) || orbGroupId != OrbGroupIds.Wave)
            {
                return 0f;
            }
            return Config.TierValue(Config.SWARM_WAVE_VORTEX_RADIUS_BY_TIER, tier);
        }

        public static int GetDraftTierByElapsed(double? elapsedSeconds) =>
            elapsedSeconds >= Config.SWARM_DRAFT_TIER_THREE_AT_SECONDS ? 3 :
            elapsedSeconds >= Config.SWARM_DRAFT_TIER_TWO_AT_SECONDS ? 2 : 1;

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

        private static float TierScaleAt(IReadOnlyList<int> orderedTiers, int index) => index >= 0 && index < orderedTiers.Count ? BattleItemCombatData.GetTierScale(orderedTiers[index]) : 1f;

        private static int CountLivingOrbs(IEnumerable<InGameItemInfo> items, int orbGroupId)
        {
            int count = 0;
            foreach (var item in items)
            {
                if (TryGetOrbGroupAndTier(item.ItemId, out var itemColor, out _) && itemColor == orbGroupId)
                {
                    count++;
                }
            }
            return count;
        }

        public static bool IsResonating(IEnumerable<InGameItemInfo> items, int orbGroupId)
        {
            var boardItemIds = new List<int>();
            foreach (var item in items)
            {
                boardItemIds.Add(item.ItemId);
            }
            return TryGetDominantOrbGroup(boardItemIds, out int dominantGroupId) && dominantGroupId == orbGroupId;
        }

        public static float GetAttackMultiplier(IEnumerable<InGameItemInfo> items)
        {
            var orbs = items as IReadOnlyCollection<InGameItemInfo> ?? items?.ToList();
            if (!IsResonating(orbs, OrbGroupIds.Sun))
            {
                return 1f;
            }
            int count = CountLivingOrbs(orbs, OrbGroupIds.Sun);
            float bonus = Config.SWARM_SUN_RESONANCE_FIRST_ATTACK_BONUS + (count - 1) * Config.SWARM_SUN_RESONANCE_ADDITIONAL_ATTACK_BONUS;
            return 1f + Math.Min(Config.SWARM_SUN_RESONANCE_ATTACK_BONUS_CAP, bonus);
        }

        public static float GetMoveSpeedMultiplier(IEnumerable<InGameItemInfo> items)
        {
            var orbs = items as IReadOnlyCollection<InGameItemInfo> ?? items?.ToList();
            if (!IsResonating(orbs, OrbGroupIds.Wind))
            {
                return 1f;
            }
            int count = CountLivingOrbs(orbs, OrbGroupIds.Wind);
            float bonus = Config.SWARM_WIND_RESONANCE_FIRST_MOVE_SPEED_BONUS + (count - 1) * Config.SWARM_WIND_RESONANCE_ADDITIONAL_MOVE_SPEED_BONUS;
            return 1f + Math.Min(Config.SWARM_WIND_RESONANCE_MOVE_SPEED_BONUS_CAP, bonus);
        }

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

        public static bool IsOrbItem(int itemId) => TryGetOrbGroupAndTier(itemId, out _, out _);
        public static bool TryGetItemId(int orbGroupId, int tier, out int itemId) => BattleItemCombatData.TryGetItemId(orbGroupId, tier, out itemId);
        public static int GetItemId(int orbGroupId, int tier) => TryGetItemId(orbGroupId, tier, out int itemId) ? itemId : 0;
        public static int GetTierOneItemId(int orbGroupId) => GetItemId(orbGroupId, 1);
        public static bool TryGetDominantOrbGroup(IEnumerable<int> boardItemIds, out int dominantGroupId)
        {
            if (boardItemIds == null)
            {
                throw new ArgumentNullException(nameof(boardItemIds));
            }

            var orbCounts = new Dictionary<int, int>
            {
                [OrbGroupIds.Sun] = 0,
                [OrbGroupIds.Wind] = 0,
                [OrbGroupIds.Wave] = 0
            };

            int occupiedOrbCount = 0;
            foreach (int itemId in boardItemIds)
            {
                if (!TryGetOrbGroupAndTier(itemId, out int orbGroupId, out _) || !orbCounts.ContainsKey(orbGroupId))
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

            foreach (var pair in orbCounts)
            {
                if (pair.Value * 2 > occupiedOrbCount)
                {
                    dominantGroupId = pair.Key;
                    return true;
                }
            }
            dominantGroupId = OrbGroupIds.None;
            return false;
        }
    }
}
