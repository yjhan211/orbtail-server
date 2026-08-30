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

        public static void Initialize(List<CsvRow> data)
        {
            _definitions.Clear();

            foreach (var row in data)
            {
                var definition = BattleItemCombatDefinition.CreateFromData(row);
                if (!_definitions.TryAdd(definition.ItemId, definition))
                    throw new ArgumentException($"Duplicate battle item combat data: item_id={definition.ItemId}");
            }
        }

        public static BattleItemCombatDefinition? Get(int itemId) =>
            _definitions.GetValueOrDefault(itemId);

        public static bool TryGet(int itemId, out BattleItemCombatDefinition? definition) =>
            _definitions.TryGetValue(itemId, out definition);

        public static bool IsCombatItem(int itemId) => _definitions.ContainsKey(itemId);

        public static List<BattleItemCombatDefinition> GetAll() =>
            _definitions.Values.OrderBy(definition => definition.ItemId).ToList();
    }

    public sealed class BattleItemCombatDefinition
    {
        public int ItemId { get; private set; }
        public string Family { get; private set; }
        public int Tier { get; private set; }
        public float AttackRange { get; private set; }
        public int Damage { get; private set; }
        public float AttackIntervalSeconds { get; private set; }
        public float ProjectileWidth { get; private set; }
        public float EffectDurationSeconds { get; private set; }

        public static BattleItemCombatDefinition CreateFromData(CsvRow row)
        {
            var definition = new BattleItemCombatDefinition
            {
                ItemId = int.Parse(row["item_id"], CultureInfo.InvariantCulture),
                Family = row["family"]?.Trim() ?? "",
                Tier = int.Parse(row["tier"], CultureInfo.InvariantCulture),
                AttackRange = float.Parse(row["attack_range"], CultureInfo.InvariantCulture),
                Damage = int.Parse(row["damage"], CultureInfo.InvariantCulture),
                AttackIntervalSeconds = float.Parse(row["attack_interval_seconds"], CultureInfo.InvariantCulture),
                ProjectileWidth = float.Parse(row["projectile_width"], CultureInfo.InvariantCulture),
                EffectDurationSeconds = float.Parse(row["effect_duration_seconds"], CultureInfo.InvariantCulture)
            };

            if (definition.ItemId <= 0 || string.IsNullOrWhiteSpace(definition.Family) ||
                definition.Tier is < 1 or > 3 || definition.AttackRange <= 0f ||
                definition.Damage <= 0 || definition.AttackIntervalSeconds <= 0f ||
                definition.ProjectileWidth < 0f || definition.EffectDurationSeconds < 0f)
            {
                throw new ArgumentException($"Invalid battle item combat data: item_id={definition.ItemId}");
            }

            return definition;
        }
    }
}
