using network.common;
using network.common.data;
using network.common.data.models;
using network.interfaces;
using network.packets;

namespace user_server.players;

public class PlayerInventory(GameUser user, PlayerInfo playerInfo, PlayerQuest playerQuest)
{
    private const string ItemUidKey = "item_uid_key";
    
    private readonly ICacheHelper _cacheHelper = user.CacheHelper;

    public static async Task<ItemInfo> CreateItem(ICacheHelper cacheHelper, int itemId, int count)
    {
        // TODO RDB PK로 교체 예정
        var itemUid = await cacheHelper.StringIncrementAsync(ItemUidKey);
        return new ItemInfo(itemUid, itemId, count);
    }

    public async Task RequestWearItem(C_TO_U_WEAR_ITEM body)
    {
        var updateItems = new List<ItemInfo>();
        var updateQuests = new List<QuestInfo>();
        await using (await PlayerInfo.Lock(user.RedLock, playerInfo.PlayerId))
        {
            if (!playerInfo.InventoryInfo.ItemDict.TryGetValue(body.ItemUid, out var targetItem))
            {
                throw new Exception($"Item with uid {body.ItemUid} not found");
            }

            var itemDetail = GameItemData.Get(targetItem.ItemId);
            if (!itemDetail.IsEquipment)
                throw new Exception($"not wearable item {targetItem.ItemId}");
            
            if (targetItem.IsWear)
            {
                // 착용 해제
                playerInfo.WearItemIdList.Remove(targetItem.ItemId);
                targetItem.IsWear = false;
            }
            else
            {
                // 같은 종류의 아이템 인덱스 찾기
                var lastWearItem = playerInfo.InventoryInfo.ItemDict.Values.FirstOrDefault(
                    item => GameItemData.GetEquipType(targetItem.ItemId) == GameItemData.GetEquipType(item.ItemId) && item.IsWear
                );
        
                if (lastWearItem != null)
                {
                    // 같은 종류 아이템 착용 해제
                    lastWearItem.IsWear = false;
                    playerInfo.WearItemIdList.Remove(lastWearItem.ItemId);
                    updateItems.Add(lastWearItem);
                }
        
                // 새로운 아이템 착용
                targetItem.IsWear = true;
                playerInfo.WearItemIdList.Add(targetItem.ItemId);
            }

            updateItems.Add(targetItem);
            await playerInfo.Save(_cacheHelper);
            
            
            foreach (var itemInfo in updateItems)
            {
                switch (itemInfo.ItemId)
                {
                    case 107000001:
                        await playerQuest.IncreaseQuestCount(100000009, 1, updateQuests);
                        break;
            
                    default: 
                        break;
                }
            }
        }

        using var packet = PacketMaker.U_TO_C_WEAR_ITEM(playerInfo);
        user.Send(packet);

        SendUpdateItems(updateItems);
        user.BroadcastUpdateInfo(playerInfo);
        foreach (var updateQuest in updateQuests)
        {
            using var questPacket = PacketMaker.U_TO_C_QUEST_UPDATE(updateQuest);
            user.Send(questPacket);
        }
    }

