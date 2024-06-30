namespace user_server
{
    using MessagePack;
    using network;
    using StackExchange.Redis;

    public static class LabController
    {
        public static async Task CreateLab(GameUser user, C_TO_U_CREATE_LAB body)
        {
            PlayerInfo? player_info;
            LabInfo? lab_info;
            using (await PlayerInfo.Lock(user.redlock, user.player_id))
            {
                player_info = await PlayerInfo.Load(user.player_id);
                if (player_info == null)
                {
                    throw new Exception("player_info not exists");
                }

                bool is_valid_grade = player_info.job_info.job_stat_dict.Values.Any(
                    stat => stat.job_grade >= JobGrade.RESEARCHER
                );

                if (!is_valid_grade)
                {
                    throw new Exception("not enough job grade");
                }

                if (player_info.lab_id != 0)
                {
                    throw new Exception("Already joined lab");
                }

                // TODO RDB PK로 교체 예정
                long lab_id = await CacheHelper.Instance.StringIncrementAsync("lab_id");
                lab_info = new(lab_id, player_info.player_id, player_info.name, body.lab_name);
                await lab_info.Save();

                player_info.lab_id = lab_id;
                player_info.lab_name = body.lab_name;
                await player_info.Save();
            }

            Packet? packet = null;
            try
            {
                packet = PacketMaker.U_TO_C_CREATE_LAB(user.player_id, player_info, lab_info);
                user.SendToClient(packet);
            }
            catch (Exception e)
            {
                LogManager.WriteErrorLog(e);
            }
            finally
            {
                if (packet != null)
                {
                    Packet.Destroy(packet);
                }
            }
        }

        static string hire_key = "lab_hire_list";

        public static async Task WriteLabHire(GameUser user, C_TO_U_WRITE_LAB_HIRE body)
        {
            PlayerInfo? player_info = await PlayerInfo.Load(user.player_id);

            if (player_info == null)
            {
                throw new Exception("player_info not exists");
            }

            if (player_info.lab_id == 0)
            {
                throw new Exception("not joined lab");
            }

            await CacheHelper.Instance.HashSetAsync(
                hire_key,
                $"{player_info.lab_id}",
                MessagePackSerializer.Serialize(body.comment)
            );

            Packet? packet = null;
            try
            {
                packet = PacketMaker.U_TO_C_WRITE_LAB_HIRE(ErrorCode.SUCCESS);
                user.SendToClient(packet);
            }
            catch (Exception e)
            {
                LogManager.WriteErrorLog(e);
            }
            finally
            {
                if (packet != null)
                {
                    Packet.Destroy(packet);
                }
            }
        }

        public static async Task LabHireList(GameUser user)
        {
            PlayerInfo? player_info = await PlayerInfo.Load(user.player_id);
            if (player_info == null)
            {
                throw new Exception("player_info not exists");
            }

            List<(long, string, string)> hire_list = new();

            var redis_values = await CacheHelper.Instance.HashGetAllAsync(hire_key);
            if (redis_values != null)
            {
                foreach (HashEntry entry in redis_values)
                {
                    var field = entry.Name;
                    if (!int.TryParse(field, out int lab_id))
                    {
                        continue;
                    }
                    // TODO 길드명도 그렇고 유저명도 캐싱 필드 분리해야됨 일단 시간이 없어서 그냥 둠..
                    var lab_info = await LabInfo.Load(lab_id);
                    if (lab_info == null)
                    {
                        continue;
                    }
                    var comment = MessagePackSerializer.Deserialize<string>(entry.Value);
                    hire_list.Add((lab_id, lab_info.lab_name, comment));
                }
            }

            Packet? packet = null;
            try
            {
                packet = PacketMaker.U_TO_C_LAB_HIRE_LIST(hire_list);
                user.SendToClient(packet);
            }
            catch (Exception e)
            {
                LogManager.WriteErrorLog(e);
            }
            finally
            {
                if (packet != null)
                {
                    Packet.Destroy(packet);
                }
            }
        }

        public static async Task JoinLab(GameUser user, C_TO_U_JOIN_LAB body)
        {
            PlayerInfo? player_info;
            LabInfo? lab_info;
            using (await PlayerInfo.Lock(user.redlock, user.player_id))
            {
                player_info = await PlayerInfo.Load(user.player_id);
                if (player_info == null)
                {
                    throw new Exception("player_info not exists");
                }

                if (player_info.lab_id == body.lab_id)
                {
                    throw new Exception("same lab id");
                }

                using (await LabInfo.Lock(user.redlock, body.lab_id))
                {
                    lab_info = await LabInfo.Load(body.lab_id);
                    if (lab_info == null)
                    {
                        throw new Exception("player_info not exists");
                    }

                    if (
                        GameDesignData.GetMaxLabMemberNum(lab_info.lab_grade)
                        <= lab_info.member_dict.Count
                    )
                    {
                        throw new Exception($"member full");
                    }

                    // 과거 랩
                    if (player_info.lab_id != 0)
                    {
                        using (await LabInfo.Lock(user.redlock, player_info.lab_id))
                        {
                            var last_lab_info = await LabInfo.Load(player_info.lab_id);
                            if (last_lab_info != null)
                            {
                                // 기술 이전
                                // TODO 길드탈퇴 고려
                                foreach (var last_reserach in last_lab_info.reserach_info_dict)
                                {
                                    var research = last_reserach.Value;
                                    lab_info.reserach_info_dict[research.research_id] = research;
                                }
                            }

                            // 기존 랩 탈퇴
                            await LabInfo.Delete(player_info.lab_id);
                        }
                    }

                    // 랩 이전
                    player_info.lab_id = lab_info.lab_id;
                    player_info.lab_name = lab_info.lab_name;
                    lab_info.member_dict.Add(player_info.player_id, player_info.name);

                    await lab_info.Save();
                    await player_info.Save();
                }
            }

            await CacheHelper.Instance.HashDeleteAsync(hire_key, $"{player_info.lab_id}");

            Packet? packet = null;
            try
            {
                packet = PacketMaker.U_TO_C_LAB_INFO(player_info, lab_info);
                foreach (var lab_member in lab_info.member_dict)
                {
                    user.nats_client.Publish(
                        GameObjectInfo.MakeHashField(ObjectType.PLAYER, lab_member.Key),
                        packet.ToBytes()
                    );
                }
            }
            catch (Exception e)
            {
                LogManager.WriteErrorLog(e);
            }
            finally
            {
                if (packet != null)
                {
                    Packet.Destroy(packet);
                }
            }

            // 랩 인벤토리 정보 전송
            await InventoryController.GetLabInventory(user);
        }

        public static async Task Make(GameUser user, C_TO_U_MAKE body)
        {
            var makeable_item_id = 0;
            PlayerInfo? player_info;
            using (await PlayerInfo.Lock(user.redlock, user.player_id))
            {
                player_info = await PlayerInfo.Load(user.player_id);
                if (player_info == null)
                {
                    throw new Exception("player_info not exists");
                }

                List<(int, int)> validator = new();
                foreach (var material_slot in body.materials)
                {
                    if (
                        player_info.inventory_info.item_dict.TryGetValue(
                            material_slot.Key,
                            out var slot_item_info
                        )
                    )
                    {
                        validator.Add((slot_item_info.item_id, material_slot.Value));
                    }
                }

                var lab_info = await LabInfo.Load(player_info.player_id);
                if (lab_info == null)
                {
                    throw new Exception("lab_info not exists");
                }

                var research_list = lab_info.reserach_info_dict.Values
                    .Select((x) => (x.research_id, x.level))
                    .ToList();

                makeable_item_id = GameDesignData.GetMakableItemId(research_list, validator);
                if (makeable_item_id != 0)
                {
                    ItemInfo item_info = await InventoryController.CreateItem(
                        user,
                        makeable_item_id,
                        1
                    );
                    player_info.inventory_info.AddItem(item_info);
                }

                foreach (var material_info in body.materials)
                {
                    var material_item_uid = material_info.Key;
                    var material_item_count = material_info.Value;

                    if (
                        player_info.inventory_info.item_dict.TryGetValue(
                            material_item_uid,
                            out var material_item
                        )
                    )
                    {
                        material_item.count -= material_item_count;
                        if (material_item.count == 0)
                        {
                            player_info.inventory_info.item_dict.Remove(material_item_uid);
                        }
                    }
                }

                await player_info.Save();
            }

            Packet? packet = null;
            try
            {
                packet = PacketMaker.U_TO_C_MAKE(makeable_item_id != 0);
                user.SendToClient(packet);
            }
            catch (Exception e)
            {
                LogManager.WriteErrorLog(e);
            }
            finally
            {
                if (packet != null)
                {
                    Packet.Destroy(packet);
                }
            }

            await InventoryController.GetCurrentItemList(user);
        }

        public static async Task<List<int>> GetUseableResearchList(PlayerInfo player_info)
        {
            var lab_info = await LabInfo.Load(player_info.lab_id);

            return lab_info?.reserach_info_dict.Select(skill => skill.Value.research_id).ToList()
                ?? new();
        }

        public static async Task UpgradeResearch(GameUser user, C_TO_U_UPGRADE_RESEARCH body)
        {
            PlayerInfo? player_info;
            LabInfo? lab_info;
            using (await PlayerInfo.Lock(user.redlock, user.player_id))
            {
                player_info = await PlayerInfo.Load(user.player_id);
                if (player_info == null)
                {
                    throw new Exception("player_info not exists");
                }

                if (player_info.lab_id == 0)
                {
                    throw new Exception("not joined lab");
                }

                lab_info = await LabInfo.Load(player_info.lab_id);
                if (lab_info == null)
                {
                    throw new Exception("lab info load fail.");
                }

                using (await LabInfo.Lock(user.redlock, lab_info.lab_id))
                {
                    ResearchInfo? research_info;
                    // 신규 생성 연구면 자격 요건 확인 후 1레벨 생성
                    if (
                        !lab_info.reserach_info_dict.TryGetValue(
                            body.research_id,
                            out research_info
                        )
                    )
                    {
                        var require_list = GameDesignData.GetRequireResearch(body.research_id);
                        foreach (var require_research in require_list)
                        {
                            if (
                                !lab_info.reserach_info_dict.TryGetValue(
                                    require_research.research_id,
                                    out var find_research
                                )
                            )
                            {
                                throw new Exception("require research not found");
                            }

                            if (find_research.level < require_research.level)
                            {
                                throw new Exception("not enough research level");
                            }
                        }
                        research_info = new(body.research_id, 1);
                    }
                    else // 기존 연구 업그레이드면 개인 연구 포인트 차감 / 연구소 연구 포인트 증가
                    {
                        if (!research_info.point_dict.TryGetValue(body.job_type, out var _))
                        {
                            throw new Exception(
                                $"invalid upgrade job type. job_type:{body.job_type}, research_id:{body.research_id}"
                            );
                        }

                        if ((research_info.level * 10) <= research_info.point_dict[body.job_type])
                        {
                            throw new Exception(
                                $"invalid current point. job_type:{body.job_type}, research_id:{body.research_id}, current_point:{research_info.point_dict[body.job_type]}"
                            );
                        }

                        var total_point = player_info.job_info.research_point_dict[
                            body.job_type
                        ].point;
                        var use_point = player_info.job_info.research_point_dict[
                            body.job_type
                        ].use_point;

                        if (total_point <= use_point)
                        {
                            throw new Exception($"empty point");
                        }

                        player_info.job_info.research_point_dict[body.job_type].use_point += 1;
                        research_info.point_dict[body.job_type] += 1;

                        bool is_upgrade_level = true;
                        foreach (var point in research_info.point_dict.Values)
                        {
                            if (point < research_info.level * 10)
                            {
                                is_upgrade_level = false;
                                break;
                            }
                        }

                        if (is_upgrade_level)
                        {
                            research_info.level += 1;
                            research_info.InitResearchPoint();
                        }
                    }

                    lab_info.reserach_info_dict[research_info.research_id] = research_info;

                    await lab_info.Save();
                    await player_info.Save();
                }
            }

            Packet? packet = null;
            try
            {
                packet = PacketMaker.U_TO_C_UPGRADE_RESEARCH(
                    lab_info.reserach_info_dict,
                    player_info.job_info
                );
                user.SendToClient(packet);
            }
            catch (Exception e)
            {
                LogManager.WriteErrorLog(e);
            }
            finally
            {
                if (packet != null)
                {
                    Packet.Destroy(packet);
                }
            }

            Packet? lab_info_packet = null;
            try
            {
                lab_info_packet = PacketMaker.U_TO_C_LAB_INFO(new(), lab_info);
                user.PublishToClients(lab_info_packet, lab_info.member_dict.Keys.ToList());

                await InventoryController.GetCurrentItemList(user);
            }
            catch (Exception e)
            {
                LogManager.WriteErrorLog(e);
            }
            finally
            {
                if (lab_info_packet != null)
                {
                    Packet.Destroy(lab_info_packet);
                }
            }
        }
    }
}
