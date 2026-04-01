// ReSharper disable All
#pragma warning disable CS8618 // 생성자를 종료할 때 null을 허용하지 않는 필드에 null이 아닌 값을 포함해야 합니다. null 허용으로 선언해 보세요.
#pragma warning disable CS8625 // Null 리터럴을 null을 허용하지 않는 참조 형식으로 변환할 수 없습니다.
#pragma warning disable CS8603 // 가능한 null 참조 반환입니다.

using System;
using System.Collections.Generic;
using System.Diagnostics.CodeAnalysis;
using System.Linq;
using network.common.data.helpers;
using network.managers;
using Newtonsoft.Json;

namespace network.common.data
{
    public static class GameItemData
    {
        private static readonly Dictionary<int, ItemInfoData> _items = new();

        public static void Initialize(List<CsvRow> baseItemData, List<CsvRow> equipmentData,
            List<CsvRow> consumableData, List<CsvRow> putData)
        {
            foreach (var baseInfo in baseItemData)
            {
                var itemId = int.Parse(baseInfo["id"]);
                var itemType = GetItemType(itemId);
                var additionalData = new Dictionary<string, CsvRow>();

                switch (itemType)
                {
                    case ItemType.EQUIPMENT:
                        var equipInfo = equipmentData.FirstOrDefault(x => x["item_id"] == itemId.ToString());
                        if (equipInfo != null)
                        {
                            additionalData["item_info_equipment"] = equipInfo;
                            if (string.IsNullOrEmpty(equipInfo["skin_name"]))
                            {
                                throw new ArgumentException($"Equipment item {itemId} missing skin_name");
                            }
                        }
                        break;

                    case ItemType.CONSUMABLE:
                        var consumableInfo = consumableData.FirstOrDefault(x => x["item_id"] == itemId.ToString());
                        if (consumableInfo != null)
                            additionalData["item_info_consumable"] = consumableInfo;
                        break;

                    case ItemType.INSTALLATION:
                        var installConsumableInfo = consumableData.FirstOrDefault(x => x["item_id"] == itemId.ToString());
                        if (installConsumableInfo != null)
                            additionalData["item_info_consumable"] = installConsumableInfo;
                        break;

                    case ItemType.PUTABLE:
                        var putInfo = putData.FirstOrDefault(x => x["item_id"] == itemId.ToString());
                        if (putInfo != null)
                            additionalData["item_info_put"] = putInfo;
                        break;
                }

                _items[itemId] = ItemInfoData.CreateFromData(baseInfo, additionalData);
            }
        }

        public static ItemInfoData Get(int id)
        {
            if (!_items.TryGetValue(id, out var item))
            {
                return null; // throw new KeyNotFoundException($"Item {id} not found");
            }

            return item;
        }

        public static List<ItemInfoData> GetAllList()
        {
            return _items.Values.ToList();
        }

        /// <summary>
        /// 특정 버프 서브타입을 가진 소비 아이템 목록 반환
        /// </summary>
        public static List<ItemInfoData> GetConsumablesByBuffSubType(BuffSubType buffSubType)
        {
            return _items.Values
                .Where(item => item.Type == ItemType.CONSUMABLE &&
                               item.ConsumableBuffList.Any(buff =>
                               {
                                   var buffData = GameBuffData.Get(buff.id);
                                   return buffData.SubType == buffSubType;
                               }))
                .ToList();
        }

        /// <summary>
        /// 특정 버프 서브타입을 가진 소비 아이템 중 랜덤 선택
        /// </summary>
        public static ItemInfoData GetRandomConsumableByBuffSubType(BuffSubType buffSubType, Random random = null)
        {
            var items = GetConsumablesByBuffSubType(buffSubType);
            if (items.Count == 0) return null;

            random ??= new Random();
            return items[random.Next(items.Count)];
        }

        public static ItemType GetItemType(int itemId)
        {
            return (ItemType)(itemId / 100000000);
        }

        public static EquipType GetEquipType(int itemId)
        {
            return (EquipType)(itemId / 1000000);
        }
    }

    [SuppressMessage("ReSharper", "UnusedAutoPropertyAccessor.Global")]
    [SuppressMessage("ReSharper", "MemberCanBePrivate.Global")]
    public class ItemInfoData
    {
        public int Id { get; private set; }
        public ItemType Type { get; private set; }
        public LocalizedText Name { get; private set; }
        public LocalizedText Comment { get; private set; }
        public List<int> Requirements { get; private set; }
        public bool Reusable { get; private set; }
        public string SkinName { get; private set; }

        // 장비, 설치 아이템 관련
        public int? MaxDurability { get; private set; }
        public List<(int id, int value1, int value2)> BuffList { get; private set; }

        // 소비/설치 아이템 버프 관련
        public List<(int id, int value, int interval)> ConsumableBuffList { get; private set; }

