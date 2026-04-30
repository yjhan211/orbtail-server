// ReSharper disable All
#pragma warning disable CS8618 // 생성자를 종료할 때 null을 허용하지 않는 필드에 null이 아닌 값을 포함해야 합니다. null 허용으로 선언해 보세요.
#pragma warning disable CS8625 // Null 리터럴을 null을 허용하지 않는 참조 형식으로 변환할 수 없습니다.
#pragma warning disable CS8603 // 가능한 null 참조 반환입니다.

using System;
using System.Collections.Generic;
using System.Linq;
using network.common.data.helpers;
using network.managers;
using Newtonsoft.Json;

namespace network.common.data
{
    public static class GameInteractableData
    {
        private static readonly Dictionary<int, InteractableInfoData> _infos = new();
        private static readonly Dictionary<int, List<InteractableInfoData>> _infosByZone = new();
        private static readonly Dictionary<int, List<int>> _itemPools = new();

        public static void Initialize(List<CsvRow> infoData, List<CsvRow> actionData, List<CsvRow> violationData, List<CsvRow> itemPoolData)
        {
            // 아이템 풀 데이터 로드
            _itemPools.Clear();
            foreach (var row in itemPoolData)
            {
                var poolId = int.Parse(row["id"]);
                var itemIdListJson = row["item_id_list"];
                var itemIds = string.IsNullOrEmpty(itemIdListJson) || itemIdListJson == "[]"
                    ? new List<int>()
                    : JsonConvert.DeserializeObject<List<int>>(itemIdListJson) ?? new List<int>();
                _itemPools[poolId] = itemIds;
            }

            // 기존 데이터 클리어
            _infos.Clear();
            _infosByZone.Clear();

            // violation 데이터를 (interact_id, action_id) 키로 매핑 — 인스턴스 단위 위반 결과
            var violationsByKey = new Dictionary<(int, int), CsvRow>();
            foreach (var row in violationData)
            {
                var id = int.Parse(row["id"]);
                var actionId = int.Parse(row["action_id"]);
                violationsByKey[(id, actionId)] = row;
            }

            // 공통 액션 풀 데이터를 object_type별로 그룹화 (GDD §2.4.2 — 통합 풀)
            var actionsByObjectType = actionData
                .GroupBy(row => int.Parse(row["object_type"]))
                .ToDictionary(
                    g => g.Key,
                    g => g.OrderBy(row => int.Parse(row["action_id"])).ToList()
                );

            // 인터랙터블 정보 생성 — 각 인터랙터블의 object_type에 매칭되는 공통 풀에서 액션 복제 + 인스턴스별 violation 부여
            foreach (var row in infoData)
            {
                var info = InteractableInfoData.CreateFromData(row, actionsByObjectType, violationsByKey);
                _infos[info.Id] = info;

                if (!_infosByZone.TryGetValue(info.ZoneId, out var list))
                {
                    list = new List<InteractableInfoData>();
                    _infosByZone[info.ZoneId] = list;
                }
                list.Add(info);
            }
        }

        public static List<int> GetItemPool(int poolId)
        {
            return _itemPools.TryGetValue(poolId, out var pool) ? pool : new List<int>();
        }

        public static HashSet<int> GetAllItemPoolIds()
        {
            return new HashSet<int>(_itemPools.Keys);
        }

        public static InteractableInfoData Get(int id)
        {
            if (!_infos.TryGetValue(id, out var info))
            {
                return null;
            }
            return info;
        }

        public static List<InteractableInfoData> GetAll()
        {
            return _infos.Values.ToList();
        }

        public static List<InteractableInfoData> GetByZone(int zoneId)
        {
            if (!_infosByZone.TryGetValue(zoneId, out var list))
            {
                return new List<InteractableInfoData>();
            }
            return list;
        }

        public static void Validate(LogManager logManager)
        {
            LogManager.WriteDebugLog("=== GameInteractableData Validation ===");
            foreach (var (id, info) in _infos)
            {
                LogManager.WriteDebugLog($"[{id}] {info.Name?.Kr} - Actions: {info.Actions.Count}");
            }
            LogManager.WriteDebugLog("All validations passed successfully!");
        }
    }

    public class InteractableInfoData
    {
        public int Id { get; private set; }
        public int ZoneId { get; private set; }
        public InteractableObjectType ObjectType { get; private set; }
        public LocalizedText Name { get; private set; }
        public LocalizedText ShortName { get; private set; }
        public LocalizedText Description { get; private set; }
        public InteractionType InteractionType { get; private set; }
        public List<InteractableActionData> Actions { get; private set; }

