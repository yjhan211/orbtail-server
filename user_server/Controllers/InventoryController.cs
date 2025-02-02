using network.common;
using network.common.data.models;
using network.helpers;
using network.packets;

namespace user_server.controllers;

public static class InventoryController
{
    private const string ItemUidKey = "item_uid_key";

    public static async Task<ItemInfo> CreateItem(int itemId, int count)
    {
        // TODO RDB PK로 교체 예정
        var itemUid = await CacheHelper.Instance.StringIncrementAsync(ItemUidKey);
        return new ItemInfo(itemUid, itemId, count);
    }

    public static async Task RequestWearItem(GameUser user, C_TO_U_WEAR_ITEM body)
    {
        if (GameUser.ActionState.Contains(user.PlayerState))
        {
            throw new Exception("Invalid Player State");
        }

        if (user.PlayerManager.PlayerInfo == null)
        {
            throw new Exception("Invalid PlayerInfo");
        }

        List<ItemInfo> updateItems;
        await using (await PlayerInfo.Lock(user.RedLock, user.PlayerId))
        {
            updateItems = await user.PlayerManager.Wear(body.ItemUid);
        }

        using var packet = PacketMaker.U_TO_C_WEAR_ITEM(user.PlayerManager.PlayerInfo);
        user.Send(packet);

        SendUpdateItems(user, updateItems);
        user.BroadcastUpdateInfo(user.PlayerManager.PlayerInfo);
    }

    public static async Task RequestUseItem(GameUser user, C_TO_U_USE_ITEM body)
    {
        if (user.PlayerManager.PlayerInfo == null)
        {
            throw new Exception("Invalid PlayerInfo");
        }
        
        List<ItemInfo> updateItems;
        await using (await PlayerInfo.Lock(user.RedLock, user.PlayerId))
        {
            updateItems = await user.PlayerManager.UseItem(user, body.ItemUid);
        }

        using var packet = PacketMaker.U_TO_C_USE_ITEM(user.PlayerManager.PlayerInfo);
        user.Send(packet);
        SendUpdateItems(user, updateItems);
    }


    public static async Task AddLabItem(GameUser user, C_TO_U_LAB_INVENTORY_ADD_ITEM body)
    {
        LabInfo? labInfo;
        await using (await PlayerInfo.Lock(user.RedLock, user.PlayerId))
        {
            var playerInfo = await PlayerInfo.Load(user.PlayerId);
            if (playerInfo == null) throw new Exception("player_info not exists");

            await using (await LabInfo.Lock(user.RedLock, playerInfo.LabId))
            {
                labInfo = await LabInfo.Load(playerInfo.LabId);
                if (labInfo == null) throw new Exception("lab_info not exists");

                if (!playerInfo.InventoryInfo.ItemDict.TryGetValue(body.ItemUid, out var targetItem))
                    throw new Exception($"Item with uid {body.ItemUid} not found");

                if (targetItem.Count <= 0) throw new Exception($"Item {body.ItemUid} count invalid");

                if (targetItem.Count <= 1)
                    playerInfo.InventoryInfo.ItemDict.Remove(body.ItemUid);
                else
                    // TODO 일괄사용
                    targetItem.Count -= 1;

                // var isCountable = !GameDataHelper.IsWearableItem(targetItem.ItemId);
                // var isExist = labInfo.InventoryInfo.ItemDict.ContainsKey(targetItem.ItemUid);
                // if (isCountable && isExist)
                // {
                //     labInfo.InventoryInfo.ItemDict[targetItem.ItemUid].Count += 1;
                // }
                // else
                // {
                //     var addItem = await CreateItem(targetItem.ItemId, 1);
                //     labInfo.InventoryInfo.ItemDict[addItem.ItemUid] = addItem;
                // }

                await labInfo.Save();
                await playerInfo.Save();
            }
        }

        SendCurrentItems(user);

        using var packet = PacketMaker.U_TO_U_LAB_INVENTORY(labInfo.InventoryInfo.ItemDict);
        foreach (var labMember in labInfo.MemberDict)
            user.NatsClient.Publish(GameObjectInfo.MakeObjectKey(ObjectType.PLAYER, labMember.Key), packet.ToBytes());
    }

