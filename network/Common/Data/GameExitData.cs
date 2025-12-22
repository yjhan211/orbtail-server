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
        private static readonly Dictionary<int, ExitConditionData> Conditions = new();
        private static readonly List<ExitConstraintData> Constraints = new();

        public static void Initialize(
            List<CsvRow> templateData,
            List<CsvRow> stepData,
            List<CsvRow> itemData,
            List<CsvRow> spotData,
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

                // 신/구 형식 호환성 처리
                var useNewFormat = row.ContainsKey("text_id");

                Steps[id] = new ExitStepData
                {
                    Id = id,
                    TemplateType = int.Parse(row["exit_template_type"]),
                    StepOrder = int.Parse(row["step_order"]),
                    TextId = useNewFormat ? int.Parse(row["text_id"]) : 0,
                    InsanityTextId = useNewFormat ? int.Parse(row["insanity_text_id"]) : 0,
                    SummaryTextId = useNewFormat ? int.Parse(row["summary_text_id"]) : 0,
                    // 구형식: "true"/"false", 신형식: "1"/"0"
                    ItemSlot = row["item_slot"].ToLower() == "true" || row["item_slot"] == "1",
                    SpotSlot = row["spot_slot"].ToLower() == "true" || row["spot_slot"] == "1",
                    ConditionSlot = row["condition_slot"].ToLower() == "true" || row["condition_slot"] == "1",
                    ActionType = int.Parse(row["action_type"]),
                    TargetInteractableId = row.ContainsKey("target_interactable_id") ? row["target_interactable_id"] : "",
                    SpotActionTextId = useNewFormat && row.ContainsKey("spot_action_text_id") ? int.Parse(row["spot_action_text_id"]) : 0,
                    // 구형식 텍스트 직접 저장 (호환성)
                    _legacyTextTemplate = !useNewFormat && row.ContainsKey("text_template") ? row["text_template"].Trim('"') : null,
                    _legacyInsanityTextTemplate = !useNewFormat && row.ContainsKey("insanity_text_template") ? row["insanity_text_template"].Trim('"') : null,
                    _legacySummaryTemplate = !useNewFormat && row.ContainsKey("summary_template") ? row["summary_template"].Trim('"') : null,
                    _legacySpotActionText = !useNewFormat && row.ContainsKey("spot_action_text") ? row["spot_action_text"].Trim('"') : null
                };
            }

            // Items (item_id를 키로 직접 사용)
            foreach (var row in itemData)
            {
                var itemId = int.Parse(row["item_id"]);
                Items[itemId] = new ExitItemData
                {
                    ItemId = itemId,
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
        public static ExitConditionData GetCondition(int id) => Conditions.GetValueOrDefault(id);

        public static List<ExitTemplateData> GetAllTemplates() => Templates.Values.ToList();
        public static List<ExitStepData> GetStepsByTemplate(int templateType) =>
            Steps.Values.Where(s => s.TemplateType == templateType).OrderBy(s => s.StepOrder).ToList();
        public static List<ExitItemData> GetAllItems() => Items.Values.ToList();
        public static List<ExitSpotData> GetAllSpots() => Spots.Values.ToList();
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
        public int TextId { get; set; }
        public int InsanityTextId { get; set; }
        public int SummaryTextId { get; set; }
        public bool ItemSlot { get; set; }
        public bool SpotSlot { get; set; }
        public bool ConditionSlot { get; set; }
        public int ActionType { get; set; }
        public string TargetInteractableId { get; set; }
        public int SpotActionTextId { get; set; }

        // 구형식 호환성을 위한 레거시 필드
        internal string _legacyTextTemplate { get; set; }
        internal string _legacyInsanityTextTemplate { get; set; }
        internal string _legacySummaryTemplate { get; set; }
        internal string _legacySpotActionText { get; set; }

        // 호환성을 위한 텍스트 프로퍼티 (신형식: TextId에서 텍스트 가져오기, 구형식: 레거시 필드 사용)
        public string TextTemplate => _legacyTextTemplate ?? GameSystemTextData.GetText(TextId);
        public string InsanityTextTemplate => _legacyInsanityTextTemplate ?? GameSystemTextData.GetText(InsanityTextId);
        public string SummaryTemplate => _legacySummaryTemplate ?? GameSystemTextData.GetText(SummaryTextId);
        public string SpotActionText => _legacySpotActionText ?? (SpotActionTextId > 0 ? GameSystemTextData.GetText(SpotActionTextId) : "");
    }

    public class ExitItemData
    {
        public int ItemId { get; set; }  // item_info.csv의 아이템 ID (키로 사용)
        public string Warning { get; set; }
    }

    public class ExitSpotData
    {
        public int Id { get; set; }
        public int InteractableId { get; set; }
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
