// ReSharper disable All
#pragma warning disable CS8618

using System.Collections.Generic;
using System.Linq;
using network.common.data.helpers;

namespace network.common.data
{
    public static class GamePortalConditionData
    {
        private static readonly Dictionary<int, PortalConditionData> Conditions = new();
        private static readonly Dictionary<int, PortalConditionData> ConditionsByItemId = new();
        private static readonly Dictionary<AreaType, PortalConditionData> ConditionsByAreaType = new();

        public static void Initialize(List<CsvRow> data)
        {
            foreach (var row in data)
            {
                var condition = PortalConditionData.CreateFromData(row);
                Conditions[condition.Id] = condition;
                ConditionsByItemId[condition.RequiredItemId] = condition;
                ConditionsByAreaType[condition.TargetAreaType] = condition;
            }
        }

        public static PortalConditionData Get(int id)
        {
            return Conditions.GetValueOrDefault(id);
        }

        public static PortalConditionData GetByItemId(int itemId)
        {
            return ConditionsByItemId.GetValueOrDefault(itemId);
        }

        public static PortalConditionData GetByAreaType(AreaType areaType)
        {
            return ConditionsByAreaType.GetValueOrDefault(areaType);
        }

        /// <summary>
        /// 해당 구역이 조건부 포탈인지 확인
        /// </summary>
        public static bool IsConditionalPortal(AreaType areaType)
        {
            return ConditionsByAreaType.ContainsKey(areaType);
        }

        public static List<PortalConditionData> GetAll()
        {
            return Conditions.Values.ToList();
        }

        /// <summary>
        /// 특정 아이템 획득 시 포탈 조건이 충족되는지 확인
        /// </summary>
        public static bool CheckPortalTrigger(int itemId, out PortalConditionData condition)
        {
            condition = GetByItemId(itemId);
            return condition != null;
        }
    }

    public class PortalConditionData
    {
        public int Id { get; private set; }
        public int RequiredItemId { get; private set; }
        public AreaType TargetAreaType { get; private set; }
        public int MessageTextId { get; private set; }

        public string GetMessage()
        {
            return GameSystemTextData.GetText(MessageTextId);
        }

        public static PortalConditionData CreateFromData(CsvRow row)
        {
            return new PortalConditionData
            {
                Id = int.Parse(row["id"]),
                RequiredItemId = int.Parse(row["required_item_id"]),
                TargetAreaType = (AreaType)int.Parse(row["target_area_type"]),
                MessageTextId = int.Parse(row["message_text_id"])
            };
        }
    }
}
