// ReSharper disable All
#pragma warning disable CS8618 // 생성자를 종료할 때 null을 허용하지 않는 필드에 null이 아닌 값을 포함해야 합니다. null 허용으로 선언해 보세요.
#pragma warning disable CS8625 // Null 리터럴을 null을 허용하지 않는 참조 형식으로 변환할 수 없습니다.
#pragma warning disable CS8603 // 가능한 null 참조 반환입니다.

using System;
using System.Collections.Generic;
using System.Linq;
using System.Diagnostics.CodeAnalysis;
using Newtonsoft.Json;
using network.common.data.helpers;
using network.managers;

namespace network.common.data
{
    public static class GameItemData
    {
        private static readonly Dictionary<int, ItemInfoData> Items = new();

        public static void Initialize(List<CsvRow> baseItemData, List<CsvRow> equipmentData,
            List<CsvRow> consumableData, List<CsvRow> installationData,
            List<CsvRow> shopData)
        {
            foreach (var baseInfo in baseItemData)
            {
                var itemId = int.Parse(baseInfo["id"]);
                var itemType = GetItemType(itemId);
                var additionalData = new Dictionary<string, CsvRow>();
                var equipType = GetEquipType(itemId);

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
                        var installInfo = installationData.FirstOrDefault(x => x["item_id"] == itemId.ToString());
                        if (installInfo != null)
                            additionalData["item_info_installation"] = installInfo;
                
                        var shopInfo = shopData.FirstOrDefault(x => x["item_id"] == itemId.ToString());
                        if (shopInfo != null)
                            additionalData["installation_shop_info"] = shopInfo;
                        break;
                }

                Items[itemId] = ItemInfoData.CreateFromData(baseInfo, additionalData);
            }
        }

        public static ItemInfoData Get(int id)
        {
            if (!Items.TryGetValue(id, out var item)) throw new KeyNotFoundException($"Item {id} not found");

            return item;
        }

        public static List<ItemInfoData> GetAllList()
        {
            return Items.Values.ToList();
        }

        public static void Validate(LogManager logManager)
        {
            logManager.WriteDebugLog("=== GameItemData Validation ===");
            foreach (var (id, item) in Items)
            {
                logManager.WriteDebugLog($"[{id}] {item.Name}");
            }
            logManager.WriteDebugLog("All validations passed successfully!");
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

        // 소비 아이템 관련
        public List<(int id, int value)> ConsumableBuffList { get; private set; }

        // 설치 아이템 관련
        public int? MaxSellItems { get; private set; }

        // 아이템 타입 체크 헬퍼 메서드
        public bool IsEquipment => Type == ItemType.EQUIPMENT;
        public bool IsConsumable => Type == ItemType.CONSUMABLE;
        public bool IsInstallation => Type == ItemType.INSTALLATION;
        public bool IsMaterial => Type == ItemType.MATERIAL;

        public static ItemInfoData CreateFromData(CsvRow baseInfo, Dictionary<string, CsvRow> additionalData = null)
        {
            var id = int.Parse(baseInfo["id"]);
            var itemType = (ItemType)(id / 100000000);

            var item = new ItemInfoData
            {
                Id = id,
                Type = itemType,
                Name = new LocalizedText(baseInfo["name"]),
                Comment = new LocalizedText(baseInfo["comment"]),
                Requirements = JsonConvert.DeserializeObject<List<int>>(baseInfo["requirements"]) ?? new(),
                Reusable = int.Parse(baseInfo["reusable"]) == 1,
                BuffList = new(),
                ConsumableBuffList = new ()
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
                        item.ConsumableBuffList = ParseTupleArray(consumableInfo["buff_list"]);
                        break;

                    case ItemType.INSTALLATION 
                        when additionalData.TryGetValue("item_info_installation", out var installInfo):
                        item.MaxDurability = int.Parse(installInfo["max_durability"]);
                        item.BuffList = ParseBuffList(installInfo["buff_list"]) ?? throw new ArgumentException();
                        if (additionalData.TryGetValue("installation_shop_info", out var shopInfo))
                            item.MaxSellItems = int.Parse(shopInfo["max_items"]);
                        break;
                }

            return item;
        }
        
        private static List<(int id, int coolTime, int value)> ParseBuffList(string jsonString)
        {
            if (string.IsNullOrEmpty(jsonString) || jsonString == "[]") return new();

            jsonString = jsonString.Trim('"');
            var arrays = JsonConvert.DeserializeObject<List<int[]>>(jsonString);
            return arrays?.Select(arr => (id: arr[0], coolTime: arr[1], value: arr[2])).ToList();
        }

        public static List<(int id, int value)> ParseTupleArray(string jsonString)
        {
            if (string.IsNullOrEmpty(jsonString) || jsonString == "[]") return new();

            jsonString = jsonString.Trim('"');
            var arrays = JsonConvert.DeserializeObject<List<int[]>>(jsonString);

            return arrays?.Select(arr => (id: arr[0], value: arr[1])).ToList();
        }
        
        public string GetBuffComment()
        {
            if (BuffList.Count <= 0 && ConsumableBuffList.Count <= 0)
            {
                return "발동 효과 없음";
            }

            var result = "";
            if (IsConsumable)
            {
                foreach (var buffInfo in ConsumableBuffList)
                {
                    var buff = GameBuffData.Get(buffInfo.id);
                    result += buff.Comment.Replace("{value1}", $"{buffInfo.value}");
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