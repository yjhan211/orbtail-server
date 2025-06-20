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
        var updateQuests = new List<QuestInfo>();
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
            
            // 슬롯에 있는 재료들과 일치하는 아이템들을 제거
            foreach (var requiredMaterialId in craftData.RequireMaterial)
            {
                // 슬롯에서 해당 재료가 있는지 확인하고 제거
                var materialFound = false;
                for (int i = 0; i < playerInfo.CraftInfo.Slots.Count; i++)
                {
                    var slot = playerInfo.CraftInfo.Slots[i];
                    if (!slot.IsEmpty() && slot.ItemId == requiredMaterialId)
                    {
                        // 슬롯에서 재료 제거
                        playerInfo.CraftInfo.Slots[i] = new SlotItem();
                        materialFound = true;
                        break;
                    }
                }
                
                if (!materialFound)
                {
                    using var errorPacket = PacketMaker.U_TO_C_CRAFT(ErrorCode.FATAL);
                    _sendToClient(errorPacket);
                    return;
                }
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
            
            playerInfo.State = PlayerState.IDLE;
            await playerInfo.Save(_cacheHelper);
            
            // 퀘스트 갱신
            switch (craftId)
            {
                case 9:
                    await _increaseQuestCount(100000012, 1, updateQuests);
                    break;
                case 3:
                    // await _increaseQuestCount(100000013, 1, updateQuests);
                    break;
            }
        }
        
        using var packet = PacketMaker.U_TO_C_CRAFT_COMPLETE(true, playerInfo.CraftInfo.Slots);
        _sendToClient(packet);
        _broadcastPlayerInfo(playerInfo);
        _sendUpdateItems(updateItems);

        foreach (var quest in updateQuests)
        {
            using var questPacket = PacketMaker.U_TO_C_QUEST_UPDATE(quest);
            _sendToClient(questPacket);
        }
        
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
                
                if (!playerInfo.CraftInfo.Slots[body.SlotNum].IsEmpty())
                {
                    throw new Exception($"already in slot");
                }
                
                var deleteItem = playerInfo.InventoryInfo.DeleteItem(body.ItemUid, 1);
                if (deleteItem == null)
                {
                    throw new Exception($"delete item {body.ItemUid} failed.");
                }

                updateItems.Add(deleteItem);

                var generationQueue = new List<int>();
                switch (itemDetail.Id)
                {
                    case 301000012: // 책가방
                        generationQueue = [51, 51, 51, 51];
                        break;
                 
                    case 301000013: // 감자 한 박스
                        generationQueue = [11, 11, 11];
                        break;
                    
                    case 301000014: // 필통
                        generationQueue = [71, 71, 72, 72, 73, 144];
                        // generationQueue = [41, 61, 61, 61, 61];
                        break;

                    case 301000015: // 밤 한 박스
                        generationQueue = [31, 31, 31, 31];
                        break;
                    
                    case 301000016: // 소형 종이 상자
                        generationQueue = [41, 51, 61, 41, 51, 61];
                        break;
                    
                    case 301000017: // 휴지통 - 검정 비닐 봉지
                        generationQueue = [21, 21, 21, 21, 21, 21];
                        break;
                    
                    case 301000018: // 포대 자루
                        generationQueue = [111, 111, 111, 111, 111, 111, 111, 111];
                        break;
                    
                    case 301000019: // 고장난 알람 시계
                        generationQueue = [101, 101, 101, 84];
                        break;
                    
                    case 301000020: // 포장지
                        generationQueue = [91, 91, 91, 92];
                        break;
                    
                    case 301000021: // 공구함
                        generationQueue = [121, 121, 144, 121, 121, 121, 121, 121, 121];
                        break;
                    
                    case 301000022: // 노란 꽃
                        generationQueue = [161, 131, 131, 131, 131];
                        break;
                    
                    case 301000023: // 파란 꽃
                        generationQueue = [151, 131, 131, 131, 131];
                        break;
                    
                    case 301000024: // 하얀 비닐 봉투
                        generationQueue = [171, 171, 171, 171, 171, 171, 171, 171];
                        break;
                }

                generationQueue.Sort((a, b) => Random.Shared.Next(0, 2) == 0 ? a - b : b - a);;
                playerInfo.CraftInfo.Slots[body.SlotNum] = new SlotItem(itemDetail.Id, generationQueue);
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
                var sourceSlotItem = playerInfo.CraftInfo.Slots[body.sourceSlotNum];
                var targetSlotItem = playerInfo.CraftInfo.Slots[body.targetSlotNum];
                
                if (sourceSlotItem.IsEmpty())
                {
                    throw new Exception("source item empty");
                }
                else if (body.sourceSlotNum == body.targetSlotNum)
                {
                    var emptySlotIndex = playerInfo.CraftInfo.GetRandomEmptySlotIndex();
                    if (emptySlotIndex < 0)
                    {
                        throw new Exception("cannot found empty slot index");
                    }
                    playerInfo.CraftInfo.GenerateItemFromQueue(body.sourceSlotNum, emptySlotIndex);
                }
                else if (sourceSlotItem.ItemId == targetSlotItem.ItemId && !sourceSlotItem.IsGenerator())
                {
                    if (sourceSlotItem.ItemId % 10 >= 4)
                    {
                        throw new Exception("already max craft slot");
                    }

                    playerInfo.CraftInfo.Slots[body.sourceSlotNum] = new SlotItem();
                    playerInfo.CraftInfo.Slots[body.targetSlotNum].ItemId = targetSlotItem.ItemId + 1;
                }
                else if (!targetSlotItem.IsEmpty())
                {
                    playerInfo.CraftInfo.Slots[body.targetSlotNum] = sourceSlotItem;
                    playerInfo.CraftInfo.Slots[body.sourceSlotNum] = targetSlotItem;
                }
                else
                {
                    playerInfo.CraftInfo.Slots[body.targetSlotNum] = sourceSlotItem;
                    playerInfo.CraftInfo.Slots[body.sourceSlotNum] = new SlotItem();
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
        // if (trackable is not CraftProgressInfo craftProgress)
        // {
        //     return;
        // }
        //
        // var craftId = craftProgress.CraftId;
        //
        // var updateItems = new List<ItemInfo>();
        // var updateQuests = new List<QuestInfo>();
        // await using (await PlayerInfo.Lock(_redLock, playerInfo.PlayerId))
        // {
        //     var craftData = GameCraftData.Get(craftId);
        //     var craftTargetItem = await PlayerInventory.CreateItem(_cacheHelper, craftData.TargetItem, 1);
        //
        //     var addItem = playerInfo.InventoryInfo.AddItem(craftTargetItem);
        //     updateItems.Add(addItem);
        //     
        //     // foreach (var (itemId, count) in craftData.RequireItems)
        //     // {
        //     //     var deleteItem = playerInfo.InventoryInfo.DeleteItemById(itemId, count);
        //     //     if (deleteItem == null)
        //     //     {
        //     //         throw new Exception("cannot find delete item");
        //     //     }
        //     //     updateItems.Add(deleteItem);
        //     // }
        //     playerInfo.State = PlayerState.IDLE;
        //     await playerInfo.Save(_cacheHelper);
        //     
        //     // 퀘스트 갱신
        //     switch (craftId)
        //     {
        //         case 1:
        //             // await _increaseQuestCount(100000010, 1, updateQuests);
        //             break;
        //         case 3:
        //             await _increaseQuestCount(100000013, 1, updateQuests);
        //             break;
        //     }
        // }
        //
        // using var packet = PacketMaker.U_TO_C_CRAFT_COMPLETE(true);
        // _sendToClient(packet);
        // _broadcastPlayerInfo(playerInfo);
        // _sendUpdateItems(updateItems);
        //
        // foreach (var quest in updateQuests)
        // {
        //     using var questPacket = PacketMaker.U_TO_C_QUEST_UPDATE(quest);
        //     _sendToClient(questPacket);
        // }
    }
}