using network.common;
using network.common.data;
using network.common.data.models;
using network.packets;

namespace user_server.controllers;

public static class ExploreController
{
    public static async Task GetExploreTargetInfo(GameUser user, C_TO_U_EXPLORE_TARGET_INFO body)
    {
        var targetInfoList = new List<ExploreTargetInfo>();
        for (var i = 0; i < body.ExploreTargetIdList.Count; i++)
        {
            var targetExploreUid = body.ExploreTargetIdList[i];
            ExploreTargetInfo? targetExploreInfo;

            await using (await ExploreTargetInfo.Lock(user.RedLock, targetExploreUid))
            {
                targetExploreInfo = await ExploreTargetInfo.Load(targetExploreUid);
            }

            if (targetExploreInfo == null) continue;

            targetInfoList.Add(targetExploreInfo);

            var isMax = targetInfoList.Count >= Config.BROADCAST_UNIT;
            var isLast = i == body.ExploreTargetIdList.Count - 1;

            if (!isMax && !isLast)
            {
                continue;
            }

            using var packet = PacketMaker.U_TO_C_EXPLORE_TARGET_INFO(targetInfoList);
            user.Send(packet);
            targetInfoList.Clear();
        }
    }
    
    public static async Task Explore(GameUser user, C_TO_U_EXPLORE body)
    {
        ExploreTargetInfo? exploreTargetInfo;
        await using (await PlayerInfo.Lock(user.RedLock, user.PlayerId))
        {
            if (user.PlayerManager.PlayerInfo == null)
            {
                using var errorPacket = PacketMaker.U_TO_C_EXPLORE(ErrorCode.FATAL);
                user.Send(errorPacket);
                return;
            }

            if (user.PlayerManager.PlayerInfo.Stamina <= 0)
            {
                using var errorPacket = PacketMaker.U_TO_C_EXPLORE(ErrorCode.FATAL);
                user.Send(errorPacket);
                return;
            }

            await using (await ExploreTargetInfo.Lock(user.RedLock, body.ExploreTargetUid))
            {
                exploreTargetInfo = await ExploreTargetInfo.Load(body.ExploreTargetUid);
                if (exploreTargetInfo == null)
                {
                    using var errorPacket = PacketMaker.U_TO_C_EXPLORE(ErrorCode.FATAL);
                    user.Send(errorPacket);
                    return;
                }

                if (exploreTargetInfo.PlayerId != 0)
                {
                    using var errorPacket = PacketMaker.U_TO_C_EXPLORE(ErrorCode.ALREADY_ANOTHER_USE_SKILL);
                    user.Send(errorPacket);
                    return;
                }

                // if (2 < user.CurrentCell.GetDistance(exploreTargetInfo.ObjectInfo.CurrentCell))
                // {
                //     using var errorPacket = PacketMaker.U_TO_C_EXPLORE(ErrorCode.FATAL);
                //     user.Send(errorPacket);
                //     return;
                // }

                exploreTargetInfo.PlayerId = user.PlayerId;
                exploreTargetInfo.EndTimestamp = DateTime.UtcNow.AddSeconds(GameRuleData.SkillCompleteTime);
                user.StartExplore(exploreTargetInfo);
                await exploreTargetInfo.Save();
            }

            await user.SetState(PlayerState.EXPLORE_1);
            user.PlayerManager.PlayerInfo.Stamina -= 5;
            await user.PlayerManager.PlayerInfo.Save();
        }

        using var packet = PacketMaker.U_TO_C_EXPLORE(ErrorCode.SUCCESS, user.PlayerManager.PlayerInfo.JobInfo);
        user.Send(packet);
        user.BroadcastUpdateInfo(user.PlayerManager.PlayerInfo);
        user.BroadcastUpdateInfo(exploreTargetInfo);
    }

    public static async Task ExploreEnd(GameUser user, ExploreTargetInfo exploreTargetInfo)
    {
        var updateItems = new List<ItemInfo>();
        await using (await PlayerInfo.Lock(user.RedLock, user.PlayerId))
        {
            if (user.PlayerManager.PlayerInfo == null)
            {
                throw new Exception("cannot find player info");
            }
            
            // TODO 능력치 기반한 확률
            var exploreTargetData = GameExploreTargetData.Get(exploreTargetInfo.ExploreTargetId);
            if (exploreTargetData.Reusable)
            {
                // 조사대상 수집 불가능하도록 TODO 재충전
                exploreTargetInfo.PlayerId = -1;
                await exploreTargetInfo.Save();
                user.BroadcastUpdateInfo(exploreTargetInfo);
            }
            else
            {
                // 조사대상 삭제
                await exploreTargetInfo.Delete();
                user.BroadcastObjectDestroy(exploreTargetInfo.ObjectInfo);
            }

            // 수집 결과 지급
            var random = new Random();
            var rewardItemId = exploreTargetData.RewardItemPool[random.Next(0, exploreTargetData.RewardItemPool.Count)];
            var rewardItem = await InventoryController.CreateItem(rewardItemId, 1);
            
            var updateItem = user.PlayerManager.PlayerInfo.InventoryInfo.AddItem(rewardItem);
            updateItems.Add(updateItem);

            await user.PlayerManager.SetState(PlayerState.IDLE);
            await user.PlayerManager.PlayerInfo.Save();
            
            // 퀘스트 갱신
            switch (exploreTargetInfo.ExploreTargetId)
            {
                case 1:
                case 2:
                case 3:
                    await QuestController.IncreaseQuestCount(user, 100000004, 1);
                    break;
                
                case 4:
                case 5: 
                case 6:
                    await QuestController.IncreaseQuestCount(user, 100000008, 1);
                    break;
                case 7:
                    await QuestController.IncreaseQuestCount(user, 100000008, 1);
                    await QuestController.StartQuest(user, 200000001);
                    break;
                
                default:
                    break;
            }
        }
        using var packet = PacketMaker.U_TO_C_EXPLORE_COMPLETE(true, user.PlayerManager.PlayerInfo.JobInfo);
        user.Send(packet);
        user.BroadcastUpdateInfo(user.PlayerManager.PlayerInfo);
        InventoryController.SendUpdateItems(user, updateItems);
    }
}