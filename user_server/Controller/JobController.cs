namespace user_server
{
    using game_server;
    using MessagePack;
    using network;

    public static class JobController
    {
        public static async Task GetJob(GameUser user, C_TO_U_GET_JOB body)
        {
            var job_info = await JobInfoController.Load(user.cache_helper, user.player_id);
            if (job_info == null)
            {
                throw new Exception("player_info not exists");
            }

            if (job_info!.job_type != JobType.NONE)
            {
                user.SendToClient(
                    PacketMaker.U_TO_C_GET_JOB(user.player_id, ErrorCode.ALREADY_HAS_JOB)
                );
                return;
            }

            InventoryInfo inventory_info;
            using (await PlayerInfoController.Lock(user.redlock, user.player_id))
            {
                List<ItemInfo> gift_item_list = new();

                switch (body.job_type)
                {
                    case JobType.GEOIOGIST:
                        job_info.job_type = JobType.GEOIOGIST;
                        job_info.job_grade = JobGrade.TRAINEE;

                        var gift_geo = await InventoryController.CreateItem(user, 102000001, 1);
                        gift_item_list.Add(gift_geo);
                        break;

                    case JobType.BOTANIST:
                        job_info.job_type = JobType.BOTANIST;
                        job_info.job_grade = JobGrade.TRAINEE;

                        var gift_botan = await InventoryController.CreateItem(user, 102000002, 1);
                        gift_item_list.Add(gift_botan);
                        break;

                    default:
                        break;
                }

                var gift_food = await InventoryController.CreateItem(user, 201000001, 1);
                gift_item_list.Add(gift_food);

                inventory_info = await InventoryController.AddItem(
                    user,
                    user.player_id,
                    gift_item_list
                );

                // job_info.exp = 100;
                await JobInfoController.Save(user.cache_helper, job_info);
            }

            user.SendToClient(
                PacketMaker.U_TO_C_GET_JOB(user.player_id, ErrorCode.SUCCESS, job_info)
            );

            await InventoryController.GetCurrentItemList(user);
        }

        public static async Task UpgradeJob(GameUser user)
        {
            var job_info = await JobInfoController.Load(user.cache_helper, user.player_id);
            if (job_info == null)
            {
                throw new Exception("player_info not exists");
            }

            if (job_info!.job_type == JobType.NONE)
            {
                user.SendToClient(PacketMaker.U_TO_C_UPGRADE_JOB(user.player_id, ErrorCode.FATAL));
                return;
            }

            var max_exp = GameDesignData.GetMaxExp(job_info.job_grade);
            if (job_info.exp < max_exp)
            {
                user.SendToClient(PacketMaker.U_TO_C_UPGRADE_JOB(user.player_id, ErrorCode.FATAL));
                return;
            }

            if (JobGrade.CHIEF <= job_info.job_grade)
            {
                user.SendToClient(PacketMaker.U_TO_C_UPGRADE_JOB(user.player_id, ErrorCode.FATAL));
                return;
            }

            JobGrade target_grade = job_info.job_grade + 1;
            InventoryInfo inventory_info;
            using (await PlayerInfoController.Lock(user.redlock, user.player_id))
            {
                List<ItemInfo> gift_item_list = new();

                switch (target_grade)
                {
                    case JobGrade.RESEARCHER:
                        job_info.job_grade = JobGrade.RESEARCHER;
                        job_info.exp = 0;

                        var gift_geo = await InventoryController.CreateItem(user, 103000001, 1);
                        gift_item_list.Add(gift_geo);
                        break;

                    default:
                        break;
                }

                inventory_info = await InventoryController.AddItem(
                    user,
                    user.player_id,
                    gift_item_list
                );

                await JobInfoController.Save(user.cache_helper, job_info);
            }

            user.SendToClient(
                PacketMaker.U_TO_C_UPGRADE_JOB(user.player_id, ErrorCode.SUCCESS, job_info)
            );

            await InventoryController.GetCurrentItemList(user);
        }

        public static List<int> GetSkillList(PlayerInfo player_info)
        {
            var result = new List<int>();
            foreach (var wear_item in player_info.wear_items)
            {
                var skill_id = GameDesignData.GetSkill(wear_item);
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

                    if (job_resource_info.player_id != 0)
                    {
                        Packet error_packet = PacketMaker.U_TO_C_USE_SKILL(
                            ErrorCode.ALREADY_ANOTHER_USE_SKILL
                        );
                        user.SendToClient(error_packet);
                        return;
                    }

                    Cell player_current_cell;
                    if (player_info.object_info.GetMoveElapsedTime() < Config.MOVE_ELAPSED_TIME)
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

                    var job_resource_detail = GameDesignData.GetJobResourceDetail(
                        job_resource_info.resource_id
                    );

                    var job_resource_name = job_resource_detail.Item1;
                    var job_resource_maxHp = job_resource_detail.Item2;
                    var job_resource_type = job_resource_detail.Item3;
                    var job_resource_level = job_resource_detail.Item4;

                    if (job_resource_type != player_info.job_info.job_type)
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
                    job_resource_info.end_timestamp = DateTime.UtcNow.AddSeconds(10);

                    user.current_progress_job = (body.skill_id, job_resource_info);
                    await JobResourceController.Save(user.cache_helper, job_resource_info);
                }

                user.in_action = true;
                player_info.state = skill_detail.Item3;
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
                    return;
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
                        // TODO 이거 진짜 이상한데 일단 나중에 고치자
                        player_info = InventoryController.AddItem(player_info, item_info);
                        // 자원 지우고
                        await JobResourceController.Delete(
                            user.cache_helper,
                            job_resource_info.resource_uid
                        );
                        BroadcastJobResourceDestroy(user, job_resource_info);
                        break;

                    case 100001:
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
                var add_exp = player_info.job_info.job_grade == JobGrade.TRAINEE ? 10 : 1;

                // 경험치 올리고
                player_info.job_info.exp += add_exp;

                // 스테이트 초기화
                player_info.state = PlayerState.NONE;

                // 저장
                await PlayerInfoController.Save(user.cache_helper, player_info);

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

            // 액션 풀기
            user.in_action = false;
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
