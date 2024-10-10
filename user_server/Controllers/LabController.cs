using StackExchange.Redis;
using MessagePack;
using network.common;
using network.helpers;
using network.packets;

namespace user_server.controllers
{
    public static class LabController
    {
        public const string HIRE_KEY = "lab_hire_list";

        public static async Task CreateLab(GameUser user, C_TO_U_CREATE_LAB body)
        {
            PlayerInfo? playerInfo;
            LabInfo? labInfo;
            using (await PlayerInfo.Lock(user.RedLock, user.PlayerId))
            {
                playerInfo = await PlayerInfo.Load(user.PlayerId);
                if (playerInfo == null)
                {
                    throw new Exception("player_info not exists");
                }

                bool isValidGrade = playerInfo.JobInfo.JobStatDict.Values.Any(stat => stat.JobGrade >= JobGrade.RESEARCHER);
                if (!isValidGrade)
                {
                    throw new Exception("not enough job grade");
                }

                if (playerInfo.LabId > 0)
                {
                    throw new Exception("Already joined lab");
                }

                // TODO RDB PK로 교체 예정
                long labId = await CacheHelper.Instance.StringIncrementAsync("lab_id");
                labInfo = new(labId, playerInfo.PlayerId, playerInfo.Name, body.LabName);
                await labInfo.Save();

                playerInfo.LabId = labId;
                playerInfo.LabName = body.LabName;
                await playerInfo.Save();
            }

            using var packet = PacketMaker.U_TO_C_CREATE_LAB(user.PlayerId, playerInfo, labInfo);
            user.SendToClient(packet);
        }

        public static async Task UpgradeResearch(GameUser user, C_TO_U_UPGRADE_RESEARCH body)
        {
            PlayerInfo? playerInfo;
            LabInfo? labInfo;
            using (await PlayerInfo.Lock(user.RedLock, user.PlayerId))
            {
                playerInfo = await PlayerInfo.Load(user.PlayerId);
                if (playerInfo == null)
                {
                    throw new Exception("player_info not exists");
                }

                if (playerInfo.LabId == 0)
                {
                    throw new Exception("not joined lab");
                }

                labInfo = await LabInfo.Load(playerInfo.LabId);
                if (labInfo == null)
                {
                    throw new Exception("lab info load fail.");
                }

                using (await LabInfo.Lock(user.RedLock, labInfo.LabId))
                {
                    // 신규 생성 연구면 자격 요건 확인 후 1레벨 생성
                    if (!labInfo.ResearchInfoDict.TryGetValue(body.ResearchId, out ResearchInfo? researchInfo))
                    {
                        var requireList = GameDesignData.GetRequireResearch(body.ResearchId);
                        foreach (var requireResearch in requireList)
                        {
                            if (!labInfo.ResearchInfoDict.TryGetValue(requireResearch.ResearchId, out var findResearch))
                            {
                                throw new Exception("require research not found");
                            }

                            if (findResearch.Level < requireResearch.Level)
                            {
                                throw new Exception("not enough research level");
                            }
                        }
                        researchInfo = new(body.ResearchId, 1);
                    }
                    else // 기존 연구 업그레이드면 개인 연구 포인트 차감 / 연구소 연구 포인트 증가
                    {
                        if (!researchInfo.PointDict.TryGetValue(body.JobType, out var _))
                        {
                            throw new Exception($"invalid upgrade job type");
                        }

                        if ((researchInfo.Level * 10) <= researchInfo.PointDict[body.JobType])
                        {
                            throw new Exception($"invalid current point.");
                        }

                        var total_point = playerInfo.JobInfo.ResearchPointDict[body.JobType].Point;
                        var use_point = playerInfo.JobInfo.ResearchPointDict[body.JobType].UsePoint;
                        if (total_point <= use_point)
                        {
                            throw new Exception($"empty point");
                        }

                        playerInfo.JobInfo.ResearchPointDict[body.JobType].UsePoint += 1;
                        researchInfo.PointDict[body.JobType] += 1;

                        bool isUpgradeLevel = true;
                        foreach (var point in researchInfo.PointDict.Values)
                        {
                            if (point < researchInfo.Level * 10)
                            {
                                isUpgradeLevel = false;
                                break;
                            }
                        }

                        if (isUpgradeLevel)
                        {
                            researchInfo.Level += 1;
                            researchInfo.InitResearchPoint();
                        }
                    }

                    labInfo.ResearchInfoDict[researchInfo.ResearchId] = researchInfo;

                    await labInfo.Save();
                    await playerInfo.Save();
                }
            }

            using var researchPacket = PacketMaker.U_TO_C_UPGRADE_RESEARCH(labInfo.ResearchInfoDict, playerInfo.JobInfo);
            user.SendToClient(researchPacket);

            using var labInfoPacket = PacketMaker.U_TO_C_LAB_INFO(new(), labInfo);
            user.PublishToClients(labInfoPacket, labInfo.MemberDict.Keys.ToList());

            await InventoryController.GetCurrentItemList(user);
        }