    public static async Task TakeLabItem(GameUser user, C_TO_U_LAB_INVENTORY_TAKE_ITEM body)
    {
        LabInfo? labInfo;
        await using (await PlayerInfo.Lock(user.RedLock, user.PlayerId))
        {
            var playerInfo = await PlayerInfo.Load(user.PlayerId);
            if (playerInfo == null) throw new Exception("player_info not exists");

            await using (await LabInfo.Lock(user.RedLock, playerInfo.LabId))
            {
                labInfo = await LabInfo.Load(playerInfo.LabId);
                if (labInfo == null) throw new Exception("lab_info not exists");

                if (!labInfo.InventoryInfo.ItemDict.TryGetValue(body.ItemUid, out var targetItem))
                    throw new Exception("not found item info");

                if (targetItem.Count <= 0) throw new Exception($"Item {body.ItemUid} count invalid");

                if (targetItem.Count <= 1)
                    labInfo.InventoryInfo.ItemDict.Remove(body.ItemUid);
                else
                    // TODO 일괄사용
                    labInfo.InventoryInfo.ItemDict[body.ItemUid].Count -= 1;

                // var isCountable = !GameDataHelper.IsWearableItem(targetItem.ItemId);
                // var isExist = labInfo.InventoryInfo.ItemDict.ContainsKey(targetItem.ItemUid);
                // if (isCountable && isExist)
                // {
                //     playerInfo.InventoryInfo.ItemDict[targetItem.ItemUid].Count += targetItem.Count;
                // }
                // else
                // {
                //     var addItem = await CreateItem(targetItem.ItemId, 1);
                //     playerInfo.InventoryInfo.ItemDict[addItem.ItemUid] = addItem;
                // }

                await labInfo.InventoryInfo.Save();
                await playerInfo.Save();
            }
        }

        SendCurrentItems(user);
        using var packet = PacketMaker.U_TO_U_LAB_INVENTORY(labInfo.InventoryInfo.ItemDict);
        foreach (var labMember in labInfo.MemberDict)
            user.NatsClient.Publish(GameObjectInfo.MakeObjectKey(ObjectType.PLAYER, labMember.Key), packet.ToBytes());
    }

    public static async Task GetLabInventory(GameUser user)
    {
        var playerInfo = await PlayerInfo.Load(user.PlayerId);
        if (playerInfo == null) throw new Exception("player_info not exists");

        var labInfo = await LabInfo.Load(playerInfo.LabId);
        if (labInfo == null) throw new Exception("lab_info not exists");

        var itemDict = labInfo.InventoryInfo.ItemDict;
        LabController.SendLabItemList(user, itemDict);
    }

    public static void SendCurrentItems(GameUser user)
    {
        if (user.PlayerManager.PlayerInfo == null)
        {
            throw new Exception("player_info not exists");
        }

        var inventoryInfo = user.PlayerManager.PlayerInfo.InventoryInfo;
        if (inventoryInfo == null) throw new Exception("inventory_info not exists");

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
    
    public static void SendUpdateItems(GameUser user, List<ItemInfo> updateItems)
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

    public static void BroadcastCurrentLabItemList(GameUser user, LabInfo labInfo)
    {
        var inventoryInfo = labInfo.InventoryInfo;
        if (inventoryInfo == null) throw new Exception("inventory_info not exists");

        var index = 0;
        var itemKeys = inventoryInfo.ItemDict.Keys.ToArray();
        while (index < itemKeys.Length)
        {
            var batchDict = new Dictionary<long, ItemInfo>();
            for (var i = index; i < index + Config.BROADCAST_UNIT && i < itemKeys.Length; i++)
            {
                var key = itemKeys[i];
                batchDict[key] = inventoryInfo.ItemDict[key];
            }

            var isEnded = index + Config.BROADCAST_UNIT >= itemKeys.Length;
            var packet = PacketMaker.U_TO_C_LAB_INVENTORY(batchDict, isEnded);
            user.Send(packet);

            index += Config.BROADCAST_UNIT;
        }
    }
}