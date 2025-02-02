using MessagePack;
using network.common;
using network.common.data.models;
using network.helpers;
using network.packets;

namespace user_server.controllers;

public static class LabController
{
    private const string HireKey = "lab_hire_list";

    public static async Task CreateLab(GameUser user, C_TO_U_CREATE_LAB body)
    {
        PlayerInfo? playerInfo;
        LabInfo? labInfo;
        await using (await PlayerInfo.Lock(user.RedLock, user.PlayerId))
        {
            playerInfo = await PlayerInfo.Load(user.PlayerId);
            if (playerInfo == null) throw new Exception("player_info not exists");

            var isValidGrade = playerInfo.JobInfo.JobStatDict.Values.Any(stat => stat.JobGrade >= JobGrade.RESEARCHER);
            if (!isValidGrade) throw new Exception("not enough job grade");

            if (playerInfo.LabId > 0) throw new Exception("Already joined lab");

            // TODO RDB PK로 교체 예정
            var labId = await CacheHelper.Instance.StringIncrementAsync("lab_id");
            labInfo = new LabInfo(labId, playerInfo.PlayerId, playerInfo.Name, body.LabName);
            await labInfo.Save();

            playerInfo.LabId = labId;
            playerInfo.LabName = body.LabName;
            await playerInfo.Save();
        }

        using var packet = PacketMaker.U_TO_C_CREATE_LAB(user.PlayerId, playerInfo, labInfo);
        user.Send(packet);
    }

    public static async Task UpgradeResearch(GameUser user, C_TO_U_UPGRADE_RESEARCH body)
    {
        PlayerInfo? playerInfo;
        LabInfo? labInfo;
        await using (await PlayerInfo.Lock(user.RedLock, user.PlayerId))
        {
            playerInfo = await PlayerInfo.Load(user.PlayerId);
            if (playerInfo == null) throw new Exception("player_info not exists");

            if (playerInfo.LabId == 0) throw new Exception("not joined lab");

            labInfo = await LabInfo.Load(playerInfo.LabId);
            if (labInfo == null) throw new Exception("lab info load fail.");

            await using (await LabInfo.Lock(user.RedLock, labInfo.LabId))
            {
                // 신규 생성 연구면 자격 요건 확인 후 1레벨 생성
                // if (!labInfo.ResearchInfoDict.TryGetValue(body.ResearchId, out var researchInfo))
                // {
                //     var requireList = GameDataHelper.GetRequireResearch(body.ResearchId);
                //     foreach (var requireResearch in requireList)
                //     {
                //         if (!labInfo.ResearchInfoDict.TryGetValue(requireResearch.ResearchId, out var findResearch))
                //             throw new Exception("require research not found");
                //
                //         if (findResearch.Level < requireResearch.Level)
                //             throw new Exception("not enough research level");
                //     }
                //
                //     researchInfo = new ResearchInfo(body.ResearchId, 1);
                // }
                // else // 기존 연구 업그레이드면 개인 연구 포인트 차감 / 연구소 연구 포인트 증가
                // {
                //     if (!researchInfo.PointDict.TryGetValue(body.JobType, out _))
                //         throw new Exception("invalid upgrade job type");
                //
                //     if (researchInfo.Level * 10 <= researchInfo.PointDict[body.JobType])
                //         throw new Exception("invalid current point.");
                //
                //     var totalPoint = playerInfo.JobInfo.ResearchPointDict[body.JobType].Point;
                //     var usePoint = playerInfo.JobInfo.ResearchPointDict[body.JobType].UsePoint;
                //     if (totalPoint <= usePoint) throw new Exception("empty point");
                //
                //     playerInfo.JobInfo.ResearchPointDict[body.JobType].UsePoint += 1;
                //     researchInfo.PointDict[body.JobType] += 1;
                //
                //     var isUpgradeLevel = true;
                //     foreach (var point in researchInfo.PointDict.Values)
                //         if (point < researchInfo.Level * 10)
                //         {
                //             isUpgradeLevel = false;
                //             break;
                //         }
                //
                //     if (isUpgradeLevel)
                //     {
                //         researchInfo.Level += 1;
                //         researchInfo.InitResearchPoint();
                //     }
                // }

                // labInfo.ResearchInfoDict[researchInfo.ResearchId] = researchInfo;

                await labInfo.Save();
                await playerInfo.Save();
            }
        }

        using var researchPacket = PacketMaker.U_TO_C_UPGRADE_RESEARCH(labInfo.ResearchInfoDict, playerInfo.JobInfo);
        user.Send(researchPacket);

        using var labInfoPacket = PacketMaker.U_TO_C_LAB_INFO(new PlayerInfo(), labInfo);
        user.PublishToClients(labInfoPacket, labInfo.MemberDict.Keys.ToList());

        InventoryController.SendCurrentItems(user);
    }
    
