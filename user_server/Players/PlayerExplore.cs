using network.common;
using network.common.data;
using network.common.data.models;
using network.helpers;
using network.packets;
using RedLockNet.SERedis;
using user_server.progress;

namespace user_server.players;

public class PlayerExplore(
    GameUser user,
    PlayerInfo playerInfo,
    PlayerQuest playerQuest,
    PlayerInventory playerInventory,
    PlayerProgress playerProgress)
{
    private readonly CacheHelper _cacheHelper = user.CacheHelper;
    private readonly RedLockFactory _redLock = user.RedLock;
    private readonly SendPacketDelegate _sendToClient = user.Send;
    private readonly BroadcastDelegate<PlayerInfo> _broadcastPlayerInfo = user.BroadcastUpdateInfo;
    private readonly BroadcastDelegate<ExploreTargetInfo> _broadcastExploreTargetInfo = user.BroadcastUpdateInfo;
    private readonly BroadcastDestroyDelegate _broadcastDestroy = user.BroadcastObjectDestroy;
    private readonly IncreaseQuestCountDelegate _increaseQuestCount = playerQuest.IncreaseQuestCount;
    private readonly StartQuestDelegate _startQuest = playerQuest.StartQuest;
    private readonly SendUpdateItemsDelegate _sendUpdateItems = playerInventory.SendUpdateItems;
    private readonly AddProgressItemDelegate _addProgressItem = playerProgress.AddProgressItem;

    public async Task Explore(C_TO_U_EXPLORE body)
    {
        ExploreTargetInfo? exploreTargetInfo;
        await using (await PlayerInfo.Lock(_redLock, playerInfo.PlayerId))
        {
            if (playerInfo.Stamina <= 0)
            {
                using var errorPacket = PacketMaker.U_TO_C_EXPLORE(ErrorCode.FATAL);
                _sendToClient(errorPacket);
                return;
            }

            await using (await ExploreTargetInfo.Lock(_redLock, body.ExploreTargetUid))
            {
                exploreTargetInfo = await ExploreTargetInfo.Load(_cacheHelper, body.ExploreTargetUid);
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
            await playerInfo.Save(_cacheHelper);
        }

        using var packet = PacketMaker.U_TO_C_EXPLORE(ErrorCode.SUCCESS);
        _sendToClient(packet);
        _broadcastPlayerInfo(playerInfo);
        _broadcastExploreTargetInfo(exploreTargetInfo);
    }

    private async Task OnExploreComplete(ExploreProgressInfo exploreProgressInfo)
    {
        var updateItems = new List<ItemInfo>();
        var updateQuests = new List<QuestInfo>();
        var exploreTargetInfo = exploreProgressInfo.ExploreTargetInfo;
        await using (await PlayerInfo.Lock(_redLock, playerInfo.PlayerId))
        {
            // TODO 능력치 기반한 확률
            var exploreTargetData = GameExploreTargetData.Get(exploreTargetInfo.ExploreTargetId);
            if (exploreTargetData.Reusable)
            {
                // 조사대상 수집 불가능하도록 TODO 재충전
                exploreTargetInfo.PlayerId = -1;
                await exploreTargetInfo.Save(_cacheHelper);
                _broadcastExploreTargetInfo(exploreTargetInfo);
            }
            else
            {
                // 조사대상 삭제
                await exploreTargetInfo.Delete(_cacheHelper);
                _broadcastDestroy(exploreTargetInfo.ObjectInfo);
            }

            // 수집 결과 지급
            var random = new Random();
            var rewardItemId = exploreTargetData.RewardItemPool[random.Next(0, exploreTargetData.RewardItemPool.Count)];
            var rewardItem = await PlayerInventory.CreateItem(_cacheHelper, rewardItemId, 1);
            
            var updateItem = playerInfo.InventoryInfo.AddItem(rewardItem);
            updateItems.Add(updateItem);

            playerInfo.State = PlayerState.IDLE;
            await playerInfo.Save(_cacheHelper);
            
            // 퀘스트 갱신
            switch (exploreTargetInfo.ExploreTargetId)
            {
                case 1:
                case 2:
                case 3:
                    await _increaseQuestCount(100000004, 1, updateQuests);
                    break;
                case 4:
                case 5: 
                case 6:
                    await _increaseQuestCount(100000008, 1, updateQuests);
                    break;
                case 7:
                    await _increaseQuestCount(100000008, 1, updateQuests);
                    await _startQuest(100000009, updateQuests);
                    break;
                case 8:
                case 9: 
                case 10: 
                case 11:
                    await _increaseQuestCount(100000012, 1, updateQuests);
                    break;
            }
        }
        using var packet = PacketMaker.U_TO_C_EXPLORE_COMPLETE(true);
        _sendToClient(packet);
        _broadcastPlayerInfo(playerInfo);
        _sendUpdateItems(updateItems);

        foreach (var updateQuest in updateQuests)
        {
            using var questPacket = PacketMaker.U_TO_C_QUEST_UPDATE(updateQuest);
            _sendToClient(questPacket);
        }
    }
}