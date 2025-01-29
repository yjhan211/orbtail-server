using MessagePack;
using network.common;
using network.common.data;
using network.common.data.models;
using network.helpers;
using network.packets;

namespace user_server.controllers;

public static class JobController
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

        DirectionType direction;
        await using (await PlayerInfo.Lock(user.RedLock, user.PlayerId))
        {
            if (user.PlayerManager.PlayerInfo == null)
            {
                using var errorPacket = PacketMaker.U_TO_C_EXPLORE(ErrorCode.FATAL);
                user.Send(errorPacket);
                return;
            }

            if (user.PlayerManager.PlayerInfo.JobInfo.Hp <= 0)
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

                if (2 < user.CurrentCell.GetDistance(exploreTargetInfo.ObjectInfo.CurrentCell))
                {
                    using var errorPacket = PacketMaker.U_TO_C_EXPLORE(ErrorCode.FATAL);
                    user.Send(errorPacket);
                    return;
                }

                // direction = exploreTargetInfo.ObjectInfo.CurrentCell.GetDirection(user.CurrentCell);
                exploreTargetInfo.PlayerId = user.PlayerId;
                exploreTargetInfo.EndTimestamp = DateTime.UtcNow.AddSeconds(GameRuleData.SkillCompleteTime);
                user.StartExplore(exploreTargetInfo);
                await exploreTargetInfo.Save();
            }

            await user.SetState(PlayerState.EXPLORE_1);
            // user.PlayerManager.PlayerInfo.JobInfo.Hp -= 1;
            await user.PlayerManager.PlayerInfo.Save();
        }

        // await user.SetFlip(direction);

        using var packet = PacketMaker.U_TO_C_EXPLORE(ErrorCode.SUCCESS, user.PlayerManager.PlayerInfo.JobInfo);
        user.Send(packet);
        user.BroadcastUpdateInfo(user.PlayerManager.PlayerInfo);
        user.BroadcastUpdateInfo(exploreTargetInfo);
    }

    public static async Task ExploreEnd(GameUser user, ExploreTargetInfo exploreTargetInfo)
    {
        await using (await PlayerInfo.Lock(user.RedLock, user.PlayerId))
        {
            if (user.PlayerManager.PlayerInfo == null)
            {
                throw new Exception("cannot find player info");
            }
            
            // TODO 능력치 기반한 확률
            var exploreTargetData = ExploreTargetData.Get(exploreTargetInfo.ExploreTargetId);
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
            
            var updateItems = new List<ItemInfo>();
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
                    await QuestController.IncreaseQuestCount(user, 4, 1);
                    break;
                
                default:
                    break;
            }

            using var packet = PacketMaker.U_TO_C_EXPLORE_COMPLETE(true, user.PlayerManager.PlayerInfo.JobInfo);
            user.Send(packet);
            user.BroadcastUpdateInfo(user.PlayerManager.PlayerInfo);
            InventoryController.SendUpdateItems(user, updateItems);
        }
    }

    // public static async Task GetJobResourceInfo(GameUser user, C_TO_U_JOB_RESOURCE_INFO body)
    // {
    //     var targetInfoList = new List<JobResourceInfo>();
    //     for (var i = 0; i < body.JobResourceIdList.Count; i++)
    //     {
    //         var targetResourceId = body.JobResourceIdList[i];
    //         JobResourceInfo? targetResourceInfo;
    //
    //         await using (await JobResourceInfo.Lock(user.RedLock, targetResourceId))
    //         {
    //             targetResourceInfo = await JobResourceInfo.Load(targetResourceId);
    //         }
    //
    //         if (targetResourceInfo == null) continue;
    //
    //         targetInfoList.Add(targetResourceInfo);
    //
    //         var isMax = targetInfoList.Count >= Config.BROADCAST_UNIT;
    //         var isEnded = i == targetInfoList.Count - 1;
    //
    //         if (!isMax && !isEnded) continue;
    //
    //         using var packet = PacketMaker.U_TO_C_JOB_RESOURCE_INFO(targetInfoList);
    //         user.Send(packet);
    //         targetInfoList.Clear();
    //     }
    // }

    // public static async Task UpgradeJob(GameUser user, C_TO_U_UPGRADE_JOB body)
    // {
    //     var playerInfo = await PlayerInfo.Load(user.PlayerId);
    //     if (playerInfo == null) throw new Exception("player_info is not exists");
    //
    //     if (!playerInfo.JobInfo.JobStatDict.TryGetValue(body.JobType, out var jobStat))
    //     {
    //         using var errorPacket = PacketMaker.U_TO_C_UPGRADE_JOB(user.PlayerId, ErrorCode.FATAL);
    //         user.Send(errorPacket);
    //         return;
    //     }
    //
    //     // var maxExp = GameDataHelper.GetMaxExp(jobStat.JobGrade);
    //     // if (jobStat.Exp < maxExp)
    //     // {
    //     //     using var errorPacket = PacketMaker.U_TO_C_UPGRADE_JOB(user.PlayerId, ErrorCode.FATAL);
    //     //     user.Send(errorPacket);
    //     //     return;
    //     // }
    //
    //     if (JobGrade.CHIEF <= jobStat.JobGrade)
    //     {
    //         using var errorPacket = PacketMaker.U_TO_C_UPGRADE_JOB(user.PlayerId, ErrorCode.FATAL);
    //         user.Send(errorPacket);
    //         return;
    //     }
    //
    //     var targetGrade = jobStat.JobGrade + 1;
    //     await using (await PlayerInfo.Lock(user.RedLock, user.PlayerId))
    //     {
    //         switch (targetGrade)
    //         {
    //             case JobGrade.RESEARCHER:
    //                 jobStat.JobGrade = JobGrade.RESEARCHER;
    //                 jobStat.Exp = 0;
    //                 break;
    //         }
    //
    //         await playerInfo.Save();
    //     }
    //
    //     using var packet = PacketMaker.U_TO_C_UPGRADE_JOB(user.PlayerId, ErrorCode.SUCCESS, playerInfo.JobInfo);
    //     user.Send(packet);
    // }

    // private static List<int> GetSkillList(PlayerInfo playerInfo)
    // {
    //     var result = new List<int>();
    //     foreach (var wearItem in playerInfo.WearItemIdList)
    //     {
    //         // var (_, skillId, _) = GameDataHelper.GetSkill(wearItem);
    //         // if (skillId <= 0) continue;
    //
    //         // result.Add(skillId);
    //     }
    //
    //     return result;
    // }

    // public static async Task UseJobSkill(GameUser user, C_TO_U_USE_SKILL body)
    // {
    //     PlayerInfo? playerInfo;
    //     JobResourceInfo? jobResourceInfo;
    //
    //     DirectionType direction;
    //
    //     await using (await PlayerInfo.Lock(user.RedLock, user.PlayerId))
    //     {
    //         playerInfo = await PlayerInfo.Load(user.PlayerId);
    //         if (playerInfo == null)
    //         {
    //             using var errorPacket = PacketMaker.U_TO_C_USE_SKILL(ErrorCode.FATAL);
    //             user.Send(errorPacket);
    //             return;
    //         }
    //
    //         if (playerInfo.JobInfo.Hp <= 0)
    //         {
    //             using var errorPacket = PacketMaker.U_TO_C_USE_SKILL(ErrorCode.FATAL);
    //             user.Send(errorPacket);
    //             return;
    //         }
    //
    //         // var skillDetail = GameDataHelper.GetSkillDetail(body.SkillId);
    //         // await using (await JobResourceInfo.Lock(user.RedLock, body.ResourceUid))
    //         // {
    //         //     jobResourceInfo = await JobResourceInfo.Load(body.ResourceUid);
    //         //     if (jobResourceInfo == null)
    //         //     {
    //         //         using var errorPacket = PacketMaker.U_TO_C_USE_SKILL(ErrorCode.FATAL);
    //         //         user.Send(errorPacket);
    //         //         return;
    //         //     }
    //         //
    //         //     if (jobResourceInfo.PlayerId != 0)
    //         //     {
    //         //         using var errorPacket = PacketMaker.U_TO_C_USE_SKILL(ErrorCode.FATAL);
    //         //         user.Send(errorPacket);
    //         //         return;
    //         //     }
    //         //
    //         //     var jobResourceDetail = GameDataHelper.GetJobResourceDetail(jobResourceInfo.ResourceId);
    //         //     var jobResourceType = jobResourceDetail.Item3;
    //         //     var jobResourceLevel = jobResourceDetail.Item4;
    //         //     var skillType = skillDetail.Item3;
    //         //
    //         //     if (skillType != JobType.NONE && skillType != jobResourceType)
    //         //     {
    //         //         using var errorPacket = PacketMaker.U_TO_C_USE_SKILL(ErrorCode.FATAL);
    //         //         user.Send(errorPacket);
    //         //         return;
    //         //     }
    //         //
    //         //     var skillList = GetSkillList(playerInfo);
    //         //     if (!skillList.Contains(body.SkillId))
    //         //     {
    //         //         using var errorPacket = PacketMaker.U_TO_C_USE_SKILL(ErrorCode.FATAL);
    //         //         user.Send(errorPacket);
    //         //         return;
    //         //     }
    //         //
    //         //     if (skillDetail.Item2 < jobResourceLevel)
    //         //     {
    //         //         using var errorPacket = PacketMaker.U_TO_C_USE_SKILL(ErrorCode.FATAL);
    //         //         user.Send(errorPacket);
    //         //         return;
    //         //     }
    //         //
    //         //     direction = user.CurrentCell.GetDirection(jobResourceInfo.ObjectInfo.CurrentCell);
    //         //     jobResourceInfo.PlayerId = user.PlayerId;
    //         //     jobResourceInfo.EndTimestamp = DateTime.UtcNow.AddSeconds(10); // TODO csv
    //         //     user.StartJobSkill(jobResourceInfo);
    //         //     await jobResourceInfo.Save();
    //         // }
    //
    //         await user.SetState(PlayerState.CHEMIST_WORK_1);
    //         playerInfo.JobInfo.Hp -= 1;
    //         await playerInfo.Save();
    //     }
    //
    //     // await user.SetFlip(direction);
    //     //
    //     // using var packet = PacketMaker.U_TO_C_USE_SKILL(ErrorCode.SUCCESS, playerInfo.JobInfo);
    //     // user.Send(packet);
    //     // user.BroadcastUpdateInfo(playerInfo);
    //     // user.BroadcastUpdateInfo(jobResourceInfo);
    // }
    //
    //
    // public static async Task JobSkillEnd(GameUser user, JobResourceInfo jobResourceInfo)
    // {
    //     // var jobResourceDetail = GameDataHelper.GetJobResourceDetail(jobResourceInfo.ResourceId);
    //     //
    //     // Random random = new();
    //     // await using (await PlayerInfo.Lock(user.RedLock, user.PlayerId))
    //     // {
    //     //     var playerInfo = await PlayerInfo.Load(user.PlayerId);
    //     //     if (playerInfo == null) throw new Exception("cannot find player info");
    //     //
    //     //     var rewardItem = jobResourceDetail.Item6[random.Next(0, jobResourceDetail.Item6.Count)];
    //     //     var rewardCount = random.Next(0, 5);
    //     //
    //     //     var itemInfo = await InventoryController.CreateItem(rewardItem, rewardCount);
    //     //     playerInfo.InventoryInfo.AddItem(itemInfo);
    //     //
    //     //     // 자원 지우고
    //     //     await jobResourceInfo.Delete();
    //     //     BroadcastObjectDestroy(user, jobResourceInfo.ObjectInfo);
    //     //
    //     //     // TODO 경험치량 조정
    //     //     var jobType = jobResourceDetail.Item3;
    //     //     if (playerInfo.JobInfo.JobStatDict.TryGetValue(jobType, out var jobStat))
    //     //     {
    //     //         var addExp = jobStat.JobGrade == JobGrade.TRAINEE ? 10 : 1;
    //     //         // 경험치 올리고
    //     //         var newExp = jobStat.Exp + addExp;
    //     //         jobStat.Exp = Math.Min(newExp, 100);
    //     //     }
    //     //
    //     //     // 스테이트 초기화
    //     //     user.SetState(playerInfo, PlayerState.NONE);
    //     //
    //     //     // 저장
    //     //     await playerInfo.Save();
    //     //
    //     //     using var packet = PacketMaker.U_TO_C_USE_SKILL_COMPLETE(true, itemInfo, playerInfo.JobInfo);
    //     //     user.Send(packet);
    //     //     user.BroadcastUpdateInfo(playerInfo);
    //     //
    //     //     await InventoryController.GetCurrentItemList(user);
    //     // }
    // }
    //
    // public static async Task Encamp(GameUser user, C_TO_U_ENCAMP body)
    // {
    //     PlayerInfo? playerInfo;
    //     CampInfo? campInfo;
    //     await using (await PlayerInfo.Lock(user.RedLock, user.PlayerId))
    //     {
    //         playerInfo = await PlayerInfo.Load(user.PlayerId);
    //         if (playerInfo == null) throw new Exception("playerInfo not exists");
    //
    //         if (!playerInfo.InventoryInfo.ItemDict.TryGetValue(body.ItemUid, out var targetItem))
    //             throw new Exception("not found item info");
    //
    //         if (await CampInfo.Load(user.PlayerId) != null) throw new Exception("already encamp");
    //
    //         campInfo = new CampInfo(playerInfo.PlayerId, playerInfo.Name, playerInfo.ObjectInfo, targetItem,
    //             playerInfo.ObjectInfo.TargetCell);
    //         user.SetState(PlayerState.CAMPING_1);
    //
    //         await campInfo.Save();
    //         await playerInfo.Save();
    //     }
    //
    //     var partKey = MapHelper.CreatePartKey(campInfo.ObjectInfo.MapId, campInfo.ObjectInfo.CurrentCell);
    //     var currentManageServer = MapHelper.GetManageServerId(partKey);
    //     var moveManageSubject = SubjectHelper.GetUpdateManageSubject(campInfo.ObjectInfo.MapId,
    //         campInfo.ObjectInfo.MapSubId, currentManageServer);
    //
    //     // 현재 담당 서버에 전송
    //     user.NatsClient.Publish(moveManageSubject, MessagePackSerializer.Serialize((partKey, campInfo.ObjectInfo)));
    //     user.BroadcastUpdateInfo(playerInfo);
    // }
    //
    // public static async Task Decamp(GameUser user)
    // {
    //     PlayerInfo? playerInfo;
    //     CampInfo? campInfo;
    //     await using (await PlayerInfo.Lock(user.RedLock, user.PlayerId))
    //     {
    //         playerInfo = await PlayerInfo.Load(user.PlayerId);
    //         if (playerInfo == null) return;
    //
    //         campInfo = await CampInfo.Load(user.PlayerId);
    //         if (campInfo == null) return;
    //
    //         playerInfo.State = PlayerState.NONE;
    //         await campInfo.Delete();
    //         await playerInfo.Save();
    //     }
    //
    //     user.BroadcastUpdateInfo(playerInfo);
    //     BroadcastObjectDestroy(user, campInfo.ObjectInfo);
    // }
    //
    // public static async Task AddSellItem(GameUser user, C_TO_U_ADD_SELL_ITEM body)
    // {
    //     CampInfo? campInfo;
    //     await using (await PlayerInfo.Lock(user.RedLock, user.PlayerId))
    //     {
    //         var playerInfo = await PlayerInfo.Load(user.PlayerId);
    //         if (playerInfo == null) return;
    //
    //         campInfo = await CampInfo.Load(user.PlayerId);
    //         if (campInfo == null) return;
    //
    //         if (!playerInfo.InventoryInfo.ItemDict.TryGetValue(body.ItemUid, out var itemInfo)) return;
    //
    //         if (3 <= campInfo.CellDict.Count) return;
    //
    //         if (itemInfo.IsWear || itemInfo.Count <= 0) return;
    //
    //         campInfo.CellDict[body.ItemUid] = (itemInfo, body.Price);
    //         await campInfo.Save();
    //     }
    //
    //     using var packet = PacketMaker.U_TO_C_ADD_SELL_ITEM(user.PlayerId);
    //     user.Send(packet);
    //     user.BroadcastUpdateInfo(campInfo);
    // }
    //
    // public static async Task DeleteSellItem(GameUser user, C_TO_U_DELETE_SELL_ITEM body)
    // {
    //     CampInfo? campInfo;
    //     await using (await PlayerInfo.Lock(user.RedLock, user.PlayerId))
    //     {
    //         var playerInfo = await PlayerInfo.Load(user.PlayerId);
    //         if (playerInfo == null) return;
    //
    //         campInfo = await CampInfo.Load(user.PlayerId);
    //         if (campInfo == null) return;
    //
    //         if (!playerInfo.InventoryInfo.ItemDict.TryGetValue(body.ItemUid, out var itemInfo)) return;
    //
    //         if (itemInfo.IsWear || itemInfo.Count <= 0) return;
    //
    //         campInfo.CellDict.Remove(body.ItemUid);
    //         await campInfo.Save();
    //     }
    //
    //     using var packet = PacketMaker.U_TO_C_DELETE_SELL_ITEM(user.PlayerId);
    //     user.Send(packet);
    //     user.BroadcastUpdateInfo(campInfo);
    // }
    //
    // public static async Task BuyItem(GameUser user, C_TO_U_BUY_ITEM body)
    // {
    //     PlayerInfo? playerInfo;
    //     PlayerInfo? sellerInfo;
    //     CampInfo? campInfo;
    //
    //     await using (await PlayerInfo.Lock(user.RedLock, user.PlayerId))
    //     {
    //         playerInfo = await PlayerInfo.Load(user.PlayerId);
    //         if (playerInfo == null) return;
    //
    //         campInfo = await CampInfo.Load(body.SellerId);
    //         if (campInfo == null) return;
    //
    //         if (!campInfo.CellDict.TryGetValue(body.SellItemUid, out var sellItemInfo)) return;
    //
    //         var price = sellItemInfo.Item2;
    //         if (playerInfo.Gold < price) return;
    //
    //         await using (await PlayerInfo.Lock(user.RedLock, body.SellerId))
    //         {
    //             sellerInfo = await PlayerInfo.Load(body.SellerId);
    //             if (sellerInfo == null) return;
    //
    //             sellerInfo.Gold += price;
    //             playerInfo.Gold -= price;
    //
    //             campInfo.CellDict.Remove(body.SellItemUid);
    //             sellerInfo.InventoryInfo.ItemDict.Remove(body.SellItemUid);
    //             playerInfo.InventoryInfo.AddItem(sellItemInfo.Item1);
    //
    //             await campInfo.Save();
    //             await sellerInfo.Save();
    //             await playerInfo.Save();
    //         }
    //     }
    //
    //     using var buyPacket = PacketMaker.U_TO_C_BUY_ITEM(user.PlayerId, playerInfo);
    //     user.Send(buyPacket);
    //
    //     await InventoryController.GetCurrentItemList(user);
    //     
    //     // TODO
    //     // using var sellerPacket = PacketMaker.U_TO_U_PLAYER_INFO(sellerInfo);
    //     // user.NatsClient.Publish(sellerInfo.ObjectInfo.GetGameObjectKey(), sellerPacket.ToBytes());
    //     // user.BroadcastUpdateInfo(campInfo);
    // }
    //
    // private static void BroadcastJobResourceCreate(GameUser user, JobResourceInfo jobResourceInfo)
    // {
    //     var positionKey =
    //         MapHelper.CreatePartKey(jobResourceInfo.ObjectInfo.MapId, jobResourceInfo.ObjectInfo.CurrentCell);
    //     var manageServer = MapHelper.GetManageServerId(positionKey);
    //     var subject = SubjectHelper.GetCreateJobResourceSubject(jobResourceInfo.ObjectInfo.MapId,
    //         jobResourceInfo.ObjectInfo.MapSubId, manageServer);
    //     var message = MessagePackSerializer.Serialize((positionKey, jobResourceInfo, jobResourceInfo.ObjectInfo));
    //
    //     user.NatsClient.Publish(subject, message);
    // }
}