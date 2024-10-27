using System.Diagnostics.CodeAnalysis;
using System.Text.Json;
using network.common.helpers;
using network.managers;

namespace network.common.data;

public static class GameItemData
{
    private static readonly Dictionary<int, ItemInfoData> Items = new();

    public static void Initialize(Dictionary<string, CsvRow> baseItemData, Dictionary<string, CsvRow> equipmentData,
        Dictionary<string, CsvRow> consumableData, Dictionary<string, CsvRow> installationData,
        Dictionary<string, CsvRow> shopData)
    {
        foreach (var (id, baseInfo) in baseItemData)
        {
            var additionalData = new Dictionary<string, CsvRow>();
            var itemId = int.Parse(id); // string id를 int로 변환
            var itemType = (ItemType)(itemId / 100000000);

            switch (itemType)
            {
                case ItemType.EQUIPMENT when equipmentData.ContainsKey(id):
                    additionalData["item_info_equipment"] = equipmentData[id];
                    break;

                case ItemType.CONSUMABLE when consumableData.ContainsKey(id):
                    additionalData["item_info_consumable"] = consumableData[id];
                    break;

                case ItemType.INSTALLATION:
                    if (installationData.ContainsKey(id))
                        additionalData["item_info_installation"] = installationData[id];
                    if (shopData.ContainsKey(id))
                        additionalData["installation_shop_info"] = shopData[id];
                    break;
            }

            // 현재 아이템의 baseInfo만 전달
            var singleItemData = new Dictionary<string, CsvRow> { { id, baseInfo } };
            Items[itemId] = ItemInfoData.CreateFromData(singleItemData, additionalData);
        }
    }

    public static ItemInfoData Get(int id)
    {
        if (!Items.TryGetValue(id, out var item)) throw new KeyNotFoundException($"Item {id} not found");

        return item;
    }

    public static void Validate(LogManager logManager)
    {
        logManager.WriteDebugLog("=== GameItemData Validation ===");
        foreach (var (id, item) in Items)
        {
            logManager.WriteDebugLog($"Item {id}:");
            logManager.WriteDebugLog($"  Name: {item.Name}");
            logManager.WriteDebugLog($"  Reusable: {item.Reusable}");

            if (item.MaxDurability > 0) logManager.WriteDebugLog($"  MaxDurability: {item.MaxDurability}");

            if (item.BuffList != null && item.BuffList.Count != 0)
                logManager.WriteDebugLog($"  BuffList: {string.Join(", ", item.BuffList)}");

            if (item.ConsumableBuffList != null)
                logManager.WriteDebugLog($"  ConsumableBuffList: {string.Join(", ", item.ConsumableBuffList)}");

            if (item.MaxSellItems > 0) logManager.WriteDebugLog($"  MaxItems: {item.MaxSellItems}");

            logManager.WriteDebugLog("");
        }

        logManager.WriteDebugLog("All validations passed successfully!");
    }
}

[SuppressMessage("ReSharper", "UnusedAutoPropertyAccessor.Global")]
[SuppressMessage("ReSharper", "MemberCanBePrivate.Global")]
public class ItemInfoData
{
    public int Id { get; private set; }
    public ItemType Type { get; private init; }
    public LocalizedText? Name { get; private init; }
    public List<int>? Requirements { get; private set; }
    public bool Reusable { get; private init; }

    // 장비, 설치 아이템 관련
    public int? MaxDurability { get; private set; }
    public List<(int id, int value1, int value2)>? BuffList { get; private set; }

    // 소비 아이템 관련
    public List<(int id, int value)>? ConsumableBuffList { get; private set; }

    // 설치 아이템 관련
    public int? MaxSellItems { get; private set; }

    // 아이템 타입 체크 헬퍼 메서드
    public bool IsEquipment => Type == ItemType.EQUIPMENT;
    public bool IsConsumable => Type == ItemType.CONSUMABLE;
    public bool IsInstallation => Type == ItemType.INSTALLATION;
    public bool IsMaterial => Type == ItemType.MATERIAL;

    public static ItemInfoData CreateFromData(Dictionary<string, CsvRow> baseItemData,
        Dictionary<string, CsvRow>? additionalData = null)
    {
        var baseInfo = baseItemData.Values.First();
        var id = int.Parse(baseInfo["id"]);
        var itemType = (ItemType)(id / 100000000);

        var item = new ItemInfoData
        {
            Id = id,
            Type = itemType,
            Name = new LocalizedText(baseInfo["name"]),
            Requirements = JsonSerializer.Deserialize<List<int>>(baseInfo["requirements"]) ?? [],
            Reusable = int.Parse(baseInfo["reusable"]) == 1
        };

        if (additionalData != null)
            switch (itemType)
            {
                case ItemType.EQUIPMENT when additionalData.TryGetValue("item_info_equipment", out var equipInfo):
                    item.MaxDurability = int.Parse(equipInfo["max_durability"]);
                    item.BuffList = ParseBuffList(equipInfo["buff_list"]);
                    break;

                case ItemType.CONSUMABLE
                    when additionalData.TryGetValue("item_info_consumable", out var consumableInfo):
                    item.ConsumableBuffList = ParseConsumableBuffList(consumableInfo["buff_list"]);
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

    private static List<(int id, int coolTime, int value)>? ParseBuffList(string? jsonString)
    {
        if (string.IsNullOrEmpty(jsonString) || jsonString == "[]") return [];

        jsonString = jsonString.Trim('"');
        var arrays = JsonSerializer.Deserialize<List<int[]>>(jsonString);
        return arrays?.Select(arr => (id: arr[0], coolTime: arr[1], value: arr[2])).ToList();
    }

    private static List<(int id, int value)>? ParseConsumableBuffList(string? jsonString)
    {
        if (string.IsNullOrEmpty(jsonString) || jsonString == "[]") return [];

        jsonString = jsonString.Trim('"');
        var arrays = JsonSerializer.Deserialize<List<int[]>>(jsonString);

        return arrays?.Select(arr => (id: arr[0], value: arr[1])).ToList();
    }
}