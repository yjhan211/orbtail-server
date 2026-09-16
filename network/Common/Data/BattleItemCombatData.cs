// ReSharper disable All
#pragma warning disable CS8618

using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using network.common.data.helpers;

namespace network.common.data
{
    public static class BattleItemCombatData
    {
        private static readonly Dictionary<int, BattleItemCombatDefinition> _definitions = new();
        private static readonly Dictionary<(OrbColor Color, int Tier), int> _itemIdByColorTier = new();

        public static void Initialize(List<CsvRow> data)
        {
            _definitions.Clear();
            _itemIdByColorTier.Clear();

            foreach (var row in data)
            {
                var definition = BattleItemCombatDefinition.CreateFromData(row);
                if (!_definitions.TryAdd(definition.ItemId, definition))
                    throw new ArgumentException($"Duplicate battle item combat data: item_id={definition.ItemId}");

                if (definition.Color == OrbColor.None)
                    continue;

                if (!_itemIdByColorTier.TryAdd((definition.Color, definition.Tier), definition.ItemId))
                    throw new ArgumentException(
                        $"Duplicate battle item color/tier: item_id={definition.ItemId}, color={definition.Color}, tier={definition.Tier}");
            }
        }

        public static BattleItemCombatDefinition? Get(int itemId) =>
            _definitions.GetValueOrDefault(itemId);

        public static bool TryGet(int itemId, out BattleItemCombatDefinition? definition) =>
            _definitions.TryGetValue(itemId, out definition);

        public static bool IsCombatItem(int itemId) => _definitions.ContainsKey(itemId);

        public static List<BattleItemCombatDefinition> GetAll() =>
            _definitions.Values.OrderBy(definition => definition.ItemId).ToList();

        /// <summary>색·티어가 저작된 행(color != 0)만 참 — 레거시 가디언 오브·나침반은 대상 밖.</summary>
        public static bool TryGetColorAndTier(int itemId, out OrbColor color, out int tier)
        {
            var definition = Get(itemId);
            if (definition == null || definition.Color == OrbColor.None)
            {
                color = OrbColor.None;
                tier = 0;
                return false;
            }

            color = definition.Color;
            tier = definition.Tier;
            return true;
        }

        public static bool TryGetItemId(OrbColor color, int tier, out int itemId) =>
            _itemIdByColorTier.TryGetValue((color, tier), out itemId);

        /// <summary>티어 공통값 조회 — 색이 저작된 오브 행(빨강 기준)에서 읽는다. 행이 없으면 T1 기본.</summary>
        private static BattleItemCombatDefinition? GetTierReferenceRow(int tier)
        {
            int clamped = Math.Clamp(tier, 1, 3);
            return _itemIdByColorTier.TryGetValue((OrbColor.Red, clamped), out int itemId)
                ? Get(itemId)
                : null;
        }

        public static int GetOrbMaxHp(int tier) => GetTierReferenceRow(tier)?.OrbMaxHp ?? 24;

        public static float GetTierScale(int tier) => GetTierReferenceRow(tier)?.TierScale ?? 1f;

        public static float GetStatTierWeight(int tier) => GetTierReferenceRow(tier)?.StatTierWeight ?? 1f;
    }

    public sealed class BattleItemCombatDefinition
    {
        public int ItemId { get; private set; }
        public string Family { get; private set; }
        public OrbColor Color { get; private set; }
        public int Tier { get; private set; }
        public float AttackRange { get; private set; }
        public int Damage { get; private set; }
        public float AttackIntervalSeconds { get; private set; }
        public int OrbMaxHp { get; private set; }
        public float TierScale { get; private set; }
        public float StatTierWeight { get; private set; }

        public static BattleItemCombatDefinition CreateFromData(CsvRow row)
        {
            var definition = new BattleItemCombatDefinition
            {
                ItemId = int.Parse(row["item_id"], CultureInfo.InvariantCulture),
                Family = row["family"]?.Trim() ?? "",
                Color = row.ContainsKey("color")
                    ? (OrbColor)int.Parse(row["color"], CultureInfo.InvariantCulture)
                    : OrbColor.None,
                Tier = int.Parse(row["tier"], CultureInfo.InvariantCulture),
                AttackRange = float.Parse(row["attack_range"], CultureInfo.InvariantCulture),
                Damage = int.Parse(row["damage"], CultureInfo.InvariantCulture),
                AttackIntervalSeconds = float.Parse(row["attack_interval_seconds"], CultureInfo.InvariantCulture),
                OrbMaxHp = row.ContainsKey("orb_max_hp")
                    ? int.Parse(row["orb_max_hp"], CultureInfo.InvariantCulture)
                    : 0,
                TierScale = row.ContainsKey("tier_scale")
                    ? float.Parse(row["tier_scale"], CultureInfo.InvariantCulture)
                    : 1f,
                StatTierWeight = row.ContainsKey("stat_tier_weight")
                    ? float.Parse(row["stat_tier_weight"], CultureInfo.InvariantCulture)
                    : 1f
            };

            if (definition.ItemId <= 0 || string.IsNullOrWhiteSpace(definition.Family) ||
                definition.Tier is < 1 or > 3)
            {
                throw new ArgumentException($"Invalid battle item combat data: item_id={definition.ItemId}");
            }

            if (definition.AttackRange <= 0f || definition.Damage <= 0 ||
                definition.AttackIntervalSeconds <= 0f)
            {
                throw new ArgumentException($"Invalid battle item combat data: item_id={definition.ItemId}");
            }

            return definition;
        }
    }
}
