// ReSharper disable All
#pragma warning disable CS8618

using System.Collections.Generic;
using Newtonsoft.Json;
using network.common.data.helpers;

namespace network.common.data
{
    public static class GameExitData
    {
        private static readonly Dictionary<int, List<ExitStepData>> StepsByGroup = new();

        public static void Initialize(List<CsvRow> stepData)
        {
            // Steps
            StepsByGroup.Clear();
            foreach (var row in stepData)
            {
                var groupId = int.Parse(row["group_id"]);
                var stepOrder = int.Parse(row["step_order"]);

                var step = new ExitStepData
                {
                    GroupId = groupId,
                    StepOrder = stepOrder,
                    TargetItemIds = ParseIntArray(row["target_item_id"]),
                    TargetInteractableActions = ParseStringArray(row["target_interactable_action"]),
                    TargetAreas = ParseIntArray(row["target_area"]),
                    TextTemplate = row["text_template"].Trim('"'),
                    InsanityTextTemplate = row["insanity_text_template"].Trim('"'),
                    SummaryTemplate = row["summary_template"].Trim('"')
                };

                if (!StepsByGroup.ContainsKey(groupId))
                    StepsByGroup[groupId] = new List<ExitStepData>();

                StepsByGroup[groupId].Add(step);
            }

            // 각 그룹 내에서 step_order로 정렬
            foreach (var group in StepsByGroup.Values)
            {
                group.Sort((a, b) => a.StepOrder.CompareTo(b.StepOrder));
            }
        }

        private static List<int> ParseIntArray(string json)
        {
            var trimmed = json?.Trim('"') ?? "";
            if (string.IsNullOrEmpty(trimmed) || trimmed == "[]")
                return new List<int>();

            return JsonConvert.DeserializeObject<List<int>>(trimmed) ?? new List<int>();
        }

        private static List<string> ParseStringArray(string value)
        {
            var trimmed = value?.Trim('"') ?? "";
            if (string.IsNullOrEmpty(trimmed) || trimmed == "[]")
                return new List<string>();

            // 파이프(|)로 구분된 형식 지원: "701000006_1|701000007_1"
            if (trimmed.Contains('|'))
            {
                return new List<string>(trimmed.Split('|'));
            }

            // 기존 JSON 배열 형식도 지원 (빈 배열이 아닌 경우)
            if (trimmed.StartsWith("["))
            {
                return JsonConvert.DeserializeObject<List<string>>(trimmed) ?? new List<string>();
            }

            // 단일 값인 경우
            return new List<string> { trimmed };
        }

        // Getters
        public static List<ExitStepData> GetStepsByGroup(int groupId) =>
            StepsByGroup.GetValueOrDefault(groupId) ?? new List<ExitStepData>();

        public static void Validate(managers.LogManager logger)
        {
            // 기본 검증
            if (StepsByGroup.Count == 0)
                throw new System.InvalidOperationException("No exit steps loaded");
        }
    }

    public class ExitStepData
    {
        public int GroupId { get; set; }
        public int StepOrder { get; set; }
        public List<int> TargetItemIds { get; set; }
        public List<string> TargetInteractableActions { get; set; }  // "interactableId_actionId" 형태
        public List<int> TargetAreas { get; set; }
        public string TextTemplate { get; set; }
        public string InsanityTextTemplate { get; set; }
        public string SummaryTemplate { get; set; }
    }
}