        public static async Task Make(GameUser user, C_TO_U_MAKE body)
        {
            var makeableItemId = 0;
            PlayerInfo? playerInfo;
            using (await PlayerInfo.Lock(user.RedLock, user.PlayerId))
            {
                playerInfo = await PlayerInfo.Load(user.PlayerId);
                if (playerInfo == null)
                {
                    throw new Exception("player_info not exists");
                }

                List<(int, int)> validator = new();
                foreach (var materialSlot in body.Materials)
                {
                    if (playerInfo.InventoryInfo.ItemDict.TryGetValue(materialSlot.Key, out var slotItemInfo))
                    {
                        validator.Add((slotItemInfo.ItemId, materialSlot.Value));
                    }
                }

                var labInfo = await LabInfo.Load(playerInfo.LabId);
                if (labInfo == null)
                {
                    throw new Exception("lab_info not exists");
                }

                var research_list = labInfo.ResearchInfoDict.Values.Select((x) => (x.ResearchId, x.Level)).ToList();
                makeableItemId = GameDesignData.GetMakableItemId(research_list, validator);
                if (makeableItemId != 0)
                {
                    var itenInfo = await InventoryController.CreateItem(makeableItemId, 1);
                    playerInfo.InventoryInfo.AddItem(itenInfo);
                }

                foreach (var materialInfo in body.Materials)
                {
                    var materialItemUid = materialInfo.Key;
                    var materialItemCount = materialInfo.Value;

                    if (playerInfo.InventoryInfo.ItemDict.TryGetValue(materialItemUid, out var materialItem))
                    {
                        materialItem.Count -= materialItemCount;
                        if (materialItem.Count == 0)
                        {
                            playerInfo.InventoryInfo.ItemDict.Remove(materialItemUid);
                        }
                    }
                }

                await playerInfo.Save();
            }

            using var packet = PacketMaker.U_TO_C_MAKE(makeableItemId != 0);
            user.SendToClient(packet);

            await InventoryController.GetCurrentItemList(user);
        }

        public static async Task WriteLabHire(GameUser user, C_TO_U_WRITE_LAB_HIRE body)
        {
            PlayerInfo? playerInfo = await PlayerInfo.Load(user.PlayerId);
            if (playerInfo == null)
            {
                throw new Exception("player_info not exists");
            }

            if (playerInfo.LabId == 0)
            {
                throw new Exception("not joined lab");
            }

            await CacheHelper.Instance.HashSetAsync(HIRE_KEY, $"{playerInfo.LabId}", MessagePackSerializer.Serialize(body.Comment));

            var packet = PacketMaker.U_TO_C_WRITE_LAB_HIRE(ErrorCode.SUCCESS);
            user.SendToClient(packet);
        }

        public static async Task LabHireList(GameUser user)
        {
            PlayerInfo? playerInfo = await PlayerInfo.Load(user.PlayerId);
            if (playerInfo == null)
            {
                throw new Exception("player_info not exists");
            }

            List<(long, string, string)> hireList = new();
            var redisValues = await CacheHelper.Instance.HashGetAllAsync(HIRE_KEY);
            if (redisValues != null)
            {
                foreach (HashEntry entry in redisValues)
                {
                    var field = entry.Name;
                    if (!int.TryParse(field, out int labId))
                    {
                        continue;
                    }
                    // TODO 길드명도 그렇고 유저명도 캐싱 필드 분리해야됨 일단 시간이 없어서 그냥 둠..
                    var labInfo = await LabInfo.Load(labId);
                    if (labInfo == null)
                    {
                        continue;
                    }
                    var comment = MessagePackSerializer.Deserialize<string>(entry.Value);
                    hireList.Add((labId, labInfo.LabName, comment));
                }
            }

            using var packet = PacketMaker.U_TO_C_LAB_HIRE_LIST(hireList);
            user.SendToClient(packet);
        }