        // 설치 아이템 관련
        public int? MaxSell_items { get; private set; }

        // 아이템 타입 체크 헬퍼 메서드
        public bool IsEquipment => Type == ItemType.EQUIPMENT;
        public bool IsConsumable => Type == ItemType.CONSUMABLE;
        public bool IsInstallation => Type == ItemType.INSTALLATION;
        public bool IsMaterial => Type == ItemType.MATERIAL;
        public bool IsPutable => Type == ItemType.PUTABLE;

        // 하우징 아이템 관련
        public string PutSpritePath { get; private set; }
        public List<(float, float)> Slots { get; private set; }
        public PlayerState SlotState { get; private set; }

        public static ItemInfoData CreateFromData(CsvRow baseInfo, Dictionary<string, CsvRow> additionalData = null)
        {
            var id = int.Parse(baseInfo["id"]);
            var itemType = (ItemType)(id / 100000000);

            var item = new ItemInfoData
            {
                Id = id,
                Type = itemType,
                Name = LocalizedText.FromCsv(baseInfo, "name"),
                Comment = LocalizedText.FromCsv(baseInfo, "comment"),
                Requirements = JsonConvert.DeserializeObject<List<int>>(baseInfo["requirements"]) ?? new(),
                Reusable = int.Parse(baseInfo["reusable"]) == 1,
                BuffList = new(),
                ConsumableBuffList = new()
            };

            if (additionalData != null)
                switch (itemType)
                {
                    case ItemType.EQUIPMENT when additionalData.TryGetValue("item_info_equipment", out var equipInfo):
                        item.MaxDurability = int.Parse(equipInfo["max_durability"]);
                        item.BuffList = ParseBuffList(equipInfo["buff_list"]);
                        item.SkinName = equipInfo["skin_name"];
                        break;

                    case ItemType.CONSUMABLE
                        when additionalData.TryGetValue("item_info_consumable", out var consumableInfo):
                        item.ConsumableBuffList = ParseIntTupleArray(consumableInfo["buff_list"]);
                        break;

                    case ItemType.INSTALLATION
                        when additionalData.TryGetValue("item_info_consumable", out var installConsumableInfo):
                        item.ConsumableBuffList = ParseIntTupleArray(installConsumableInfo["buff_list"]);
                        break;

                    case ItemType.PUTABLE
                        when additionalData.TryGetValue("item_info_put", out var putInfo):
                        item.PutSpritePath = putInfo["sprite_path"];
                        item.Slots = ParseFloatTupleArray(putInfo["slot_list"]);
                        item.SlotState = PlayerState.Parse<PlayerState>(int.Parse(putInfo["player_state"]).ToString());
                        break;
                }

            return item;
        }

        private static List<(int id, int coolTime, int value)> ParseBuffList(string jsonString)
        {
            if (string.IsNullOrEmpty(jsonString) || jsonString == "[]") return new();

            var arrays = JsonConvert.DeserializeObject<List<int[]>>(jsonString);
            return arrays?.Select(arr => (id: arr[0], coolTime: arr[1], value: arr[2])).ToList();
        }

        public static List<(int id, int value, int interval)> ParseIntTupleArray(string jsonString)
        {
            if (string.IsNullOrEmpty(jsonString) || jsonString == "[]") return new();

            var arrays = JsonConvert.DeserializeObject<List<int[]>>(jsonString);

            return arrays?.Select(arr => (id: arr[0], value: arr[1], interval: arr.Length > 2 ? arr[2] : 0)).ToList();
        }

        public static List<(float id, float value)> ParseFloatTupleArray(string jsonString)
        {
            if (string.IsNullOrEmpty(jsonString) || jsonString == "[]") return new();

            var arrays = JsonConvert.DeserializeObject<List<float[]>>(jsonString);

            return arrays?.Select(arr => (id: arr[0], value: arr[1])).ToList();
        }

        public string GetBuffComment()
        {
            if (BuffList.Count <= 0 && ConsumableBuffList.Count <= 0)
            {
                return "발동 효과 없음";
            }

            var result = "";
            if (ConsumableBuffList.Count > 0)
            {
                foreach (var buffInfo in ConsumableBuffList)
                {
                    var buff = GameBuffData.Get(buffInfo.id);
                    result += buff.Comment
                        .Replace("{value1}", $"{buffInfo.value}")
                        .Replace("{value2}", $"{buffInfo.interval}");
                }
            }
            else
            {
                foreach (var buffInfo in BuffList)
                {
                    var buff = GameBuffData.Get(buffInfo.id);
                    result += buff.Comment.Replace("{value1}", $"{buffInfo.value1}").Replace("{value2}", $"{buffInfo.value2}");
                }
            }

            return result;
        }
    }
}
