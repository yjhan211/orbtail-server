namespace user_server
{
    using game_server;
    using MessagePack;
    using network;

    public static class LabController
    {
        public static async Task CreateLab(GameUser user, C_TO_U_CREATE_LAB body)
        {
            PlayerInfo? player_info;
            LabInfo? lab_info;
            using (await PlayerInfoController.Lock(user.redlock, user.player_id))
            {
                player_info = await PlayerInfoController.Load(user.cache_helper, user.player_id);
                if (player_info == null)
                {
                    throw new Exception("player_info not exists");
                }

                if (player_info.job_info.job_grade < JobGrade.RESEARCHER)
                {
                    throw new Exception("not enough job grade");
                }

                if (player_info.lab_id != 0)
                {
                    throw new Exception("Already joined lab");
                }

                // TODO RDB PK로 교체 예정
                long lab_id = await user.cache_helper.StringIncrement("lab_id");
                lab_info = new(
                    lab_id,
                    player_info.player_id,
                    player_info.name,
                    player_info.job_info.job_type,
                    body.lab_name
                );
                await LabInfoController.Save(user.cache_helper, lab_info);

                player_info.lab_id = lab_id;
                player_info.lab_name = body.lab_name;
                await PlayerInfoController.Save(user.cache_helper, player_info);
            }

            Packet packet = PacketMaker.U_TO_C_CREATE_LAB(user.player_id, player_info, lab_info);
            user.SendToClient(packet);
        }

        public static async Task Make(GameUser user, C_TO_U_MAKE body)
        {
            var makeable_item_id = 0;
            PlayerInfo? player_info;
            using (await PlayerInfoController.Lock(user.redlock, user.player_id))
            {
                player_info = await PlayerInfoController.Load(user.cache_helper, user.player_id);
                if (player_info == null)
                {
                    throw new Exception("player_info not exists");
                }

                List<(int, int)> validator = new();
                foreach (var material_slot in body.materials)
                {
                    ItemInfo? slot_item_info = null;
                    foreach (var user_item_info in player_info.inventory_info.item_list)
                    {
                        if (material_slot.Key == user_item_info.item_uid)
                        {
                            slot_item_info = user_item_info;
                            break;
                        }
                    }

                    if (slot_item_info == null)
                    {
                        continue;
                    }

                    validator.Add((slot_item_info.item_id, material_slot.Value));
                }

                var research_list = await GetUseableResearchList(user, player_info);
                makeable_item_id = GameDesignData.GetMakableItemId(research_list, validator);
                if (makeable_item_id != 0)
                {
                    ItemInfo item_info = await InventoryController.CreateItem(
                        user,
                        makeable_item_id,
                        1
                    );

                    player_info = InventoryController.AddItem(player_info, item_info);
                }

                foreach (var material_info in body.materials)
                {
                    var material_item_uid = material_info.Key;
                    var material_item_count = material_info.Value;

                    int remove_index = -1;
                    for (int i = 0; i < player_info.inventory_info.item_list.Count; i++)
                    {
                        if (player_info.inventory_info.item_list[i].item_uid == material_item_uid)
                        {
                            player_info.inventory_info.item_list[i].count -= material_item_count;
                            if (player_info.inventory_info.item_list[i].count == 0)
                            {
                                remove_index = i;
                            }
                        }
                    }
                    if (0 < remove_index)
                    {
                        player_info.inventory_info.item_list.RemoveAt(remove_index);
                    }
                }

                await PlayerInfoController.Save(user.cache_helper, player_info);
            }

            Packet packet = PacketMaker.U_TO_C_MAKE(makeable_item_id != 0);
            user.SendToClient(packet);

            await InventoryController.GetCurrentItemList(user);
        }

        public static async Task<List<int>> GetUseableResearchList(
            GameUser user,
            PlayerInfo player_info
        )
        {
            var result = new List<int>();
            var lab_info = await LabInfoController.Load(user.cache_helper, player_info.lab_id);
            if (lab_info == null)
            {
                return result;
            }

            foreach (var lab_skill in lab_info.reserach_info_dict)
            {
                switch (player_info.job_info.job_type)
                {
                    case JobType.GEOIOGIST:
                        if (0 < lab_skill.Value.geo_level)
                        {
                            result.Add(lab_skill.Value.research_id);
                        }
                        break;

                    case JobType.BOTANIST:
                        if (0 < lab_skill.Value.botan_level)
                        {
                            result.Add(lab_skill.Value.research_id);
                        }
                        break;

                    case JobType.BIOLOGY:
                        if (0 < lab_skill.Value.bio_level)
                        {
                            result.Add(lab_skill.Value.research_id);
                        }
                        break;
                }
            }

            return result;
        }

        public static async Task UpgradeResearch(GameUser user, C_TO_U_UPGRADE_RESEARCH body)
        {
            // 여기서부터
            PlayerInfo? player_info;
            LabInfo? lab_info;
            using (await PlayerInfoController.Lock(user.redlock, user.player_id))
            {
                player_info = await PlayerInfoController.Load(user.cache_helper, user.player_id);
                if (player_info == null)
                {
                    throw new Exception("player_info not exists");
                }

                if (player_info.lab_id == 0)
                {
                    throw new Exception("not joined lab");
                }

                lab_info = await LabInfoController.Load(user.cache_helper, player_info.lab_id);
                if (lab_info == null)
                {
                    throw new Exception("lab info load fail.");
                }

                ResearchInfo? research_info;

                // 신규 생성 연구면 자격 요건 확인
                if (!lab_info.reserach_info_dict.TryGetValue(body.research_id, out research_info))
                {
                    var require_research_list = GameDesignData.GetRequireResearch(body.research_id);
                    if (require_research_list.Count == 0)
                    {
                        throw new Exception("not found require research");
                    }

                    foreach (var require_research in require_research_list)
                    {
                        var require_id = require_research.Item1;
                        var require_level_type = require_research.Item2;
                        var require_level = require_research.Item3;

                        if (!lab_info.reserach_info_dict.TryGetValue(require_id, out var find))
                        {
                            throw new Exception("require research not found");
                        }

                        switch (require_level_type)
                        {
                            case 1:
                                if (require_level < find.geo_level)
                                {
                                    throw new Exception("not enough research level");
                                }
                                break;
                            case 2:
                                if (require_level < find.botan_level)
                                {
                                    throw new Exception("not enough research level");
                                }
                                break;
                            case 3:
                                if (require_level < find.bio_level)
                                {
                                    throw new Exception("not enough research level");
                                }
                                break;
                        }
                    }

                    research_info = new();
                    research_info.research_id = body.research_id;
                }
                else // 기존 연구 업그레이드면 비용 확인 후 차감
                {
                    var job_type = player_info.job_info.job_type;
                    var research_charge_list = GameDesignData.GetResearchUpgradeCharge(
                        job_type,
                        research_info
                    );

                    if (research_charge_list == null)
                    {
                        throw new Exception("not found research charge");
                    }

                    int check = 0;
                    foreach (var item in player_info.inventory_info.item_list)
                    {
                        foreach (var charge_info in research_charge_list)
                        {
                            var charge_item_id = charge_info.Item1;
                            var charge_item_count = charge_info.Item2;

                            if (charge_item_id == item.item_id && charge_item_count <= item.count)
                            {
                                check++;
                            }
                        }
                    }

                    if (check < research_charge_list.Count)
                    {
                        throw new Exception("not enough items");
                    }

                    foreach (var charge_info in research_charge_list)
                    {
                        var charge_item_id = charge_info.Item1;
                        var charge_item_count = charge_info.Item2;

                        int remove_index = -1;
                        for (int i = 0; i < player_info.inventory_info.item_list.Count; i++)
                        {
                            if (player_info.inventory_info.item_list[i].item_id == charge_item_id)
                            {
                                player_info.inventory_info.item_list[i].count -= charge_item_count;
                                if (player_info.inventory_info.item_list[i].count == 0)
                                {
                                    remove_index = i;
                                }
                            }
                        }
                        if (0 < remove_index)
                        {
                            player_info.inventory_info.item_list.RemoveAt(remove_index);
                        }
                    }

                    switch (job_type)
                    {
                        case JobType.GEOIOGIST:
                            research_info.geo_level += 1;
                            break;
                        case JobType.BOTANIST:
                            research_info.botan_level += 1;
                            break;
                        case JobType.BIOLOGY:
                            research_info.bio_level += 1;
                            break;
                    }
                }

                lab_info.reserach_info_dict[research_info.research_id] = research_info;
                await LabInfoController.Save(user.cache_helper, lab_info);
                await PlayerInfoController.Save(user.cache_helper, player_info);
            }

            Packet packet = PacketMaker.U_TO_C_UPGRADE_RESEARCH(lab_info.reserach_info_dict);
            user.SendToClient(packet);

            await InventoryController.GetCurrentItemList(user);
        }
    }
}
