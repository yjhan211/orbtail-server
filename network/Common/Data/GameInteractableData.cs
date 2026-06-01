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
        private static readonly Dictionary<int, List<int>> _areaItemPools = new();

        /// <summary>
        ///     #135 — 영역(AreaType) 단위 ItemPool. 자기 풀 외 사물 RNG 채집 시 영역 풀에서 추출.
        /// </summary>
        public static void InitializeAreaItemPool(List<CsvRow> areaItemPoolData)
        {
            _areaItemPools.Clear();
            foreach (var row in areaItemPoolData)
            {
                int areaType = int.Parse(row["area_type"]);
                var json = row["item_id_list"];
                var ids = string.IsNullOrEmpty(json) || json == "[]"
                    ? new List<int>()
                    : JsonConvert.DeserializeObject<List<int>>(json) ?? new List<int>();
                _areaItemPools[areaType] = ids;
            }
        }

        public static List<int> GetItemPoolByArea(int areaType) =>
            _areaItemPools.TryGetValue(areaType, out var pool) ? pool : new List<int>();

        public static void Initialize(List<CsvRow> infoData, List<CsvRow> actionData, List<CsvRow> itemPoolData)
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

            // 공통 액션 풀 데이터를 object_type별로 그룹화 (GDD §2.4.2 — 통합 풀)
            var actionsByObjectType = actionData
                .Where(IsDefaultObjectActionGroup)
                .GroupBy(row => int.Parse(row["object_type"]))
                .ToDictionary(
                    g => g.Key,
                    g => g.OrderBy(row => int.Parse(row["action_id"])).ToList()
                );

            // 인터랙터블 정보 생성 — 각 인터랙터블의 object_type에 매칭되는 공통 풀에서 액션 복제
            foreach (var row in infoData)
            {
                var info = InteractableInfoData.CreateFromData(row, actionsByObjectType);
                _infos[info.Id] = info;

                if (!_infosByZone.TryGetValue(info.ZoneId, out var list))
                {
                    list = new List<InteractableInfoData>();
                    _infosByZone[info.ZoneId] = list;
                }
                list.Add(info);
            }
        }

        private static bool IsDefaultObjectActionGroup(CsvRow row)
        {
            int objectType = int.Parse(row["object_type"]);
            if (!row.ContainsKey("action_group_key") || string.IsNullOrWhiteSpace(row["action_group_key"]))
                return true;

            return row["action_group_key"].Trim().Equals(
                $"object_{objectType}",
                StringComparison.OrdinalIgnoreCase);
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
                LogManager.WriteDebugLog($"[{id}] {info.ShortName?.Kr} - Actions: {info.Actions.Count}");
            }
            LogManager.WriteDebugLog("All validations passed successfully!");
        }
    }

    public class InteractableInfoData
    {
        public int Id { get; private set; }
        public int ZoneId { get; private set; }
        public InteractableObjectType ObjectType { get; private set; }
        public LocalizedText ShortName { get; private set; }
        public LocalizedText Description { get; private set; }
        public InteractionType InteractionType { get; private set; }
        public int CellX { get; private set; }
        public int CellY { get; private set; }
        public List<InteractableActionData> Actions { get; private set; }

        public static InteractableInfoData CreateFromData(
            CsvRow row,
            Dictionary<int, List<CsvRow>> actionsByObjectType)
        {
            var id = int.Parse(row["id"]);
            var objectType = row.ContainsKey("object_type") && !string.IsNullOrEmpty(row["object_type"])
                ? (InteractableObjectType)int.Parse(row["object_type"])
                : InteractableObjectType.None;

            // object_type에 해당하는 공통 풀에서 액션 데이터를 복제
            var actions = new List<InteractableActionData>();
            if (actionsByObjectType.TryGetValue((int)objectType, out var poolRows))
            {
                foreach (var poolRow in poolRows)
                {
                    actions.Add(InteractableActionData.CreateFromData(id, poolRow));
                }
            }

            return new InteractableInfoData
            {
                Id = id,
                ZoneId = int.Parse(row["area_type"]),
                ObjectType = objectType,
                ShortName = LocalizedText.FromCsv(row, "short_name"),
                Description = LocalizedText.FromCsvMultiline(row, "description"),
                InteractionType = row.ContainsKey("interaction_type") ? (InteractionType)int.Parse(row["interaction_type"]) : InteractionType.EXPLORE,
                CellX = row.ContainsKey("cell_x") && int.TryParse(row["cell_x"], out int cx) ? cx : 0,
                CellY = row.ContainsKey("cell_y") && int.TryParse(row["cell_y"], out int cy) ? cy : 0,
                Actions = actions
            };
        }
    }

    public class InteractableActionData
    {
        public int InteractId { get; private set; }
        public string ActionGroupKey { get; private set; }
        public int ActionId { get; private set; }
        public int State { get; private set; }  // 0=기본, 1+=특수 상태
        public LocalizedText ActionText { get; private set; }

        // 기본 결과 (RNG 채집 + 사보타주 등에서 사용)
        public LocalizedText ResultText { get; private set; }
        public ActionResultType ResultType { get; private set; }
        public int ResultId { get; private set; }
        public int ResultAmount { get; private set; }

        public int PortalTriggerId { get; private set; }
        public int StaminaCost { get; private set; }
        public int RequireItemId { get; private set; }  // 0이면 조건 없음, 0보다 크면 해당 아이템 필요
        public string RequireAction { get; private set; }  // 빈 문자열이면 조건 없음, "interactableId_actionId" 형식

        public static InteractableActionData CreateFromData(int interactId, CsvRow row)
        {
            var actionId = int.Parse(row["action_id"]);

            // 기본 결과 (공통 풀의 row 기반)
            var resultText = LocalizedText.FromCsvMultiline(row, "result_text");
            var resultType = row.ContainsKey("result_type") ? (ActionResultType)int.Parse(row["result_type"]) : ActionResultType.NONE;
            var resultId = row.ContainsKey("result_id") ? int.Parse(row["result_id"]) : 0;
            var resultAmount = row.ContainsKey("result_amount") ? int.Parse(row["result_amount"]) : 0;

            return new InteractableActionData
            {
                InteractId = interactId,
                ActionGroupKey = row.ContainsKey("action_group_key") ? row["action_group_key"]?.Trim() ?? "" : "",
                ActionId = actionId,
                State = row.ContainsKey("state") && !string.IsNullOrEmpty(row["state"]) ? int.Parse(row["state"]) : 0,
                ActionText = LocalizedText.FromCsv(row, "action_text"),
                ResultText = resultText,
                ResultType = resultType,
                ResultId = resultId,
                ResultAmount = resultAmount,
                PortalTriggerId = row.ContainsKey("portal_trigger_id") ? int.Parse(row["portal_trigger_id"]) : 0,
                StaminaCost = row.ContainsKey("stamina_cost") ? int.Parse(row["stamina_cost"]) : 0,
                RequireItemId = row.ContainsKey("require_item_id") && !string.IsNullOrEmpty(row["require_item_id"]) ? int.Parse(row["require_item_id"]) : 0,
                RequireAction = row.ContainsKey("require_action") ? row["require_action"]?.Trim() ?? "" : ""
            };
        }
    }

}
