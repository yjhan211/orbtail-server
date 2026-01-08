// ReSharper disable All
#pragma warning disable CS8618 // 생성자를 종료할 때 null을 허용하지 않는 필드에 null이 아닌 값을 포함해야 합니다. null 허용으로 선언해 보세요.
#pragma warning disable CS8625 // Null 리터럴을 null을 허용하지 않는 참조 형식으로 변환할 수 없습니다.
#pragma warning disable CS8603 // 가능한 null 참조 반환입니다.

using System;
using System.Collections.Generic;
using System.Linq;
using Newtonsoft.Json;
using network.common.data.helpers;
using network.managers;

namespace network.common.data
{
    public static class GameInteractableData
    {
        private static readonly Dictionary<int, InteractableInfoData> Infos = new();
        private static readonly Dictionary<int, List<InteractableInfoData>> InfosByZone = new();
        private static readonly Dictionary<int, List<int>> ItemPools = new();

        public static void Initialize(List<CsvRow> infoData, List<CsvRow> actionData, List<CsvRow> itemPoolData)
        {
            // 아이템 풀 데이터 로드
            ItemPools.Clear();
            foreach (var row in itemPoolData)
            {
                var poolId = int.Parse(row["pool_id"]);
                var itemIdListJson = row["item_id_list"].Trim('"');
                var itemIds = string.IsNullOrEmpty(itemIdListJson) || itemIdListJson == "[]"
                    ? new List<int>()
                    : JsonConvert.DeserializeObject<List<int>>(itemIdListJson) ?? new List<int>();
                ItemPools[poolId] = itemIds;
            }

            // 기존 데이터 클리어
            Infos.Clear();
            InfosByZone.Clear();

            // 액션 데이터를 id별로 그룹화
            var actionsByInteractId = actionData
                .GroupBy(row => int.Parse(row["id"]))
                .ToDictionary(
                    g => g.Key,
                    g => g.OrderBy(row => int.Parse(row["action_id"]))
                          .Select(InteractableActionData.CreateFromData)
                          .ToList()
                );

            // 인터랙터블 정보 생성
            foreach (var row in infoData)
            {
                var info = InteractableInfoData.CreateFromData(row, actionsByInteractId);
                Infos[info.Id] = info;

                if (!InfosByZone.TryGetValue(info.ZoneId, out var list))
                {
                    list = new List<InteractableInfoData>();
                    InfosByZone[info.ZoneId] = list;
                }
                list.Add(info);
            }
        }

        public static List<int> GetItemPool(int poolId)
        {
            return ItemPools.TryGetValue(poolId, out var pool) ? pool : new List<int>();
        }

        public static InteractableInfoData Get(int id)
        {
            if (!Infos.TryGetValue(id, out var info))
            {
                return null;
            }
            return info;
        }

        public static List<InteractableInfoData> GetAll()
        {
            return Infos.Values.ToList();
        }

        public static List<InteractableInfoData> GetByZone(int zoneId)
        {
            if (!InfosByZone.TryGetValue(zoneId, out var list))
            {
                return new List<InteractableInfoData>();
            }
            return list;
        }

        public static void Validate(LogManager logManager)
        {
            LogManager.WriteDebugLog("=== GameInteractableData Validation ===");
            foreach (var (id, info) in Infos)
            {
                LogManager.WriteDebugLog($"[{id}] {info.Name} - Actions: {info.Actions.Count}");
            }
            LogManager.WriteDebugLog("All validations passed successfully!");
        }
    }

    public class InteractableInfoData
    {
        public int Id { get; private set; }
        public int ZoneId { get; private set; }
        public string Name { get; private set; }
        public string ShortName { get; private set; }
        public string Description { get; private set; }
        public InteractionType InteractionType { get; private set; }
        public List<InteractableActionData> Actions { get; private set; }

        public static InteractableInfoData CreateFromData(CsvRow row, Dictionary<int, List<InteractableActionData>> actionsByInteractId)
        {
            var id = int.Parse(row["id"]);

            return new InteractableInfoData
            {
                Id = id,
                ZoneId = int.Parse(row["area_type"]),
                Name = row["name"].Trim('"'),
                ShortName = row["short_name"].Trim('"'),
                Description = row["description"].Trim('"').Replace("\\n", "\n"),
                InteractionType = row.ContainsKey("interaction_type") ? (InteractionType)int.Parse(row["interaction_type"]) : InteractionType.EXPLORE,
                Actions = actionsByInteractId.TryGetValue(id, out var actions) ? actions : new List<InteractableActionData>()
            };
        }
    }

    public class InteractableActionData
    {
        public int InteractId { get; private set; }
        public int ActionId { get; private set; }
        public string ActionText { get; private set; }

        // 기본 결과 (규칙 무관 또는 미채택 시)
        public string ResultText { get; private set; }
        public ActionResultType ResultType { get; private set; }
        public int ResultId { get; private set; }
        public int ResultAmount { get; private set; }

        // 규칙 위반 시 결과 (비어있으면 기본 결과 사용)
        public string ViolationResultText { get; private set; }
        public ActionResultType ViolationResultType { get; private set; }
        public int ViolationResultId { get; private set; }
        public int ViolationResultAmount { get; private set; }

        // 위반 결과가 정의되어 있는지 여부
        public bool HasViolationResult => !string.IsNullOrEmpty(ViolationResultText);

        public int PortalTriggerId { get; private set; }
        public int StaminaCost { get; private set; }

        public static InteractableActionData CreateFromData(CsvRow row)
        {
            // 기본 결과
            var resultText = row["result_text"].Trim('"').Replace("\\n", "\n");
            var resultType = row.ContainsKey("result_type") ? (ActionResultType)int.Parse(row["result_type"]) : ActionResultType.NONE;
            var resultId = row.ContainsKey("result_id") ? int.Parse(row["result_id"]) : 0;
            var resultAmount = row.ContainsKey("result_amount") ? int.Parse(row["result_amount"]) : 0;

            // 위반 결과 (비어있으면 기본 결과 사용)
            var violationResultText = row.ContainsKey("violation_result_text") && !string.IsNullOrEmpty(row["violation_result_text"])
                ? row["violation_result_text"].Trim('"').Replace("\\n", "\n")
                : "";
            var violationResultType = row.ContainsKey("violation_result_type") && !string.IsNullOrEmpty(row["violation_result_type"])
                ? (ActionResultType)int.Parse(row["violation_result_type"])
                : ActionResultType.NONE;
            var violationResultId = row.ContainsKey("violation_result_id") && !string.IsNullOrEmpty(row["violation_result_id"])
                ? int.Parse(row["violation_result_id"])
                : 0;
            var violationResultAmount = row.ContainsKey("violation_result_amount") && !string.IsNullOrEmpty(row["violation_result_amount"])
                ? int.Parse(row["violation_result_amount"])
                : 0;

            return new InteractableActionData
            {
                InteractId = int.Parse(row["id"]),
                ActionId = int.Parse(row["action_id"]),
                ActionText = row["action_text"],
                ResultText = resultText,
                ResultType = resultType,
                ResultId = resultId,
                ResultAmount = resultAmount,
                ViolationResultText = violationResultText,
                ViolationResultType = violationResultType,
                ViolationResultId = violationResultId,
                ViolationResultAmount = violationResultAmount,
                PortalTriggerId = row.ContainsKey("portal_trigger_id") ? int.Parse(row["portal_trigger_id"]) : 0,
                StaminaCost = row.ContainsKey("stamina_cost") ? int.Parse(row["stamina_cost"]) : 0
            };
        }
    }

}