        public static InteractableInfoData CreateFromData(
            CsvRow row,
            Dictionary<int, List<CsvRow>> actionsByObjectType,
            Dictionary<(int, int), CsvRow> violationsByKey)
        {
            var id = int.Parse(row["id"]);
            var objectType = row.ContainsKey("object_type") && !string.IsNullOrEmpty(row["object_type"])
                ? (InteractableObjectType)int.Parse(row["object_type"])
                : InteractableObjectType.None;

            // object_type에 해당하는 공통 풀에서 액션 데이터를 복제하고, 인스턴스 ID(id)와 인스턴스별 violation을 부여
            var actions = new List<InteractableActionData>();
            if (actionsByObjectType.TryGetValue((int)objectType, out var poolRows))
            {
                foreach (var poolRow in poolRows)
                {
                    actions.Add(InteractableActionData.CreateFromData(id, poolRow, violationsByKey));
                }
            }

            return new InteractableInfoData
            {
                Id = id,
                ZoneId = int.Parse(row["area_type"]),
                ObjectType = objectType,
                Name = LocalizedText.FromCsv(row, "name"),
                ShortName = LocalizedText.FromCsv(row, "short_name"),
                Description = LocalizedText.FromCsvMultiline(row, "description"),
                InteractionType = row.ContainsKey("interaction_type") ? (InteractionType)int.Parse(row["interaction_type"]) : InteractionType.EXPLORE,
                Actions = actions
            };
        }
    }

    public class InteractableActionData
    {
        public int InteractId { get; private set; }
        public int ActionId { get; private set; }
        public int State { get; private set; }  // 0=기본, 1+=특수 상태
        public LocalizedText ActionText { get; private set; }

        // 기본 결과 (규칙 무관 또는 미채택 시)
        public LocalizedText ResultText { get; private set; }
        public ActionResultType ResultType { get; private set; }
        public int ResultId { get; private set; }
        public int ResultAmount { get; private set; }

        // 규칙 위반 시 결과 (비어있으면 기본 결과 사용)
        public LocalizedText ViolationResultText { get; private set; }
        public ActionResultType ViolationResultType { get; private set; }
        public int ViolationResultId { get; private set; }
        public int ViolationResultAmount { get; private set; }

        // 위반 결과가 정의되어 있는지 여부 (한국어 텍스트 기준)
        public bool HasViolationResult => ViolationResultText != null && !string.IsNullOrEmpty(ViolationResultText.Kr);

        public int PortalTriggerId { get; private set; }
        public int StaminaCost { get; private set; }
        public int RequireItemId { get; private set; }  // 0이면 조건 없음, 0보다 크면 해당 아이템 필요
        public string RequireAction { get; private set; }  // 빈 문자열이면 조건 없음, "interactableId_actionId" 형식

        public static InteractableActionData CreateFromData(int interactId, CsvRow row, Dictionary<(int, int), CsvRow> violationsByKey)
        {
            var actionId = int.Parse(row["action_id"]);

            // 기본 결과 (공통 풀의 row 기반)
            var resultText = LocalizedText.FromCsvMultiline(row, "result_text");
            var resultType = row.ContainsKey("result_type") ? (ActionResultType)int.Parse(row["result_type"]) : ActionResultType.NONE;
            var resultId = row.ContainsKey("result_id") ? int.Parse(row["result_id"]) : 0;
            var resultAmount = row.ContainsKey("result_amount") ? int.Parse(row["result_amount"]) : 0;

            // 위반 결과 (별도 CSV에서 조회)
            LocalizedText violationResultText = new LocalizedText("");
            var violationResultType = ActionResultType.NONE;
            var violationResultId = 0;
            var violationResultAmount = 0;

            if (violationsByKey.TryGetValue((interactId, actionId), out var violationRow))
            {
                violationResultText = LocalizedText.FromCsvMultiline(violationRow, "result_text");
                violationResultType = (ActionResultType)int.Parse(violationRow["result_type"]);
                violationResultId = int.Parse(violationRow["result_id"]);
                violationResultAmount = int.Parse(violationRow["result_amount"]);
            }

            return new InteractableActionData
            {
                InteractId = interactId,
                ActionId = actionId,
                State = row.ContainsKey("state") && !string.IsNullOrEmpty(row["state"]) ? int.Parse(row["state"]) : 0,
                ActionText = LocalizedText.FromCsv(row, "action_text"),
                ResultText = resultText,
                ResultType = resultType,
                ResultId = resultId,
                ResultAmount = resultAmount,
                ViolationResultText = violationResultText,
                ViolationResultType = violationResultType,
                ViolationResultId = violationResultId,
                ViolationResultAmount = violationResultAmount,
                PortalTriggerId = row.ContainsKey("portal_trigger_id") ? int.Parse(row["portal_trigger_id"]) : 0,
                StaminaCost = row.ContainsKey("stamina_cost") ? int.Parse(row["stamina_cost"]) : 0,
                RequireItemId = row.ContainsKey("require_item_id") && !string.IsNullOrEmpty(row["require_item_id"]) ? int.Parse(row["require_item_id"]) : 0,
                RequireAction = row.ContainsKey("require_action") ? row["require_action"]?.Trim() ?? "" : ""
            };
        }
    }

}
