// ReSharper disable All
#pragma warning disable CS8618

using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using network.common.data.helpers;

namespace network.common.data
{
    /// <summary>
    ///     잔상 몬스터 종별 스탯 (swarm_monster.csv). kind 값은 서버 MonsterKind enum과 동기.
    ///     attack_range 0 = 접촉 몹 — 소비부가 접촉 반경으로 폴백한다.
    /// </summary>
    public static class SwarmMonsterData
    {
        private static readonly Dictionary<int, SwarmMonsterDefinition> _definitions = new();

        public static void Initialize(List<CsvRow> data)
        {
            _definitions.Clear();

            foreach (var row in data)
            {
                var definition = SwarmMonsterDefinition.CreateFromData(row);
                if (!_definitions.TryAdd(definition.Kind, definition))
                    throw new ArgumentException($"Duplicate swarm monster data: kind={definition.Kind}");
            }
        }

        public static SwarmMonsterDefinition? Get(int kind) =>
            _definitions.GetValueOrDefault(kind);

        public static List<SwarmMonsterDefinition> GetAll() =>
            _definitions.Values.OrderBy(definition => definition.Kind).ToList();
    }

    public sealed class SwarmMonsterDefinition
    {
        public int Kind { get; private set; }
        public int MaxHp { get; private set; }
        public int OrbDamage { get; private set; }
        public float AttackRange { get; private set; }
        public float AttackCooldownSeconds { get; private set; }
        public int StoneReward { get; private set; }
        public int HeartReward { get; private set; }
        public int BootsReward { get; private set; }
        public int KeyReward { get; private set; }
        public float ContactRadiusScale { get; private set; }

        public static SwarmMonsterDefinition CreateFromData(CsvRow row)
        {
            var definition = new SwarmMonsterDefinition
            {
                Kind = int.Parse(row["kind"], CultureInfo.InvariantCulture),
                MaxHp = int.Parse(row["max_hp"], CultureInfo.InvariantCulture),
                OrbDamage = int.Parse(row["orb_damage"], CultureInfo.InvariantCulture),
                AttackRange = float.Parse(row["attack_range"], CultureInfo.InvariantCulture),
                AttackCooldownSeconds =
                    float.Parse(row["attack_cooldown_seconds"], CultureInfo.InvariantCulture),
                StoneReward = int.Parse(row["stone_reward"], CultureInfo.InvariantCulture),
                HeartReward = int.Parse(row["heart_reward"], CultureInfo.InvariantCulture),
                BootsReward = int.Parse(row["boots_reward"], CultureInfo.InvariantCulture),
                KeyReward = int.Parse(row["key_reward"], CultureInfo.InvariantCulture),
                ContactRadiusScale = float.Parse(row["contact_radius_scale"], CultureInfo.InvariantCulture)
            };

            if (definition.Kind < 0 || definition.MaxHp <= 0 || definition.OrbDamage <= 0 ||
                definition.AttackRange < 0f || definition.AttackCooldownSeconds <= 0f ||
                definition.StoneReward < 0 || definition.HeartReward < 0 ||
                definition.BootsReward < 0 || definition.KeyReward < 0 ||
                definition.ContactRadiusScale <= 0f)
            {
                throw new ArgumentException($"Invalid swarm monster data: kind={definition.Kind}");
            }

            return definition;
        }
    }
}
