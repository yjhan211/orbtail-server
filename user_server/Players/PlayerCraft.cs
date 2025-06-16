using Microsoft.Extensions.Logging;
using network.common;
using network.common.data;
using network.common.data.models;
using network.interfaces;
using network.packets;
using user_server.progress;

namespace user_server.players;

public class PlayerCraft(GameUser user, PlayerInfo playerInfo, PlayerQuest playerQuest, PlayerInventory playerInventory, PlayerProgress playerProgress)
{
    private readonly ICacheHelper _cacheHelper = user.CacheHelper;
    private readonly IRedLockFactory _redLock = user.RedLock;
    private readonly SendPacketDelegate _sendToClient = user.Send;
    private readonly BroadcastDelegate<PlayerInfo> _broadcastPlayerInfo = user.BroadcastUpdateInfo;
    private readonly IncreaseQuestCountDelegate _increaseQuestCount = playerQuest.IncreaseQuestCount;
    private readonly SendUpdateItemsDelegate _sendUpdateItems = playerInventory.SendUpdateItems;
    private readonly AddProgressItemDelegate _addProgressItem = playerProgress.AddProgressItem;
    
    public async Task Craft(C_TO_U_CRAFT body)
    {
        var craftId = body.CraftId;
        var updateItems = new List<ItemInfo>();
        // var updateQuests = new List<QuestInfo>();
        var craftData = GameCraftData.Get(body.CraftId);
        await using (await PlayerInfo.Lock(_redLock, playerInfo.PlayerId))
        {
            // if (playerInfo.Stamina < craftData.Stamina)
            // {
            //     using var errorPacket = PacketMaker.U_TO_C_CRAFT(ErrorCode.FATAL);
            //     _sendToClient(errorPacket);
            //     return;
            // }

            if (!playerInfo.CraftInfo.Manuals.Contains(craftData.ManualId))
            {
                using var errorPacket = PacketMaker.U_TO_C_CRAFT(ErrorCode.FATAL);
                _sendToClient(errorPacket);
                return;
            }
            
            // var craftProgressInfo = new CraftProgressInfo(body.CraftId, DateTime.Now.AddSeconds(craftData.Seconds));
            // _addProgressItem(craftProgressInfo, async trackable =>
            // {
            //     var progressInfo = (CraftProgressInfo)trackable;
            //     await OnCraftComplete(progressInfo);
            // });
            //
            // playerInfo.State = PlayerState.CRAFT_1;
            // playerInfo.Stamina = Math.Clamp(playerInfo.Stamina - craftData.Stamina, 0, 100);
            // await playerInfo.Save(_cacheHelper);
            
            var craftTargetItem = await PlayerInventory.CreateItem(_cacheHelper, craftData.TargetItem, 1);
            var addItem = playerInfo.InventoryInfo.AddItem(craftTargetItem);
            updateItems.Add(addItem);
            
            // foreach (var (itemId, count) in craftData.RequireItems)
            // {
            //     var deleteItem = playerInfo.InventoryInfo.DeleteItemById(itemId, count);
            //     if (deleteItem == null)
            //     {
            //         throw new Exception("cannot find delete item");
            //     }
            //     updateItems.Add(deleteItem);
            // }
            playerInfo.State = PlayerState.IDLE;
            await playerInfo.Save(_cacheHelper);
            
            // 퀘스트 갱신
            switch (craftId)
            {
                case 1:
                    // await _increaseQuestCount(100000010, 1, updateQuests);
                    break;
                case 3:
                    // await _increaseQuestCount(100000013, 1, updateQuests);
                    break;
            }
        }
        
        using var packet = PacketMaker.U_TO_C_CRAFT_COMPLETE(true);
        _sendToClient(packet);
        _broadcastPlayerInfo(playerInfo);
        _sendUpdateItems(updateItems);

        // foreach (var quest in updateQuests)
        // {
        //     using var questPacket = PacketMaker.U_TO_C_QUEST_UPDATE(quest);
        //     _sendToClient(questPacket);
        // }
        
        var rewardItemInfo = GameItemData.Get(craftData.TargetItem);
        await user.ChatController.SendChat(playerInfo.PlayerId, playerInfo.Name, ChatType.ALL, $"아싸! {rewardItemInfo.Name} 만들었다!", user.NatsClient);
        user.BroadcastSocialAction(playerInfo, SocialActionType.LAUGH);
        
        // using var packet = PacketMaker.U_TO_C_CRAFT(ErrorCode.SUCCESS);
        // _sendToClient(packet);
        // _broadcastPlayerInfo(playerInfo);
    }
    