    public async Task RequestUseItem(C_TO_U_USE_ITEM body)
    {
        var updateItems = new List<ItemInfo>();
        var updateQuests = new List<QuestInfo>();
        await using (await PlayerInfo.Lock(user.RedLock, playerInfo.PlayerId))
        {
            if (!playerInfo.InventoryInfo.ItemDict.TryGetValue(body.ItemUid, out var useItem))
            {
                throw new Exception($"Item with uid {body.ItemUid} not found");
            }

            const int count = 1; // TODO
            if (useItem.Count < count)
            {
                throw new Exception($"Item with uid {body.ItemUid} count not enough");
            }

            var itemDetail = GameItemData.Get(useItem.ItemId);
            if (!itemDetail.IsConsumable)
            {
                throw new Exception($"not consumable item {useItem.ItemId}");
            }

            var deleteItem = playerInfo.InventoryInfo.DeleteItem(body.ItemUid, count);
            if (deleteItem == null)
            {
                throw new Exception($"delete item {body.ItemUid} failed.");
            }
            updateItems.Add(deleteItem);
        
            foreach (var (buffId, value) in itemDetail.ConsumableBuffList)
            {
                var buffDetail = GameBuffData.Get(buffId);
                if (buffDetail.Type != BuffType.INSTANT)
                {
                    throw new NotImplementedException();
                }
                switch (buffDetail.SubType)
                {
                    case BuffSubType.CONDITION_ADD:
                        playerInfo.Hp = Math.Clamp(playerInfo.Hp + (value * 100), 0, 10000);
                        break;
                
                    case BuffSubType.CRAFT_ADD:
                        playerInfo.CraftInfo.Manuals.Add(value);
                        break;
                    
                    case BuffSubType.DURABILITY_ADD:
                        if (!playerInfo.InventoryInfo.ItemDict.TryGetValue(body.TargetItemUid, out var targetItem))
                        {
                            throw new Exception($"targetItem {body.TargetItemUid} not found");
                        }
                        if (GameItemData.GetEquipType(targetItem.ItemId) != EquipType.TOOL)
                        {
                            throw new Exception($"invalid targetItemType");
                        }
                        targetItem.Durability = Math.Clamp(targetItem.Durability + value, 0, 100);
                        updateItems.Add(targetItem);
                        break;
                    
                    case BuffSubType.COLOR_CHANGE:
                        if (!playerInfo.InventoryInfo.ItemDict.TryGetValue(body.TargetItemUid, out var targetItem2))
                        {
                            throw new Exception($"targetItem {body.TargetItemUid} not found");
                        }

                        switch (targetItem2.ItemId)
                        {
                            case 104000002:
                            case 104000003:
                            case 104000004:
                                switch (deleteItem.ItemId)
                                {
                                    case 201000007:
                                        targetItem2.ItemId = 104000002;
                                        break;
                                    
                                    case 201000008:
                                        targetItem2.ItemId = 104000003;
                                        break;
                                    
                                    default:
                                        break;
                                }
                                break;
                            
                            case 105000002:
                            case 105000003:
                            case 105000004:
                                switch (deleteItem.ItemId)
                                {
                                    case 201000007:
                                        targetItem2.ItemId = 105000002;
                                        break;
                                    
                                    case 201000008:
                                        targetItem2.ItemId = 105000003;
                                        break;
                                    
                                    default:
                                        break;
                                }
                                break;
                            
                            default:
                                break;
                        }
                        updateItems.Add(targetItem2);
                        break;
                }
            }
            await playerInfo.Save(_cacheHelper);
            
            switch (deleteItem.ItemId)
            {
                case 201000001:
                case 201000002:
                case 201000003:
                case 201000006:    
                    var rewardItem = await CreateItem(_cacheHelper, 301000020, 1); // 빈 포장지
                    var addItem = playerInfo.InventoryInfo.AddItem(rewardItem);
                    updateItems.Add(addItem);
                    break;
                
                case 201000005:
                    await playerQuest.IncreaseQuestCount(100000004, 1, updateQuests);
                    break;
                    
                // case 201000004:
                //     await playerQuest.IncreaseQuestCount(100000009, 1, updateQuests);
                //     break;
                    
                case 202000001:
                    await playerQuest.IncreaseQuestCount(200000001, 1, updateQuests);
                    break;
                
                case 202000002:
                    await playerQuest.IncreaseQuestCount(200000002, 1, updateQuests);
                    break;
            }
        }

        using var packet = PacketMaker.U_TO_C_USE_ITEM(playerInfo);
        user.Send(packet);
        SendUpdateItems(updateItems);
        foreach (var updateQuest in updateQuests)
        {
            using var questPacket = PacketMaker.U_TO_C_QUEST_UPDATE(updateQuest);
            user.Send(questPacket);
        }
    }
    
    public void SendCurrentItems()
    {
        var inventoryInfo = playerInfo.InventoryInfo;
        if (inventoryInfo.ItemDict.Count == 0)
        {
            using var packet = PacketMaker.U_TO_C_INVENTORY_ITEM_LIST(new Dictionary<long, ItemInfo>(), true);
            user.Send(packet);
        }

        var itemKeys = inventoryInfo.ItemDict.Keys.ToArray();
        for (var i = 0; i < itemKeys.Length; i += Config.BROADCAST_UNIT)
        {
            var batchDict = itemKeys.Skip(i).Take(Config.BROADCAST_UNIT)
                .ToDictionary(key => key, key => inventoryInfo.ItemDict[key]);

            var isEnded = i + Config.BROADCAST_UNIT >= itemKeys.Length;

            using var packet = PacketMaker.U_TO_C_INVENTORY_ITEM_LIST(batchDict, isEnded);
            user.Send(packet);
        }
    }
    
    public virtual void SendUpdateItems(List<ItemInfo> updateItems)
    {
        if (updateItems.Count == 0)
        {
            return;
        }

        for (var i = 0; i < updateItems.Count; i += Config.BROADCAST_UNIT)
        {
            var batchItems = updateItems.Skip(i).Take(Config.BROADCAST_UNIT).ToList();
            var isEnded = i + Config.BROADCAST_UNIT >= updateItems.Count;
            using var packet = PacketMaker.U_TO_C_INVENTORY_UPDATE(batchItems, isEnded);
            user.Send(packet);
        }
    }
}