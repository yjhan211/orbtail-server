// ReSharper disable All
#pragma warning disable CS8618

using System.Collections.Generic;
using System.Linq;
using network.common.data.helpers;

namespace network.common.data
{
    /// <summary>
    ///     선행 아이템 데이터 (v0.2.0). 일부 부품은 선행 아이템 소지 시만 회수 가능.
    ///     장갑(group 1) DC/SC/HE 공유 — 블러프 친화.
    /// </summary>
    public static class PrerequisiteItemData
    {
        // target_part_id → PrerequisiteItem (그 부품에 필요한 선행)
        private static readonly Dictionary<int, PrerequisiteItem> _byTargetPart = new();
        // share_group → 같은 그룹 부품 목록 (예: group 1 = 장갑이 적용되는 모든 부품)
        private static readonly Dictionary<int, List<PrerequisiteItem>> _byShareGroup = new();

        public static void Initialize(List<CsvRow> data)
        {
            _byTargetPart.Clear();
            _byShareGroup.Clear();

            foreach (var row in data)
            {
                var item = new PrerequisiteItem
                {
                    ShareGroup = int.Parse(row["share_group"]),
                    ItemNameKr = row["item_name_kr"],
                    ItemNameEn = row.ContainsKey("item_name_en") ? row["item_name_en"] : "",
                    ItemNameJp = row.ContainsKey("item_name_jp") ? row["item_name_jp"] : "",
                    TargetPartId = int.Parse(row["target_part_id"]),
                    LocationArea = int.Parse(row["location_area"]),
                    LocationObjectType = int.Parse(row["location_object_type"]),
                    DescriptionKr = row["description_kr"],
                    DescriptionEn = row.ContainsKey("description_en") ? row["description_en"] : "",
                    DescriptionJp = row.ContainsKey("description_jp") ? row["description_jp"] : "",
                    SpriteItemId = row.ContainsKey("sprite_item_id") && !string.IsNullOrEmpty(row["sprite_item_id"])
                        ? int.Parse(row["sprite_item_id"])
                        : 0
                };

                _byTargetPart[item.TargetPartId] = item;

                if (!_byShareGroup.ContainsKey(item.ShareGroup))
                    _byShareGroup[item.ShareGroup] = new List<PrerequisiteItem>();
                _byShareGroup[item.ShareGroup].Add(item);
            }
        }

        /// <summary>
        ///     해당 부품 회수에 필요한 선행 아이템 (없으면 null)
        /// </summary>
        public static PrerequisiteItem GetForPart(int partId) =>
            _byTargetPart.GetValueOrDefault(partId);

        public static List<PrerequisiteItem> GetAll() =>
            _byTargetPart.Values.ToList();

        /// <summary>
        ///     share_group의 대표 아이템 (장갑 = group 1 → 대표 1개로 그룹 식별)
        /// </summary>
        public static PrerequisiteItem GetGroupRepresentative(int shareGroup) =>
            _byShareGroup.TryGetValue(shareGroup, out var list) && list.Count > 0 ? list[0] : null;
    }

    public class PrerequisiteItem
    {
        public int ShareGroup { get; set; }      // 0=없음, 1+=공유 그룹 ID
        public string ItemNameKr { get; set; }    // 예: "장갑"
        public string ItemNameEn { get; set; }
        public string ItemNameJp { get; set; }
        public int TargetPartId { get; set; }     // 이 선행이 필요한 부품 ID (해당 행 적용)
        public int LocationArea { get; set; }     // 선행 아이템 발견 위치 area
        public int LocationObjectType { get; set; } // 선행 아이템 발견 위치 object_type
        public string DescriptionKr { get; set; } // 미충족 시 안내 문구
        public string DescriptionEn { get; set; }
        public string DescriptionJp { get; set; }
        public int SpriteItemId { get; set; }     // ItemSprites/<id>.png 재활용 (#79)

        public string GetName(string lang) =>
            lang switch
            {
                "en" => string.IsNullOrWhiteSpace(ItemNameEn) ? ItemNameKr : ItemNameEn,
                "jp" => string.IsNullOrWhiteSpace(ItemNameJp) ? ItemNameKr : ItemNameJp,
                _ => ItemNameKr
            };

        public string GetDescription(string lang) =>
            lang switch
            {
                "en" => string.IsNullOrWhiteSpace(DescriptionEn) ? DescriptionKr : DescriptionEn,
                "jp" => string.IsNullOrWhiteSpace(DescriptionJp) ? DescriptionKr : DescriptionJp,
                _ => DescriptionKr
            };
    }
}
