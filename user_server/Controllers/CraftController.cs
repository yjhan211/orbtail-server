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

            var craftData = GameCraftData.Get(body.CraftId);
            // TODO 주석해제
            // if (user.PlayerManager.PlayerInfo.Stamina < craftData.Stamina)
            // {
            //     using var errorPacket = PacketMaker.U_TO_C_CRAFT(ErrorCode.FATAL);
            //     user.Send(errorPacket);
            //     return;
            // }

            if (!user.PlayerManager.PlayerInfo.CraftInfo.Manuals.Contains(craftData.ManualId))
            {
                using var errorPacket = PacketMaker.U_TO_C_CRAFT(ErrorCode.FATAL);
                user.Send(errorPacket);
                return;
            }

            user.StartCraft(body.CraftId, DateTime.Now.AddSeconds(craftData.Seconds));
            await user.SetState(PlayerState.CRAFT_1);
            
            user.PlayerManager.PlayerInfo.Stamina = Math.Clamp(user.PlayerManager.PlayerInfo.Stamina - craftData.Stamina, 0, 100);
            await user.PlayerManager.PlayerInfo.Save();
        }
        
        using var packet = PacketMaker.U_TO_C_CRAFT(ErrorCode.SUCCESS);
        user.Send(packet);
        user.BroadcastUpdateInfo(user.PlayerManager.PlayerInfo);
    }

    public static async Task CraftEnd(GameUser user, int craftId)
    {
        var updateItems = new List<ItemInfo>();
        var updateQuests = new List<QuestInfo>();
        await using (await PlayerInfo.Lock(user.RedLock, user.PlayerId))
        {
            if (user.PlayerManager.PlayerInfo == null)
            {
                throw new Exception("cannot find player info");
            }

            var craftData = GameCraftData.Get(craftId);
            var craftTargetItem = await InventoryController.CreateItem(craftData.TargetItem, 1);

            var addItem = user.PlayerManager.PlayerInfo.InventoryInfo.AddItem(craftTargetItem);
            updateItems.Add(addItem);
            
            foreach (var (itemId, count) in craftData.RequireItems)
            {
                var deleteItem = user.PlayerManager.PlayerInfo.InventoryInfo.DeleteItemById(itemId, count);
                if (deleteItem == null)
                {
                    throw new Exception("cannot find delete item");
                }
                updateItems.Add(deleteItem);
            }
            await user.PlayerManager.SetState(PlayerState.IDLE);
            await user.PlayerManager.PlayerInfo.Save();
            
            // 퀘스트 갱신
            switch (craftId)
            {
                case 1:
                    await QuestController.IncreaseQuestCount(user, 100000010, 1, updateQuests);
                    break;
                
                default:
                    break;
            }
        }
        
        using var packet = PacketMaker.U_TO_C_CRAFT_COMPLETE(true);
        user.Send(packet);
        user.BroadcastUpdateInfo(user.PlayerManager.PlayerInfo);
        InventoryController.SendUpdateItems(user, updateItems);

        foreach (var quest in updateQuests)
        {
            using var questPacket = PacketMaker.U_TO_C_QUEST_UPDATE(quest);
            user.Send(questPacket);
        }
    }
}