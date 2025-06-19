using MessagePack;
using network.common;
using network.common.data;
using network.common.data.models;
using Microsoft.Extensions.Logging;
using network.helpers;
using network.interfaces;
using network.managers;
using network.packets;
using user_server.controllers;
using user_server.progress;

namespace user_server.players;

public class PlayerExplore(
    GameUser user,
    PlayerInfo playerInfo,
    PlayerQuest playerQuest,
    PlayerInventory playerInventory,
    PlayerProgress playerProgress)
{
    private readonly INatsClient _natsClient = user.NatsClient;
    private readonly ICacheHelper _cacheHelper = user.CacheHelper;
    
    private readonly IRedLockFactory _redLock = user.RedLock;
    private readonly SendPacketDelegate _sendToClient = user.Send;
    private readonly BroadcastDelegate<PlayerInfo> _broadcastPlayerInfo = user.BroadcastUpdateInfo;
    private readonly BroadcastDelegate<ExploreTargetInfo> _broadcastExploreTargetInfo = user.BroadcastUpdateInfo;
    private readonly BroadcastDestroyDelegate _broadcastDestroy = user.BroadcastObjectDestroy;
    private readonly IncreaseQuestCountDelegate _increaseQuestCount = playerQuest.IncreaseQuestCount;
    private readonly StartQuestDelegate _startQuest = playerQuest.StartQuest;
    private readonly SendUpdateItemsDelegate _sendUpdateItems = playerInventory.SendUpdateItems;
    private readonly AddProgressItemDelegate _addProgressItem = playerProgress.AddProgressItem;
    private readonly ILogger _logger = user.Logger;

    private readonly MapObjectController _mapObjectController = user.MapObjectController;
    
    protected virtual async Task<ExploreTargetInfo?> LoadExploreTargetInfo(long exploreTargetUid)
    {
        return await ExploreTargetInfo.Load(_cacheHelper, exploreTargetUid);
    }
    
    public async Task Explore(C_TO_U_EXPLORE body)
    {
        ExploreTargetInfo? exploreTargetInfo;
        await using (await PlayerInfo.Lock(_redLock, playerInfo.PlayerId))
        {
            // if (playerInfo.Stamina <= 0)
            // {
            //     using var errorPacket = PacketMaker.U_TO_C_EXPLORE(ErrorCode.FATAL);
            //     _sendToClient(errorPacket);
            //     return;
            // }

            await using (await ExploreTargetInfo.Lock(_redLock, body.ExploreTargetUid))
            {
                exploreTargetInfo = await LoadExploreTargetInfo(body.ExploreTargetUid);
                if (exploreTargetInfo == null)
                {
                    using var errorPacket = PacketMaker.U_TO_C_EXPLORE(ErrorCode.FATAL);
                    _sendToClient(errorPacket);
                    return;
                }
                if (exploreTargetInfo.PlayerId != 0)
                {
                    using var errorPacket = PacketMaker.U_TO_C_EXPLORE(ErrorCode.ALREADY_ANOTHER_USE_SKILL);
                    _sendToClient(errorPacket);
                    return;
                }
                
                // 거리 체크 로직 (주석 처리됨)
                // if (2 < _playerInfo.ObjectInfo.CurrentCell.GetDistance(exploreTargetInfo.ObjectInfo.CurrentCell))
                // {
                //     using var errorPacket = PacketMaker.U_TO_C_EXPLORE(ErrorCode.FATAL);
                //     _sendToClient(errorPacket);
                //     return;
                // }

                exploreTargetInfo.PlayerId = playerInfo.PlayerId;
                exploreTargetInfo.EndTimestamp = DateTime.UtcNow.AddSeconds(GameRuleData.SkillCompleteTime);
                
                var exploreProgressInfo = new ExploreProgressInfo(exploreTargetInfo);
                _addProgressItem(exploreProgressInfo, async trackable =>
                {
                    var progressInfo = (ExploreProgressInfo)trackable;
                    await OnExploreComplete(progressInfo);
                });
                await exploreTargetInfo.Save(_cacheHelper);
            }

            playerInfo.State = PlayerState.EXPLORE_1;
            playerInfo.Stamina -= 5;
            
            var direction = playerInfo.ObjectInfo.TargetCell.GetDirection(exploreTargetInfo.ObjectInfo.CurrentCell);
            playerInfo.ObjectInfo.SetFlip(direction);
            await playerInfo.Save(_cacheHelper);
        }

        using var packet = PacketMaker.U_TO_C_EXPLORE(ErrorCode.SUCCESS);
        _sendToClient(packet);
        
        var isCommonMap = GameMapData.IsCommonMap(playerInfo.ObjectInfo.MapId);
        var currentPartKey = isCommonMap
            ? MapHelper.CreatePartKey(playerInfo.ObjectInfo.MapId, playerInfo.ObjectInfo.CurrentCell)
            : MapHelper.CreatePartKey(playerInfo.ObjectInfo.MapId, playerInfo.ObjectInfo.MapSubId);
        
        var currentManageServer = isCommonMap
            ? MapHelper.GetManageServerId(currentPartKey)
            : MapHelper.GetManageServerId(playerInfo.ObjectInfo.MapSubId);
        
        var moveSubject = SubjectHelper.GetUpdateManageSubject(playerInfo.ObjectInfo, currentManageServer);
        _natsClient.Publish(moveSubject,
            MessagePackSerializer.Serialize((currentPartKey, objectInfo: playerInfo.ObjectInfo)));
        
        _mapObjectController.EnqueueUpdateObject(playerInfo.ObjectInfo);
        
        _broadcastPlayerInfo(playerInfo);
        _broadcastExploreTargetInfo(exploreTargetInfo);
    }

    private async Task OnExploreComplete(ExploreProgressInfo exploreProgressInfo)
    {
        var rewardItemId = 0;
        var updateItems = new List<ItemInfo>();
        var updateQuests = new List<QuestInfo>();
        var exploreTargetInfo = exploreProgressInfo.ExploreTargetInfo;
        await using (await PlayerInfo.Lock(_redLock, playerInfo.PlayerId))
        {
            // TODO 능력치 기반한 확률
            var exploreTargetData = GameExploreTargetData.Get(exploreTargetInfo.ExploreTargetId);

            // TODO 재수집 가능하게 주석처리 (테스트용)
            // if (exploreTargetData.Reusable)
            // {
            //     // 조사대상 수집 불가능하도록 TODO 재충전
            //     exploreTargetInfo.PlayerId = -1;
            //     await exploreTargetInfo.Save(_cacheHelper);
            //     _broadcastExploreTargetInfo(exploreTargetInfo);
            // }
            // else
            // {
            //     // 조사대상 삭제
            //     await exploreTargetInfo.Delete(_cacheHelper);
            //     _broadcastDestroy(exploreTargetInfo.ObjectInfo);
            // }
            
            exploreTargetInfo.PlayerId = 0;
            await exploreTargetInfo.Save(_cacheHelper);
            _broadcastExploreTargetInfo(exploreTargetInfo);

            // 수집 결과 지급
            var random = new Random();
            rewardItemId = exploreTargetData.RewardItemPool[random.Next(0, exploreTargetData.RewardItemPool.Count)];

            var isQuestIncrease = playerInfo.InventoryInfo.ItemDict.FirstOrDefault(x => x.Value.ItemId == rewardItemId).Value == null;
            if (isQuestIncrease)
            {
                var questMap = new Dictionary<int, (int QuestId, List<int>)>
                {
                    { 12, (100000003, []) },
                    { 18, (100000003, [])},
                    { 1, (100000006, [])},
                    { 2, (100000006, [])},
                    { 3, (100000006, [])},
                    { 15, (100000006, [])},
                    { 4, (100000008, [])},
                    { 5, (100000008, [])},
                    { 6, (100000008, [])},
                    { 7, (100000008, [])},
                    { 8, (100000011, [])},
                    { 9, (100000011, [])},
                    { 10, (100000011, [])},
                    { 11, (100000011, [])},
                };

                if (questMap.TryGetValue(exploreTargetInfo.ExploreTargetId, out var questInfo))
                {
                    await _increaseQuestCount(questInfo.QuestId, 1, updateQuests);
                    foreach (var followQuest in questInfo.Item2)
                    {
                        await _startQuest(followQuest, updateQuests);
                    }
                }

                if (exploreTargetInfo.ExploreTargetId == 13)
                {
                    await _startQuest(200000001, updateQuests);
                }
            }
            
            var rewardItem = await PlayerInventory.CreateItem(_cacheHelper, rewardItemId, 1);
            switch (rewardItem.ItemId)
            {
                case 107000001:
                    rewardItem.Durability = 0;
                    break;
            }
            var updateItem = playerInfo.InventoryInfo.AddItem(rewardItem);
            updateItems.Add(updateItem);

            playerInfo.State = PlayerState.IDLE;
            await playerInfo.Save(_cacheHelper);
        }
        using var packet = PacketMaker.U_TO_C_EXPLORE_COMPLETE(true, rewardItemId);
        _sendToClient(packet);
        _broadcastPlayerInfo(playerInfo);
        _sendUpdateItems(updateItems);

        foreach (var updateQuest in updateQuests)
        {
            using var questPacket = PacketMaker.U_TO_C_QUEST_UPDATE(updateQuest);
            _sendToClient(questPacket);
        }

        var rewardItemInfo = GameItemData.Get(rewardItemId);
        await user.ChatController.SendChat(playerInfo.PlayerId, playerInfo.Name, ChatType.ALL, $"우와~ {rewardItemInfo.Name} 수집했다!", user.NatsClient);
        user.BroadcastSocialAction(playerInfo, SocialActionType.LAUGH);

        //
        // if (user.PlayerController != null)
        // {
        //     await user.PlayerController.SocialAction(socialMessage);
        // }
    }
}