    public static async Task WriteLabHire(GameUser user, C_TO_U_WRITE_LAB_HIRE body)
    {
        var playerInfo = await PlayerInfo.Load(user.PlayerId);
        if (playerInfo == null) throw new Exception("player_info not exists");

        if (playerInfo.LabId == 0) throw new Exception("not joined lab");

        await CacheHelper.Instance.HashSetAsync(HireKey, $"{playerInfo.LabId}",
            MessagePackSerializer.Serialize(body.Comment));

        var packet = PacketMaker.U_TO_C_WRITE_LAB_HIRE(ErrorCode.SUCCESS);
        user.Send(packet);
    }

    public static async Task LabHireList(GameUser user)
    {
        var playerInfo = await PlayerInfo.Load(user.PlayerId);
        if (playerInfo == null) throw new Exception("player_info not exists");

        List<(long, string, string)> hireList = [];
        var redisValues = await CacheHelper.Instance.HashGetAllAsync(HireKey);
        foreach (var entry in redisValues)
        {
            var field = entry.Name;
            if (!int.TryParse(field, out var labId)) continue;
            // TODO 길드명도 그렇고 유저명도 캐싱 필드 분리해야됨 일단 시간이 없어서 그냥 둠..
            var labInfo = await LabInfo.Load(labId);
            if (labInfo == null) continue;
            var comment = MessagePackSerializer.Deserialize<string>(entry.Value);
            hireList.Add((labId, labInfo.LabName, comment));
        }

        using var packet = PacketMaker.U_TO_C_LAB_HIRE_LIST(hireList);
        user.Send(packet);
    }

    public static async Task JoinLab(GameUser user, C_TO_U_JOIN_LAB body)
    {
        // PlayerInfo? playerInfo;
        // LabInfo? labInfo;
        // await using (await PlayerInfo.Lock(user.RedLock, user.PlayerId))
        // {
        //     playerInfo = await PlayerInfo.Load(user.PlayerId);
        //     if (playerInfo == null) throw new Exception("player_info not exists");
        //
        //     if (playerInfo.LabId == body.LabId) throw new Exception("same lab id");
        //
        //     await using (await LabInfo.Lock(user.RedLock, body.LabId))
        //     {
        //         labInfo = await LabInfo.Load(body.LabId);
        //         if (labInfo == null) throw new Exception("player_info not exists");
        //
        //         if (GameDataHelper.GetMaxLabMemberNum(labInfo.LabGrade) <= labInfo.MemberDict.Count)
        //             throw new Exception("member full");
        //
        //         // 과거 랩
        //         if (playerInfo.LabId != 0)
        //             await using (await LabInfo.Lock(user.RedLock, playerInfo.LabId))
        //             {
        //                 var lastLabInfo = await LabInfo.Load(playerInfo.LabId);
        //                 if (lastLabInfo != null)
        //                     // 기술 이전
        //                     // TODO 길드탈퇴 고려
        //                     foreach (var lastResearch in lastLabInfo.ResearchInfoDict)
        //                     {
        //                         var research = lastResearch.Value;
        //                         labInfo.ResearchInfoDict[research.ResearchId] = research;
        //                     }
        //
        //                 // 기존 랩 탈퇴
        //                 await LabInfo.Delete(playerInfo.LabId);
        //             }
        //
        //         // 랩 이전
        //         playerInfo.LabId = labInfo.LabId;
        //         playerInfo.LabName = labInfo.LabName;
        //         labInfo.MemberDict.Add(playerInfo.PlayerId, playerInfo.Name);
        //
        //         await labInfo.Save();
        //         await playerInfo.Save();
        //     }
        // }

        // await CacheHelper.Instance.HashDeleteAsync(HireKey, $"{playerInfo.LabId}");
        // await InventoryController.GetLabInventory(user);
        //
        // using var packet = PacketMaker.U_TO_C_LAB_INFO(playerInfo, labInfo);
        // foreach (var labMember in labInfo.MemberDict)
        //     user.NatsClient.Publish(GameObjectInfo.MakeObjectKey(ObjectType.PLAYER, labMember.Key), packet.ToBytes());
    }

    public static void SendLabItemList(GameUser user, Dictionary<long, ItemInfo> itemDict)
    {
        if (itemDict.Count == 0)
        {
            using var packet = PacketMaker.U_TO_C_LAB_INVENTORY(new Dictionary<long, ItemInfo>(), true);
            user.Send(packet);
        }

        var index = 0;
        var itemKeys = itemDict.Keys.ToArray();
        while (index < itemKeys.Length)
        {
            var batchDict = new Dictionary<long, ItemInfo>();

            for (var i = index; i < index + Config.BROADCAST_UNIT && i < itemKeys.Length; i++)
            {
                var key = itemKeys[i];
                batchDict[key] = itemDict[key];
            }

            var isEnded = index + Config.BROADCAST_UNIT >= itemKeys.Length;

            using var inventoryPacket = PacketMaker.U_TO_C_LAB_INVENTORY(batchDict, isEnded);
            user.Send(inventoryPacket);

            index += Config.BROADCAST_UNIT;
        }
    }
}