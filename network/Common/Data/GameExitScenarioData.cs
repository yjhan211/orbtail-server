// ReSharper disable All
#pragma warning disable CS8618

using System;
using System.Collections.Generic;
using System.Linq;
using network.common.data.helpers;

namespace network.common.data
{
    public static class GameExitScenarioData
    {
        private static readonly Dictionary<int, ExitScenarioData> Scenarios = new();
        private static readonly Dictionary<ExitTemplateType, ExitScenarioData> ScenariosByTemplate = new();

        public static void Initialize(List<CsvRow> data)
        {
            foreach (var row in data)
            {
                var scenario = ExitScenarioData.CreateFromData(row);
                Scenarios[scenario.Id] = scenario;
                ScenariosByTemplate[scenario.ExitTemplateType] = scenario;
            }
        }

        public static ExitScenarioData Get(int id)
        {
            return Scenarios.GetValueOrDefault(id);
        }

        public static ExitScenarioData GetByTemplateType(ExitTemplateType templateType)
        {
            return ScenariosByTemplate.GetValueOrDefault(templateType);
        }

        public static List<ExitScenarioData> GetAll()
        {
            return Scenarios.Values.ToList();
        }
    }

    public class ExitScenarioData
    {
        public int Id { get; private set; }
        public ExitTemplateType ExitTemplateType { get; private set; }
        public List<ExitItemType> ItemTypePool { get; private set; }
        public List<ExitSpotType> SpotTypePool { get; private set; }
        public List<ExitDebuffType> DebuffTypePool { get; private set; }
        public List<ExitConditionType> ConditionTypePool { get; private set; }
        public ExitFinaleType FinaleType { get; private set; }
        public ExitEffectType EffectType { get; private set; }
        public int StepCount { get; private set; }

        private static List<T> ParseEnumPool<T>(string value) where T : struct, Enum
        {
            if (string.IsNullOrEmpty(value) || value == "0")
                return new List<T>();

            return value.Split('|')
                .Select(v => (T)Enum.ToObject(typeof(T), int.Parse(v.Trim())))
                .ToList();
        }

        public static ExitScenarioData CreateFromData(CsvRow row)
        {
            return new ExitScenarioData
            {
                Id = int.Parse(row["id"]),
                ExitTemplateType = (ExitTemplateType)int.Parse(row["exit_template_type"]),
                ItemTypePool = ParseEnumPool<ExitItemType>(row["item_type_pool"]),
                SpotTypePool = ParseEnumPool<ExitSpotType>(row["spot_type_pool"]),
                DebuffTypePool = ParseEnumPool<ExitDebuffType>(row["debuff_type_pool"]),
                ConditionTypePool = ParseEnumPool<ExitConditionType>(row["condition_type_pool"]),
                FinaleType = (ExitFinaleType)int.Parse(row["finale_type"]),
                EffectType = (ExitEffectType)int.Parse(row["effect_type"]),
                StepCount = int.Parse(row["step_count"])
            };
        }
    }
}
