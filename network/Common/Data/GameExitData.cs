// ReSharper disable All
#pragma warning disable CS8618

using System;
using System.Collections.Generic;
using System.Linq;
using Newtonsoft.Json;
using network.common.data.helpers;

namespace network.common.data
{
    public static class GameExitData
    {
        private static readonly Dictionary<int, ExitTemplateData> Templates = new();
        private static readonly Dictionary<int, ExitStepData> Steps = new();
        private static readonly Dictionary<int, ExitItemData> Items = new();
        private static readonly Dictionary<int, ExitSpotData> Spots = new();
        private static readonly Dictionary<int, ExitDebuffData> Debuffs = new();
        private static readonly Dictionary<int, ExitConditionData> Conditions = new();
        private static readonly List<ExitConstraintData> Constraints = new();

        public static void Initialize(
            List<CsvRow> templateData,
            List<CsvRow> stepData,
            List<CsvRow> itemData,
            List<CsvRow> spotData,
            List<CsvRow> debuffData,
            List<CsvRow> conditionData,
            List<CsvRow> constraintData)
        {
            // Templates
            foreach (var row in templateData)
            {
                var id = int.Parse(row["id"]);
                Templates[id] = new ExitTemplateData
                {
                    Id = id,
                    Name = row["name"].Trim('"'),
                    Description = row["description"].Trim('"')
                };
            }

            // Steps
            foreach (var row in stepData)
            {
                var id = int.Parse(row["id"]);
                Steps[id] = new ExitStepData
                {
                    Id = id,
                    TemplateType = int.Parse(row["exit_template_type"]),
                    StepOrder = int.Parse(row["step_order"]),
                    TextTemplate = row["text_template"].Trim('"'),
                    InsanityTextTemplate = row.ContainsKey("insanity_text_template") ? row["insanity_text_template"].Trim('"') : "",
                    SummaryTemplate = row["summary_template"].Trim('"'),
                    ItemSlot = row["item_slot"].ToLower() == "true",
                    SpotSlot = row["spot_slot"].ToLower() == "true",
                    DebuffSlot = row["debuff_slot"].ToLower() == "true",
                    ConditionSlot = row["condition_slot"].ToLower() == "true",
                    ActionType = int.Parse(row["action_type"]),
                    TargetInteractableId = row["target_interactable_id"],
                    SpotActionText = row.ContainsKey("spot_action_text") ? row["spot_action_text"].Trim('"') : ""
                };
            }

            // Items
            foreach (var row in itemData)
            {
                var id = int.Parse(row["id"]);
                Items[id] = new ExitItemData
                {
                    Id = id,
                    ItemId = int.Parse(row["item_id"]),
                    Warning = row["warning"].Trim('"')
                };
            }

            // Spots
            foreach (var row in spotData)
            {
                var id = int.Parse(row["id"]);
                Spots[id] = new ExitSpotData
                {
                    Id = id,
                    InteractableId = int.Parse(row["interactable_id"])
                };
            }

            // Debuffs
            foreach (var row in debuffData)
            {
                var id = int.Parse(row["id"]);
                Debuffs[id] = new ExitDebuffData
                {
                    Id = id,
                    Warning = row["warning"].Trim('"'),
                    EffectType = int.Parse(row["effect_type"]),
                    EffectValue = float.Parse(row["effect_value"])
                };
            }

            // Conditions
            foreach (var row in conditionData)
            {
                var id = int.Parse(row["id"]);
                Conditions[id] = new ExitConditionData
                {
                    Id = id,
                    Text = row["text"].Trim('"'),
                    CheckType = int.Parse(row["check_type"]),
                    CheckValue = int.Parse(row["check_value"]),
                    FinaleType = int.Parse(row["finale_type"])
                };
            }

            // Constraints
            foreach (var row in constraintData)
            {
                Constraints.Add(new ExitConstraintData
                {
                    Id = int.Parse(row["id"]),
                    TemplateType = int.Parse(row["exit_template_type"]),
                    ConstraintType = int.Parse(row["constraint_type"]),
                    SlotType = int.Parse(row["slot_type"]),
                    Values = ParseIntArray(row["values"]),
                    ConditionSlotType = int.Parse(row["condition_slot_type"]),
                    ConditionValues = ParseIntArray(row["condition_values"])
                });
            }
        }

