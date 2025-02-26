using network.common;
using network.common.data;
using network.common.data.models;
using network.helpers;
using network.interfaces;
using network.packets;
using RedLockNet.SERedis;
using user_server.progress;

namespace user_server.players;

public class PlayerCraft(GameUser user, PlayerInfo playerInfo, PlayerQuest playerQuest, PlayerInventory playerInventory, PlayerProgress playerProgress)
{
    private readonly CacheHelper _cacheHelper = user.CacheHelper;
    private readonly RedLockFactory _redLock = user.RedLock;
    private readonly SendPacketDelegate _sendToClient = user.Send;
    private readonly BroadcastDelegate<PlayerInfo> _broadcastPlayerInfo = user.BroadcastUpdateInfo;
    private readonly IncreaseQuestCountDelegate _increaseQuestCount = playerQuest.IncreaseQuestCount;
    private readonly SendUpdateItemsDelegate _sendUpdateItems = playerInventory.SendUpdateItems;
    private readonly AddProgressItemDelegate _addProgressItem = playerProgress.AddProgressItem;

    public async Task Craft(C_TO_U_CRAFT body)
    {
        await using (await PlayerInfo.Lock(_redLock, playerInfo.PlayerId))
        {
            var craftData = GameCraftData.Get(body.CraftId);
            if (playerInfo.Stamina < craftData.Stamina)
            {
                using var errorPacket = PacketMaker.U_TO_C_CRAFT(ErrorCode.FATAL);
                _sendToClient(errorPacket);
                return;
            }

            if (!playerInfo.CraftInfo.Manuals.Contains(craftData.ManualId))
            {
                using var errorPacket = PacketMaker.U_TO_C_CRAFT(ErrorCode.FATAL);
                _sendToClient(errorPacket);
                return;
            }
            
            var craftProgressInfo = new CraftProgressInfo(body.CraftId, DateTime.Now.AddSeconds(craftData.Seconds));
            _addProgressItem(craftProgressInfo, async trackable =>
            {
                var progressInfo = (CraftProgressInfo)trackable;
                await OnCraftComplete(progressInfo);
            });
            
            playerInfo.State = PlayerState.CRAFT_1;
            playerInfo.Stamina = Math.Clamp(playerInfo.Stamina - craftData.Stamina, 0, 100);
            await playerInfo.Save(_cacheHelper);
        }
        
        using var packet = PacketMaker.U_TO_C_CRAFT(ErrorCode.SUCCESS);
        _sendToClient(packet);
        _broadcastPlayerInfo(playerInfo);
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
            
            foreach (var (itemId, count) in craftData.RequireItems)
            {
                var deleteItem = playerInfo.InventoryInfo.DeleteItemById(itemId, count);
                if (deleteItem == null)
                {
                    throw new Exception("cannot find delete item");
                }
                updateItems.Add(deleteItem);
            }
            playerInfo.State = PlayerState.IDLE;
            await playerInfo.Save(_cacheHelper);
            
            // 퀘스트 갱신
            switch (craftId)
            {
                case 1:
                    await _increaseQuestCount(100000010, 1, updateQuests);
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