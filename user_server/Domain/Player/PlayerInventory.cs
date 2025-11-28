using user_server.infrastructure.network;
using network.common;
using network.common.data;
using network.common.data.models;
using network.interfaces;
using network.packets;

namespace user_server.domain.player;

public sealed class PlayerInventory(GameSession user, PlayerInfo playerInfo, PlayerQuest playerQuest)
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
            // 1. 모든 기존 착용 아이템 해제
            foreach (var item in playerInfo.InventoryInfo.ItemDict.Values.ToList().Where(item => item.IsWear))
            {
                item.IsWear = false;
                updateItems.Add(item);
            }
            playerInfo.WearItemIdList.Clear();

            // 2. 새로운 아이템들 착용 (null이나 빈 리스트면 모두 해제)
            if (body.ItemUidList.Count > 0)
            {
                var equipTypeSet = new HashSet<EquipType>();
                foreach (var itemUid in body.ItemUidList)
                {
                    if (!playerInfo.InventoryInfo.ItemDict.TryGetValue(itemUid, out var targetItem))
                    {
                        throw new Exception($"Item with uid {itemUid} not found");
                    }

                    var itemDetail = GameItemData.Get(targetItem.ItemId);
                    if (!itemDetail.IsEquipment)
                        throw new Exception($"not wearable item {targetItem.ItemId}");

                    var equipType = GameItemData.GetEquipType(targetItem.ItemId);

                    // 같은 EquipType 중복 체크
                    if (!equipTypeSet.Add(equipType))
                    {
                        throw new Exception($"Duplicate equip type {equipType} for item {targetItem.ItemId}");
                    }

                    targetItem.IsWear = true;
                    playerInfo.WearItemIdList.Add(targetItem.ItemId);

                    if (!updateItems.Contains(targetItem))
                    {
                        updateItems.Add(targetItem);
                    }
                }
            }

            await playerInfo.Save(_cacheHelper);

            // 퀘스트 처리 (착용한 아이템만)
            foreach (var itemInfo in updateItems.Where(x => x.IsWear))
            {
                switch (itemInfo.ItemId)
                {
                    case 107000001:
                        await playerQuest.IncreaseQuestCount(100000009, 1, updateQuests);
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
        if (updateQuests == null)
        {
            throw new ArgumentNullException(nameof(updateQuests));
        }
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

            foreach (var (buffId, _) in itemDetail.ConsumableBuffList)
            {
                var buffDetail = GameBuffData.Get(buffId);
                if (buffDetail.Type != BuffType.INSTANT)
                {
                    throw new NotImplementedException();
                }
            }
            await playerInfo.Save(_cacheHelper);
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

    private void SendUpdateItems(List<ItemInfo> updateItems)
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