    public async Task PutMaterial(C_TO_U_PUT_MATERIAL body)
    {
        var errorCode = ErrorCode.SUCCESS;
        var updateItems = new List<ItemInfo>();
        try
        {
            await using (await PlayerInfo.Lock(_redLock, playerInfo.PlayerId))
            {
                if (!playerInfo.InventoryInfo.ItemDict.TryGetValue(body.ItemUid, out var useItem))
                {
                    throw new Exception($"Item with uid {body.ItemUid} not found");
                }

                var itemDetail = GameItemData.Get(useItem.ItemId);
                if (!itemDetail.IsMaterial)
                {
                    throw new Exception($"not material item {useItem.ItemId}");
                }
                
                if (playerInfo.CraftInfo.Slots[body.SlotNum] != 0)
                {
                    throw new Exception($"already in slot");
                }
                
                var deleteItem = playerInfo.InventoryInfo.DeleteItem(body.ItemUid, 1);
                if (deleteItem == null)
                {
                    throw new Exception($"delete item {body.ItemUid} failed.");
                }

                updateItems.Add(deleteItem);
                playerInfo.CraftInfo.Slots[body.SlotNum] = itemDetail.Id;
                await playerInfo.Save(_cacheHelper);
            }
        }
        catch (Exception ex)
        {
            errorCode = ErrorCode.FATAL;
            user.Logger.LogError(ex.Message);
        }
        finally
        {
            _sendUpdateItems(updateItems);
            using var packet = PacketMaker.U_TO_C_PUT_MATERIAL(errorCode, playerInfo.CraftInfo.Slots);
            user.Send(packet);
        }
    }

    public async Task HandleCraft(C_TO_U_HANDLE_CRAFT body)
    {
        var errorCode = ErrorCode.SUCCESS;
        try
        {
            await using (await PlayerInfo.Lock(_redLock, playerInfo.PlayerId))
            {
                var sourceSlotId = playerInfo.CraftInfo.Slots[body.sourceSlotNum];
                var targetSlotId = playerInfo.CraftInfo.Slots[body.targetSlotNum];

                if (sourceSlotId == targetSlotId)
                {
                    if (sourceSlotId % 10 >= 4)
                    {
                        throw new Exception("already max craft slot");
                    }
                    playerInfo.CraftInfo.Slots[body.sourceSlotNum] = sourceSlotId + 1;
                    playerInfo.CraftInfo.Slots[body.targetSlotNum] = 0;
                }
                else if (targetSlotId > 0)
                {
                    playerInfo.CraftInfo.Slots[body.targetSlotNum] = sourceSlotId;
                    playerInfo.CraftInfo.Slots[body.sourceSlotNum] = targetSlotId;
                }
                else
                {
                    playerInfo.CraftInfo.Slots[body.targetSlotNum] = sourceSlotId;
                    playerInfo.CraftInfo.Slots[body.sourceSlotNum] = 0;
                }

                await playerInfo.Save(_cacheHelper);
            }
        }
        catch (Exception ex)
        {
            errorCode = ErrorCode.FATAL;
            user.Logger.LogError(ex.Message);
        }
        finally
        {
            using var packet = PacketMaker.U_TO_C_HANDLE_CRAFT(errorCode, playerInfo.CraftInfo.Slots);
            user.Send(packet);
        }
    }
    
    private async Task OnCraftComplete(IProgressTrackable trackable)
    {
        if (trackable is not CraftProgressInfo craftProgress)
        {
            return;
        }

        var craftId = craftProgress.CraftId;
        
        var updateItems = new List<ItemInfo>();
        var updateQuests = new List<QuestInfo>();
        await using (await PlayerInfo.Lock(_redLock, playerInfo.PlayerId))
        {
            var craftData = GameCraftData.Get(craftId);
            var craftTargetItem = await PlayerInventory.CreateItem(_cacheHelper, craftData.TargetItem, 1);

            var addItem = playerInfo.InventoryInfo.AddItem(craftTargetItem);
            updateItems.Add(addItem);
            
            // foreach (var (itemId, count) in craftData.RequireItems)
            // {
            //     var deleteItem = playerInfo.InventoryInfo.DeleteItemById(itemId, count);
            //     if (deleteItem == null)
            //     {
            //         throw new Exception("cannot find delete item");
            //     }
            //     updateItems.Add(deleteItem);
            // }
            playerInfo.State = PlayerState.IDLE;
            await playerInfo.Save(_cacheHelper);
            
            // 퀘스트 갱신
            switch (craftId)
            {
                case 1:
                    // await _increaseQuestCount(100000010, 1, updateQuests);
                    break;
                case 3:
                    await _increaseQuestCount(100000013, 1, updateQuests);
                    break;
            }
        }
        
        using var packet = PacketMaker.U_TO_C_CRAFT_COMPLETE(true);
        _sendToClient(packet);
        _broadcastPlayerInfo(playerInfo);
        _sendUpdateItems(updateItems);

        foreach (var quest in updateQuests)
        {
            using var questPacket = PacketMaker.U_TO_C_QUEST_UPDATE(quest);
            _sendToClient(questPacket);
        }
    }
}