        private static List<int> ParseIntArray(string value)
        {
            if (string.IsNullOrEmpty(value) || value == "[]")
                return new List<int>();

            value = value.Trim('"');
            return JsonConvert.DeserializeObject<List<int>>(value) ?? new List<int>();
        }

        // Getters
        public static ExitTemplateData GetTemplate(int id) => Templates.GetValueOrDefault(id);
        public static ExitStepData GetStep(int id) => Steps.GetValueOrDefault(id);
        public static ExitItemData GetItem(int id) => Items.GetValueOrDefault(id);
        public static ExitSpotData GetSpot(int id) => Spots.GetValueOrDefault(id);
        public static ExitDebuffData GetDebuff(int id) => Debuffs.GetValueOrDefault(id);
        public static ExitConditionData GetCondition(int id) => Conditions.GetValueOrDefault(id);

        public static List<ExitTemplateData> GetAllTemplates() => Templates.Values.ToList();
        public static List<ExitStepData> GetStepsByTemplate(int templateType) =>
            Steps.Values.Where(s => s.TemplateType == templateType).OrderBy(s => s.StepOrder).ToList();
        public static List<ExitItemData> GetAllItems() => Items.Values.ToList();
        public static List<ExitSpotData> GetAllSpots() => Spots.Values.ToList();
        public static List<ExitDebuffData> GetAllDebuffs() => Debuffs.Values.ToList();
        public static List<ExitConditionData> GetAllConditions() => Conditions.Values.ToList();
        public static List<ExitConstraintData> GetConstraintsByTemplate(int templateType) =>
            Constraints.Where(c => c.TemplateType == 0 || c.TemplateType == templateType).ToList();

        public static void Validate(managers.LogManager logger)
        {
            // 기본 검증
            if (Templates.Count == 0)
                throw new InvalidOperationException("No exit templates loaded");
            if (Steps.Count == 0)
                throw new InvalidOperationException("No exit steps loaded");
        }
    }

    public class ExitTemplateData
    {
        public int Id { get; set; }
        public string Name { get; set; }
        public string Description { get; set; }
    }

    public class ExitStepData
    {
        public int Id { get; set; }
        public int TemplateType { get; set; }
        public int StepOrder { get; set; }
        public string TextTemplate { get; set; }
        public string InsanityTextTemplate { get; set; }
        public string SummaryTemplate { get; set; }
        public bool ItemSlot { get; set; }
        public bool SpotSlot { get; set; }
        public bool DebuffSlot { get; set; }
        public bool ConditionSlot { get; set; }
        public int ActionType { get; set; }
        public string TargetInteractableId { get; set; }
        public string SpotActionText { get; set; }
    }

    public class ExitItemData
    {
        public int Id { get; set; }
        public int ItemId { get; set; }
        public string Warning { get; set; }
    }

    public class ExitSpotData
    {
        public int Id { get; set; }
        public int InteractableId { get; set; }
    }

    public class ExitDebuffData
    {
        public int Id { get; set; }
        public string Warning { get; set; }
        public int EffectType { get; set; }
        public float EffectValue { get; set; }
    }

    public class ExitConditionData
    {
        public int Id { get; set; }
        public string Text { get; set; }
        public int CheckType { get; set; }
        public int CheckValue { get; set; }
        public int FinaleType { get; set; }
    }

    public class ExitConstraintData
    {
        public int Id { get; set; }
        public int TemplateType { get; set; }
        public int ConstraintType { get; set; }
        public int SlotType { get; set; }
        public List<int> Values { get; set; }
        public int ConditionSlotType { get; set; }
        public List<int> ConditionValues { get; set; }
    }
}
