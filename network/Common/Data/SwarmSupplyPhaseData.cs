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
    ///     잔상 공급 페이즈 곡선 (swarm_supply_phase.csv, #335). 폐쇄 단계별 인당 목표·HP·접촉 피해을
    ///     phase_index 오름차순으로 든다. 필수 데이터이며 빈 목록이나 잘못된 곡선은 초기화 시 거부한다.
    ///     until_seconds가 0 이하면 매치 끝까지(최종 페이즈)로 읽는다.
    /// </summary>
    public static class SwarmSupplyPhaseData
    {
        private static readonly List<SwarmSupplyPhaseDefinition> _phases = new();

        public static void Initialize(List<CsvRow> data)
        {
            var byIndex = new Dictionary<int, SwarmSupplyPhaseDefinition>();
            foreach (var row in data)
            {
                var definition = SwarmSupplyPhaseDefinition.CreateFromData(row);
                if (!byIndex.TryAdd(definition.PhaseIndex, definition))
                    throw new ArgumentException($"Duplicate swarm supply phase data: phase_index={definition.PhaseIndex}");
            }

            var phases = byIndex.Values.OrderBy(definition => definition.PhaseIndex).ToList();
            Validate(phases);
            _phases.Clear();
            _phases.AddRange(phases);
        }

        public static SwarmSupplyPhaseDefinition? Get(int phaseIndex) =>
            phaseIndex >= 0 && phaseIndex < _phases.Count ? _phases[phaseIndex] : null;

        public static IReadOnlyList<SwarmSupplyPhaseDefinition> GetAll() => _phases;

        public static bool IsLoaded => _phases.Count > 0;

        /// <summary>
        ///     phase_index는 0부터 빈틈없이 이어지고, until_seconds는 단조 증가하며, 마지막 페이즈만 무한이다 —
        ///     소비부의 선형 탐색(GetSupplyPhaseIndex)이 이 순서를 전제한다.
        /// </summary>
        public static void Validate() => Validate(_phases);

        private static void Validate(IReadOnlyList<SwarmSupplyPhaseDefinition> phases)
        {
            if (phases.Count == 0)
                throw new ArgumentException("swarm_supply_phase.csv must contain supply phases.");

            double previousUntil = 0d;
            for (int index = 0; index < phases.Count; index++)
            {
                var phase = phases[index];
                if (phase.PhaseIndex != index)
                    throw new ArgumentException($"Swarm supply phase_index must be contiguous from 0: got {phase.PhaseIndex} at {index}");

                bool last = index == phases.Count - 1;
                if (last != phase.IsFinal)
                    throw new ArgumentException($"Only the last swarm supply phase may have until_seconds <= 0: phase_index={phase.PhaseIndex}");
                if (!last && phase.UntilSeconds <= previousUntil)
                    throw new ArgumentException($"Swarm supply until_seconds must increase: phase_index={phase.PhaseIndex}");
                previousUntil = phase.UntilSeconds;
            }
        }
    }

    public sealed class SwarmSupplyPhaseDefinition
    {
        public int PhaseIndex { get; private set; }
        /// <summary>페이즈 종료 시각(초). 최종 페이즈는 double.MaxValue.</summary>
        public double UntilSeconds { get; private set; }
        public int PerPlayerTarget { get; private set; }
        public int NormalHp { get; private set; }
        public int ContactDamage { get; private set; }
        public int CoreHp { get; private set; }

        public bool IsFinal => UntilSeconds >= double.MaxValue;

        public SwarmSupplyPhaseDefinition(
            int phaseIndex, double untilSeconds, int perPlayerTarget, int normalHp, int contactDamage,
            int coreHp)
        {
            PhaseIndex = phaseIndex;
            UntilSeconds = untilSeconds;
            PerPlayerTarget = perPlayerTarget;
            NormalHp = normalHp;
            ContactDamage = contactDamage;
            CoreHp = coreHp;
        }

        public static SwarmSupplyPhaseDefinition CreateFromData(CsvRow row)
        {
            double untilSeconds = double.Parse(row["until_seconds"], CultureInfo.InvariantCulture);
            if (double.IsNaN(untilSeconds) || double.IsInfinity(untilSeconds))
                throw new ArgumentException("Swarm supply until_seconds must be finite.");
            var definition = new SwarmSupplyPhaseDefinition(
                int.Parse(row["phase_index"], CultureInfo.InvariantCulture),
                untilSeconds <= 0d ? double.MaxValue : untilSeconds,
                int.Parse(row["per_player_target"], CultureInfo.InvariantCulture),
                int.Parse(row["normal_hp"], CultureInfo.InvariantCulture),
                int.Parse(row["contact_damage"], CultureInfo.InvariantCulture),
                int.Parse(row["core_hp"], CultureInfo.InvariantCulture));

            if (definition.PhaseIndex < 0 || definition.PerPlayerTarget <= 0 || definition.NormalHp <= 0 ||
                definition.ContactDamage <= 0 || definition.CoreHp <= 0)
            {
                throw new ArgumentException($"Invalid swarm supply phase data: phase_index={definition.PhaseIndex}");
            }

            return definition;
        }
    }
}
