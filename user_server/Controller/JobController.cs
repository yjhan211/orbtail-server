namespace user_server
{
    using game_server;
    using MessagePack;
    using network;

    public static class JobController
    {
        public static async Task UpgradeJob(GameUser user, C_TO_U_UPGRADE_JOB body)
        {
            var player_info = await PlayerInfoController.Load(user.cache_helper, user.player_id);
            if (player_info == null)
            {
                throw new Exception("player_info is not exists");
            }

            if (!player_info.job_info.job_stat_dict.TryGetValue(body.job_type, out var job_stat))
            {
                user.SendToClient(PacketMaker.U_TO_C_UPGRADE_JOB(user.player_id, ErrorCode.FATAL));
                return;
            }

            var max_exp = GameDesignData.GetMaxExp(job_stat.job_grade);
            if (job_stat.exp < max_exp)
            {
                user.SendToClient(PacketMaker.U_TO_C_UPGRADE_JOB(user.player_id, ErrorCode.FATAL));
                return;
            }

            if (JobGrade.CHIEF <= job_stat.job_grade)
            {
                user.SendToClient(PacketMaker.U_TO_C_UPGRADE_JOB(user.player_id, ErrorCode.FATAL));
                return;
            }

            JobGrade target_grade = job_stat.job_grade + 1;
            using (await PlayerInfoController.Lock(user.redlock, user.player_id))
            {
                List<ItemInfo> gift_item_list = new();

                switch (target_grade)
                {
                    case JobGrade.RESEARCHER:
                        job_stat.job_grade = JobGrade.RESEARCHER;
                        job_stat.exp = 0;

                        // 연구원의 제복
                        var gift_geo = await InventoryController.CreateItem(user, 103000002, 1);
                        gift_item_list.Add(gift_geo);
                        break;

                    default:
                        break;
                }

                player_info.inventory_info.AddItem(gift_item_list);
                await PlayerInfoController.Save(user.cache_helper, player_info);
            }

            user.SendToClient(
                PacketMaker.U_TO_C_UPGRADE_JOB(
                    user.player_id,
                    ErrorCode.SUCCESS,
                    player_info.job_info
                )
            );

            await InventoryController.GetCurrentItemList(user);
        }

        public static List<int> GetSkillList(PlayerInfo player_info)
        {
            var result = new List<int>();
            foreach (var wear_item in player_info.wear_items)
            {
                var (_, skill_id) = GameDesignData.GetSkill(wear_item);
                if (skill_id == 0)
                {
                    continue;
                }

                result.Add(skill_id);
            }

            return result;
        }

        public static async Task UseJobSkill(GameUser user, C_TO_U_USE_SKILL body)
        {
            PlayerInfo? player_info;
            JobResourceInfo? job_resource_info;
            var direction = DirectionType.NONE;

            using (await PlayerInfoController.Lock(user.redlock, user.player_id))
            {
                player_info = await PlayerInfoController.Load(user.cache_helper, user.player_id);
                if (player_info == null)
                {
                    Packet error_packet = PacketMaker.U_TO_C_USE_SKILL(ErrorCode.FATAL);
                    user.SendToClient(error_packet);
                    return;
                }
                if (player_info.job_info == null)
                {
                    Packet error_packet = PacketMaker.U_TO_C_USE_SKILL(ErrorCode.FATAL);
                    user.SendToClient(error_packet);
                    return;
                }
                if (player_info.job_info.hp <= 0)
                {
                    Packet error_packet = PacketMaker.U_TO_C_USE_SKILL(ErrorCode.FATAL);
                    user.SendToClient(error_packet);
                    return;
                }

                var skill_detail = GameDesignData.GetSkillDetail(body.skill_id);

                using (await JobResourceController.Lock(user.redlock, body.resource_uid))
                {
                    job_resource_info = await JobResourceController.Load(
                        user.cache_helper,
                        body.resource_uid
                    );

                    if (job_resource_info == null)
                    {
                        Packet error_packet = PacketMaker.U_TO_C_USE_SKILL(ErrorCode.FATAL);
                        user.SendToClient(error_packet);
                        return;
                    }

                    JobType skill_type = skill_detail.Item3;

                    var job_resource_detail = GameDesignData.GetJobResourceDetail(
                        job_resource_info.resource_id
                    );
                    var job_resource_name = job_resource_detail.Item1;
                    var job_resource_maxHp = job_resource_detail.Item2;
                    var job_resource_type = job_resource_detail.Item3;
                    var job_resource_level = job_resource_detail.Item4;

                    if (skill_type != JobType.NONE && skill_type != job_resource_type)
                    {
                        Packet error_packet = PacketMaker.U_TO_C_USE_SKILL(ErrorCode.FATAL);
                        user.SendToClient(error_packet);
                        return;
                    }

                    if (job_resource_info.player_id != 0)
                    {
                        Packet error_packet = PacketMaker.U_TO_C_USE_SKILL(
                            ErrorCode.ALREADY_ANOTHER_USE_SKILL
                        );
                        user.SendToClient(error_packet);
                        return;
                    }

                    Cell player_current_cell;
                    if (user.object_controller!.GetMoveElapsedTime() < Config.MOVE_ELAPSED_TIME)
                    {
                        player_current_cell = player_info.object_info.current_cell;
                    }
                    else
                    {
                        player_current_cell = player_info.object_info.target_cell;
                    }

                    var job_resource_current_cell = job_resource_info.object_info.current_cell;
                    if (1 < MapHelper.GetDistance(player_current_cell, job_resource_current_cell))
                    {
                        Packet error_packet = PacketMaker.U_TO_C_USE_SKILL(ErrorCode.FATAL);
                        user.SendToClient(error_packet);
                        return;
                    }

                    direction = CalcSkillDirection(player_current_cell, job_resource_current_cell);

                    var skill_list = GetSkillList(player_info);
                    if (!skill_list.Contains(body.skill_id))
                    {
                        Packet error_packet = PacketMaker.U_TO_C_USE_SKILL(ErrorCode.FATAL);
                        user.SendToClient(error_packet);
                        return;
                    }

                    if (skill_detail.Item2 < job_resource_level)
                    {
                        Packet error_packet = PacketMaker.U_TO_C_USE_SKILL(ErrorCode.FATAL);
                        user.SendToClient(error_packet);
                        return;
                    }

                    job_resource_info.player_id = user.player_id;
                    job_resource_info.end_timestamp = DateTime.UtcNow.AddSeconds(10); // TODO 임시 하드코딩

                    user.current_progress_job = (body.skill_id, job_resource_info);
                    await JobResourceController.Save(user.cache_helper, job_resource_info);
                }

                user.in_action = true;
                player_info.state = skill_detail.Item4;
                player_info.job_info.hp -= 1;

                await PlayerInfoController.Save(user.cache_helper, player_info);
            }

            await user.object_controller!.SetFlip(direction);

            Packet packet = PacketMaker.U_TO_C_USE_SKILL(ErrorCode.SUCCESS, player_info.job_info);

            user.SendToClient(packet);
            user.BroadcastUpdatePlayerInfo(player_info);
            user.BroadcastUpdateJobResourceInfo(job_resource_info);
        }

        public static async Task JobSkillEnd(
            GameUser user,
            int skill_id,
            JobResourceInfo job_resource_info
        )
        {
            if (DateTime.UtcNow <= job_resource_info.end_timestamp)
            {
                return;
            }

            var job_resource_detail = GameDesignData.GetJobResourceDetail(
                job_resource_info.resource_id
            );

            Random random = new();
            using (await PlayerInfoController.Lock(user.redlock, user.player_id))
            {
                user.current_progress_job = null;

                var player_info = await PlayerInfoController.Load(
                    user.cache_helper,
                    user.player_id
                );

                if (player_info == null)
                {
                    user.in_action = false;
                    throw new Exception("cannot find player info");
                }

                ItemInfo? item_info = null;
                bool is_success = true;

                switch (skill_id)
                {
                    case 10001:
                    case 20001:
                    case 10002:
                    case 20002:
                        // 아이템 뽑기
                        var reward_item = job_resource_detail.Item6[
                            random.Next(0, job_resource_detail.Item6.Count)
                        ];
                        // 아이템 주기
                        item_info = await InventoryController.CreateItem(user, reward_item, 1);
                        player_info.inventory_info.AddItem(item_info);

                        // 자원 지우고
                        await JobResourceController.Delete(
                            user.cache_helper,
                            job_resource_info.resource_uid
                        );
                        BroadcastJobResourceDestroy(user, job_resource_info);
                        break;

                    case 100001:
                    case 100002:
                        is_success = random.Next(0, 100) < 50;
                        if (is_success)
                        {
                            var upgrade_pool = GameDesignData.GetJobResourceUpgradePool(
                                job_resource_info.resource_id
                            );

                            job_resource_info.resource_id = upgrade_pool[
                                random.Next(0, upgrade_pool.Count)
                            ];
                        }
                        job_resource_info.player_id = 0;
                        await JobResourceController.Save(user.cache_helper, job_resource_info);
                        user.BroadcastUpdateJobResourceInfo(job_resource_info);
                        break;
                }

                // TODO 경험치량 조정
                var job_type = job_resource_detail.Item3;
                if (!player_info.job_info.job_stat_dict.TryGetValue(job_type, out var job_stat))
                {
                    user.in_action = false;
                    throw new Exception($"cannot found job stat : {job_type}");
                }

                var add_exp = job_stat.job_grade == JobGrade.TRAINEE ? 10 : 1;

                // 경험치 올리고
                job_stat.exp += add_exp;

                // 스테이트 초기화
                player_info.state = PlayerState.NONE;

                // 저장
                await PlayerInfoController.Save(user.cache_helper, player_info);
                user.in_action = false;

                // 완료 패킷 전송
                Packet packet = PacketMaker.U_TO_C_USE_SKILL_COMPLETE(
                    is_success,
                    item_info,
                    player_info.job_info
                );

                user.SendToClient(packet);
                user.BroadcastUpdatePlayerInfo(player_info);

                if (item_info != null)
                {
                    await InventoryController.GetCurrentItemList(user);
                }
            }
        }

        public static async Task Encamp(GameUser user, C_TO_U_ENCAMP body)
        {
            PlayerInfo? player_info;
            CampInfo? camp_info;
            using (await PlayerInfoController.Lock(user.redlock, user.player_id))
            {
                player_info = await PlayerInfoController.Load(user.cache_helper, user.player_id);
                if (player_info == null)
                {
                    throw new Exception("player_info not exists");
                }

                if (player_info.object_info.map_id == MapID.CAMPUS_1)
                {
                    throw new Exception("invalid map id");
                }

                if (
                    !player_info.inventory_info.item_dict.TryGetValue(
                        body.item_uid,
                        out var target_item
                    )
                )
                {
                    throw new Exception("not found item info");
                }

                if (await CampInfoController.Load(user.cache_helper, user.player_id) != null)
                {
                    throw new Exception("already encamp");
                }

                camp_info = new CampInfo(
                    player_info.player_id,
                    player_info.name,
                    player_info.object_info,
                    target_item,
                    player_info.object_info.target_cell
                )
                {
                    add_hp_timestamp = DateTime.UtcNow.AddSeconds(5)
                };

                player_info.state = PlayerState.CAMIPING_1;
                await CampInfoController.Save(user.cache_helper, camp_info);
                await PlayerInfoController.Save(user.cache_helper, player_info);

                user.current_camp_info = camp_info;
            }

            var current_position_key = MapHelper.GetPositionKey(
                camp_info.object_info.map_id,
                camp_info.object_info.map_sub_id,
                camp_info.object_info.current_cell
            );

            var current_manage_server = MapHelper.GetServerIdByPositionKey(
                Program.game_server_num,
                current_position_key
            );

            var move_manage_subject = MapHelper.GetMoveManageSubject(
                camp_info.object_info.map_id,
                camp_info.object_info.map_sub_id,
                current_manage_server
            );

            // 현재 담당 서버에 전송
            user.nats_client.Publish(
                move_manage_subject,
                MessagePackSerializer.Serialize((current_position_key, camp_info.object_info))
            );

            user.BroadcastUpdatePlayerInfo(player_info);
        }

        public static async Task Decamp(GameUser user)
        {
            PlayerInfo? player_info;
            CampInfo? camp_info;
            using (await PlayerInfoController.Lock(user.redlock, user.player_id))
            {
                player_info = await PlayerInfoController.Load(user.cache_helper, user.player_id);
                if (player_info == null)
                {
                    return;
                }

                camp_info = await CampInfoController.Load(user.cache_helper, user.player_id);
                if (camp_info == null)
                {
                    return;
                }

                player_info.state = PlayerState.NONE;
                await CampInfoController.Delete(user.cache_helper, camp_info.player_id);
                await PlayerInfoController.Save(user.cache_helper, player_info);

                user.current_camp_info = null;
            }

            user.BroadcastUpdatePlayerInfo(player_info);
            BroadCastCampDestroy(user, camp_info);
        }

        public static void BroadcastJobResourceDestroy(
            GameUser user,
            JobResourceInfo job_resource_info
        )
        {
            var position_key = MapHelper.GetPositionKey(
                job_resource_info.object_info.map_id,
                job_resource_info.object_info.map_sub_id,
                job_resource_info.object_info.current_cell
            );

            var manage_server = MapHelper.GetServerIdByPositionKey(
                Program.game_server_num,
                position_key
            );

            user.nats_client.Publish(
                MapHelper.GetDestroyObjectSubject(
                    job_resource_info.object_info.map_id,
                    job_resource_info.object_info.map_sub_id,
                    manage_server
                ),
                MessagePackSerializer.Serialize(
                    (position_key, job_resource_info.object_info.GetHashField())
                )
            );
        }

        public static void BroadCastCampDestroy(GameUser user, CampInfo camp_info)
        {
            var position_key = MapHelper.GetPositionKey(
                camp_info.object_info.map_id,
                camp_info.object_info.map_sub_id,
                camp_info.object_info.current_cell
            );

            var manage_server = MapHelper.GetServerIdByPositionKey(
                Program.game_server_num,
                position_key
            );

            user.nats_client.Publish(
                MapHelper.GetDestroyObjectSubject(
                    camp_info.object_info.map_id,
                    camp_info.object_info.map_sub_id,
                    manage_server
                ),
                MessagePackSerializer.Serialize(
                    (position_key, camp_info.object_info.GetHashField())
                )
            );
        }

        public static DirectionType CalcSkillDirection(Cell fromCell, Cell toCell)
        {
            int deltaX = toCell.x - fromCell.x;
            int deltaY = toCell.y - fromCell.y;

            if (deltaX > 0 && deltaY == 0)
                return DirectionType.TOP_LEFT;
            else if (deltaX < 0 && deltaY == 0)
                return DirectionType.TOP_RIGHT;
            else if (deltaX == 0 && deltaY < 0)
                return DirectionType.BOTTOM_LEFT;
            else if (deltaX == 0 && deltaY > 0)
                return DirectionType.BOTTOM_RIGHT;
            else
                return DirectionType.NONE;
        }
    }
}
