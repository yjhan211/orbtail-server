using network.common;
using network.common.data;
using network.common.data.models;
using network.packets;

namespace user_server.controllers;

public static class CraftController
{
    public static async Task Craft(GameUser user, C_TO_U_CRAFT body)
    {
        await using (await PlayerInfo.Lock(user.RedLock, user.PlayerId))
        {
            if (user.PlayerManager.PlayerInfo == null)
            {
                using var errorPacket = PacketMaker.U_TO_C_CRAFT(ErrorCode.FATAL);
                user.Send(errorPacket);
                return;
            }

            if (user.PlayerManager.PlayerInfo.Stamina <= 0)
            {
                using var errorPacket = PacketMaker.U_TO_C_CRAFT(ErrorCode.FATAL);
                user.Send(errorPacket);
                return;
            }

            var craftData = GameCraftData.Get(body.CraftId);
            if (!user.PlayerManager.PlayerInfo.CraftInfo.Manuals.Contains(craftData.ManualId))
            {
                using var errorPacket = PacketMaker.U_TO_C_CRAFT(ErrorCode.FATAL);
                user.Send(errorPacket);
                return;
            }

            user.StartCraft(body.CraftId, DateTime.Now.AddSeconds(10));
            await user.SetState(PlayerState.CRAFT_1);
            user.PlayerManager.PlayerInfo.Stamina -= 5;
            await user.PlayerManager.PlayerInfo.Save();
        }
        
        using var packet = PacketMaker.U_TO_C_CRAFT(ErrorCode.SUCCESS);
        user.Send(packet);
        user.BroadcastUpdateInfo(user.PlayerManager.PlayerInfo);
    }

    public static async Task CraftEnd(GameUser user, int craftId)
    {
        var updateItems = new List<ItemInfo>();
        await using (await PlayerInfo.Lock(user.RedLock, user.PlayerId))
        {
            if (user.PlayerManager.PlayerInfo == null)
            {
                throw new Exception("cannot find player info");
            }

            var craftData = GameCraftData.Get(craftId);
            var craftTargetItem = await InventoryController.CreateItem(craftData.TargetItem, 1);

            user.PlayerManager.PlayerInfo.InventoryInfo.AddItem(craftTargetItem);
            updateItems.Add(craftTargetItem);
            
            foreach (var (itemId, count) in craftData.RequireItems)
            {
                var deleteItem = user.PlayerManager.PlayerInfo.InventoryInfo.DeleteItem(itemId, count);
                if (deleteItem == null)
                {
                    throw new Exception("cannot find delete item");
                }
                updateItems.Add(deleteItem);
            }
            await user.PlayerManager.SetState(PlayerState.IDLE);
            await user.PlayerManager.PlayerInfo.Save();
        }
        
        using var packet = PacketMaker.U_TO_C_CRAFT_COMPLETE(true);
        user.Send(packet);
        user.BroadcastUpdateInfo(user.PlayerManager.PlayerInfo);
        InventoryController.SendUpdateItems(user, updateItems);
    }
}