        public static async Task JoinLab(GameUser user, C_TO_U_JOIN_LAB body)
        {
            PlayerInfo? playerInfo;
            LabInfo? labInfo;
            using (await PlayerInfo.Lock(user.RedLock, user.PlayerId))
            {
                playerInfo = await PlayerInfo.Load(user.PlayerId);
                if (playerInfo == null)
                {
                    throw new Exception("player_info not exists");
                }

                if (playerInfo.LabId == body.LabId)
                {
                    throw new Exception("same lab id");
                }

                using (await LabInfo.Lock(user.RedLock, body.LabId))
                {
                    labInfo = await LabInfo.Load(body.LabId);
                    if (labInfo == null)
                    {
                        throw new Exception("player_info not exists");
                    }

                    if (GameDesignData.GetMaxLabMemberNum(labInfo.LabGrade) <= labInfo.MemberDict.Count)
                    {
                        throw new Exception($"member full");
                    }

                    // 과거 랩
                    if (playerInfo.LabId != 0)
                    {
                        using (await LabInfo.Lock(user.RedLock, playerInfo.LabId))
                        {
                            var lastLabInfo = await LabInfo.Load(playerInfo.LabId);
                            if (lastLabInfo != null)
                            {
                                // 기술 이전
                                // TODO 길드탈퇴 고려
                                foreach (var lastResearch in lastLabInfo.ResearchInfoDict)
                                {
                                    var research = lastResearch.Value;
                                    labInfo.ResearchInfoDict[research.ResearchId] = research;
                                }
                            }

                            // 기존 랩 탈퇴
                            await LabInfo.Delete(playerInfo.LabId);
                        }
                    }

                    // 랩 이전
                    playerInfo.LabId = labInfo.LabId;
                    playerInfo.LabName = labInfo.LabName;
                    labInfo.MemberDict.Add(playerInfo.PlayerId, playerInfo.Name);

                    await labInfo.Save();
                    await playerInfo.Save();
                }
            }

            await CacheHelper.Instance.HashDeleteAsync(HIRE_KEY, $"{playerInfo.LabId}");
            await InventoryController.GetLabInventory(user);

            using var packet = PacketMaker.U_TO_C_LAB_INFO(playerInfo, labInfo);
            foreach (var labMenber in labInfo.MemberDict)
            {
                user.NatsClient.Publish(GameObjectInfo.MakeHashField(ObjectType.PLAYER, labMenber.Key), packet.ToBytes());
            }
        }

        public static async Task<List<int>> GetUseableResearchList(PlayerInfo playerInfo)
        {
            var labInfo = await LabInfo.Load(playerInfo.LabId);
            return labInfo?.ResearchInfoDict.Select(skill => skill.Value.ResearchId).ToList() ?? new();
        }

        public static void SendLabItemList(GameUser user, Dictionary<long, ItemInfo> itemDict)
        {
            if (itemDict.Count == 0)
            {
                using var packet = PacketMaker.U_TO_C_LAB_INVENTORY(new(), true);
                user.SendToClient(packet);
            }

            int index = 0;
            var itemKeys = itemDict.Keys.ToArray();
            while (index < itemKeys.Length)
            {
                var batchDict = new Dictionary<long, ItemInfo>();

                for (int i = index; i < index + Config.BROADCAST_UNIT && i < itemKeys.Length; i++)
                {
                    var key = itemKeys[i];
                    batchDict[key] = itemDict[key];
                }

                var isEnded = index + Config.BROADCAST_UNIT >= itemKeys.Length;

                using var inventoryPacket = PacketMaker.U_TO_C_LAB_INVENTORY(batchDict, isEnded);
                user.SendToClient(inventoryPacket);

                index += Config.BROADCAST_UNIT;
            }
        }

    }
}
