namespace user_server
{
    using System.Diagnostics;
    using MessagePack;
    using network;

    public static class JobController
    {
        public static async Task UpgradeJob(GameUser user, C_TO_U_UPGRADE_JOB body)
        {
            var player_info = await PlayerInfo.Load(user.player_id);
            if (player_info == null)
            {
                throw new Exception("player_info is not exists");
            }

            Packet? packet = null;
            if (!player_info.job_info.job_stat_dict.TryGetValue(body.job_type, out var job_stat))
            {
                try
                {
                    packet = PacketMaker.U_TO_C_UPGRADE_JOB(user.player_id, ErrorCode.FATAL);
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
                return;
            }

            var max_exp = GameDesignData.GetMaxExp(job_stat.job_grade);
            if (job_stat.exp < max_exp)
            {
                try
                {
                    packet = PacketMaker.U_TO_C_UPGRADE_JOB(user.player_id, ErrorCode.FATAL);
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
                return;
            }

            if (JobGrade.CHIEF <= job_stat.job_grade)
            {
                try
                {
                    packet = PacketMaker.U_TO_C_UPGRADE_JOB(user.player_id, ErrorCode.FATAL);
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
                return;
            }

            JobGrade target_grade = job_stat.job_grade + 1;
            using (await PlayerInfo.Lock(user.redlock, user.player_id))
            {
                List<ItemInfo> gift_item_list = new();

                switch (target_grade)
                {
                    case JobGrade.RESEARCHER:
                        job_stat.job_grade = JobGrade.RESEARCHER;
                        job_stat.exp = 0;

                        // 연구원의 제복
                        // var gift_geo = await InventoryController.CreateItem(user, 103000002, 1);
                        // gift_item_list.Add(gift_geo);
                        break;

                    default:
                        break;
                }

                player_info.inventory_info.AddItem(gift_item_list);
                await player_info.Save();
            }

            try
            {
                packet = PacketMaker.U_TO_C_UPGRADE_JOB(
                    user.player_id,
                    ErrorCode.SUCCESS,
                    player_info.job_info
                );
                user.SendToClient(packet);
                await InventoryController.GetCurrentItemList(user);
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

        public static List<int> GetSkillList(PlayerInfo player_info)
        {
            var result = new List<int>();
            foreach (var wear_item in player_info.wear_items)
            {
                var (_, skill_id, _) = GameDesignData.GetSkill(wear_item);
                if (skill_id == 0)
                {
                    continue;
                }

                result.Add(skill_id);
            }

            return result;
        }

        public static async Task Explore(GameUser user, C_TO_U_EXPLORE body)
        {
            PlayerInfo? player_info;
            ExploreTargetInfo? explore_target_info;

            var direction = DirectionType.NONE;

            Packet? packet = null;
            using (await PlayerInfo.Lock(user.redlock, user.player_id))
            {
                player_info = await PlayerInfo.Load(user.player_id);
                if (player_info == null)
                {
                    try
                    {
                        packet = PacketMaker.U_TO_C_EXPLORE(ErrorCode.FATAL);
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
                    return;
                }
                if (player_info.job_info == null)
                {
                    try
                    {
                        packet = PacketMaker.U_TO_C_EXPLORE(ErrorCode.FATAL);
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
                    return;
                }
                if (player_info.job_info.hp <= 0)
                {
                    try
                    {
                        packet = PacketMaker.U_TO_C_EXPLORE(ErrorCode.FATAL);
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
                    return;
                }

                using (await ExploreTargetInfo.Lock(user.redlock, body.explore_target_uid))
                {
                    explore_target_info = await ExploreTargetInfo.Load(body.explore_target_uid);
                    if (explore_target_info == null)
                    {
                        try
                        {
                            packet = PacketMaker.U_TO_C_EXPLORE(ErrorCode.FATAL);
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
                        return;
                    }

                    if (explore_target_info.player_id != 0)
                    {
                        try
                        {
                            packet = PacketMaker.U_TO_C_EXPLORE(
                                ErrorCode.ALREADY_ANOTHER_USE_SKILL
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
                        return;
                    }

                    var explore_target_detail = GameDesignData.GetExploreTargetDetail(
                        explore_target_info.explore_target_id
                    );

                    var job_type = explore_target_detail.Item3;
                    var require_level = explore_target_detail.Item4;

                    var (is_valid_job_type, is_explore_able, is_enough_level) =
                        player_info.wear_items.Aggregate(
                            (false, false, false),
                            (acc, wear_item) =>
                            {
                                var (skill_type, skill_id, skill_level) = GameDesignData.GetSkill(
                                    wear_item
                                );
                                return (
                                    acc.Item1 || skill_type == job_type,
                                    acc.Item2 || skill_id == 100001 || skill_id == 100002,
                                    acc.Item3 || require_level <= skill_level
                                );
                            }
                        );

                    if (!is_valid_job_type || !is_explore_able || !is_enough_level)
                    {
                        try
                        {
                            packet = PacketMaker.U_TO_C_EXPLORE(ErrorCode.FATAL);
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
                        return;
                    }

                    var move_elapsed_time = await user.object_controller!.CalcMoveElapsedTime();
                    Cell player_current_cell;
                    if (user.object_controller!.GetMoveElapsedTime() < move_elapsed_time)
                    {
                        player_current_cell = player_info.object_info.current_cell;
                    }
                    else
                    {
                        player_current_cell = player_info.object_info.target_cell;
                    }

                    var explore_target_current_cell = explore_target_info.object_info.current_cell;
                    if (1 < MapHelper.GetDistance(player_current_cell, explore_target_current_cell))
                    {
                        try
                        {
                            packet = PacketMaker.U_TO_C_EXPLORE(ErrorCode.FATAL);
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
                        return;
                    }

                    direction = CalcSkillDirection(
                        player_current_cell,
                        explore_target_current_cell
                    );

                    explore_target_info.player_id = user.player_id;
                    explore_target_info.end_timestamp = DateTime.UtcNow.AddSeconds(10); // TODO 임시 하드코딩

                    user.current_progress_explore = explore_target_info;
                    await explore_target_info.Save();
                }

                user.in_action = true;
                player_info.state = PlayerState.EXPLORE_1;
                player_info.job_info.hp -= 1;

                await player_info.Save();
            }

            await user.object_controller!.SetFlip(direction);

            try
            {
                packet = PacketMaker.U_TO_C_EXPLORE(ErrorCode.SUCCESS, player_info.job_info);
                user.SendToClient(packet);
                user.BroadcastUpdatePlayerInfo(player_info);
                user.BroadcastUpdateExploreTargetInfo(explore_target_info);
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

        public static async Task UseJobSkill(GameUser user, C_TO_U_USE_SKILL body)
        {
            PlayerInfo? player_info;
            JobResourceInfo? job_resource_info;
            var direction = DirectionType.NONE;

            Packet? packet = null;
            using (await PlayerInfo.Lock(user.redlock, user.player_id))
            {
                player_info = await PlayerInfo.Load(user.player_id);
                if (player_info == null)
                {
                    try
                    {
                        packet = PacketMaker.U_TO_C_USE_SKILL(ErrorCode.FATAL);
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
                    return;
                }
                if (player_info.job_info == null)
                {
                    try
                    {
                        packet = PacketMaker.U_TO_C_USE_SKILL(ErrorCode.FATAL);
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
                    return;
                }
                if (player_info.job_info.hp <= 0)
                {
                    try
                    {
                        packet = PacketMaker.U_TO_C_USE_SKILL(ErrorCode.FATAL);
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
                    return;
                }

                var skill_detail = GameDesignData.GetSkillDetail(body.skill_id);
                using (await JobResourceInfo.Lock(user.redlock, body.resource_uid))
                {
                    job_resource_info = await JobResourceInfo.Load(body.resource_uid);
                    if (job_resource_info == null)
                    {
                        try
                        {
                            packet = PacketMaker.U_TO_C_USE_SKILL(ErrorCode.FATAL);
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
                        try
                        {
                            packet = PacketMaker.U_TO_C_USE_SKILL(ErrorCode.FATAL);
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
                        return;
                    }

                    if (job_resource_info.player_id != 0)
                    {
                        try
                        {
                            packet = PacketMaker.U_TO_C_USE_SKILL(
                                ErrorCode.ALREADY_ANOTHER_USE_SKILL
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
                        return;
                    }

                    var move_elapsed_time = await user.object_controller!.CalcMoveElapsedTime();
                    if (player_info.object_info.map_id == MapID.WETLAND_1)
                    {
                        move_elapsed_time *= 2;
                    }

                    Cell player_current_cell;
                    if (user.object_controller!.GetMoveElapsedTime() < move_elapsed_time)
                    {
                        player_current_cell = player_info.object_info.current_cell;
                    }
                    else
                    {
                        player_current_cell = player_info.object_info.target_cell;
                    }

                    var job_resource_current_cell = job_resource_info.object_info.current_cell;
                    // if (1 < MapHelper.GetDistance(player_current_cell, job_resource_current_cell))
                    // {
                    //     try
                    //     {
                    //         packet = PacketMaker.U_TO_C_USE_SKILL(ErrorCode.FATAL);
                    //         user.SendToClient(packet);
                    //     }
                    //     catch (Exception e)
                    //     {
                    //         LogManager.WriteErrorLog(e);
                    //     }
                    //     finally
                    //     {
                    //         if (packet != null)
                    //         {
                    //             Packet.Destroy(packet);
                    //         }
                    //     }
                    //     return;
                    // }

                    direction = CalcSkillDirection(player_current_cell, job_resource_current_cell);

                    var skill_list = GetSkillList(player_info);
                    if (!skill_list.Contains(body.skill_id))
                    {
                        try
                        {
                            packet = PacketMaker.U_TO_C_USE_SKILL(ErrorCode.FATAL);
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
                        return;
                    }

                    if (skill_detail.Item2 < job_resource_level)
                    {
                        try
                        {
                            packet = PacketMaker.U_TO_C_USE_SKILL(ErrorCode.FATAL);
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
                        return;
                    }

                    job_resource_info.player_id = user.player_id;
                    job_resource_info.end_timestamp = DateTime.UtcNow.AddSeconds(10); // TODO 임시 하드코딩

                    user.current_progress_job = (body.skill_id, job_resource_info);
                    await job_resource_info.Save();
                }

                user.in_action = true;
                player_info.state = skill_detail.Item4;
                player_info.job_info.hp -= 1;

                await player_info.Save();
            }

            await user.object_controller!.SetFlip(direction);

            try
            {
                packet = PacketMaker.U_TO_C_USE_SKILL(ErrorCode.SUCCESS, player_info.job_info);
                user.SendToClient(packet);
                user.BroadcastUpdatePlayerInfo(player_info);
                user.BroadcastUpdateJobResourceInfo(job_resource_info);
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

        public static async Task ExploreEnd(GameUser user, ExploreTargetInfo explore_target_info)
        {
            if (DateTime.UtcNow <= explore_target_info.end_timestamp)
            {
                return;
            }

            Random random = new();
            Packet? packet = null;
            using (await PlayerInfo.Lock(user.redlock, user.player_id))
            {
                user.current_progress_explore = null;

                var player_info = await PlayerInfo.Load(user.player_id);
                if (player_info == null)
                {
                    user.in_action = false;
                    throw new Exception("cannot find player info");
                }

                bool is_success = random.Next(0, 100) < 50;
                if (is_success)
                {
                    // 잡리소스 생성
                    var upgrade_pool = GameDesignData.GetExploreResultPool(
                        explore_target_info.explore_target_id
                    );

                    long job_resource_uid = await CacheHelper.Instance.StringIncrementAsync(
                        "temp_job_resource_uid"
                    );

                    JobResourceInfo job_resource_info =
                        new(
                            job_resource_uid,
                            upgrade_pool[random.Next(0, upgrade_pool.Count)],
                            new()
                            {
                                object_type = ObjectType.JOBRESOURCE,
                                object_id = job_resource_uid,
                                current_cell = explore_target_info.object_info.current_cell,
                                target_cell = explore_target_info.object_info.current_cell,
                                map_id = explore_target_info.object_info.map_id,
                                map_sub_id = explore_target_info.object_info.map_sub_id
                            }
                        );

                    await job_resource_info.Save();

                    // 매니지 서버로 전송
                    BroadcastJobResourceCreate(user, job_resource_info);

                    // 조사대상 삭제
                    await explore_target_info.Delete();
                    BroadcastObjectDestroy(user, explore_target_info.object_info);
                }
                else
                {
                    // 조사대상 소유권 해제
                    explore_target_info.player_id = 0;
                    await explore_target_info.Save();

                    user.BroadcastUpdateExploreTargetInfo(explore_target_info);
                }

                var explore_target_detail = GameDesignData.GetExploreTargetDetail(
                    explore_target_info.explore_target_id
                );

                // 경험치 올리고
                var job_type = explore_target_detail.Item3;
                long research_point = player_info.job_info.research_point_dict[job_type].point;
                player_info.job_info.research_point_dict[job_type].point = research_point + 1;

                // 스테이트 초기화
                player_info.state = PlayerState.NONE;

                // 저장
                await player_info.Save();
                user.in_action = false;

                try
                {
                    packet = PacketMaker.U_TO_C_EXPLORE_COMPLETE(is_success, player_info.job_info);
                    user.SendToClient(packet);
                    user.BroadcastUpdatePlayerInfo(player_info);
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
            using (await PlayerInfo.Lock(user.redlock, user.player_id))
            {
                user.current_progress_job = null;

                var player_info = await PlayerInfo.Load(user.player_id);
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
                        await job_resource_info.Delete();
                        BroadcastObjectDestroy(user, job_resource_info.object_info);
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
                await player_info.Save();
                user.in_action = false;

                Packet? packet = null;
                try
                {
                    packet = PacketMaker.U_TO_C_USE_SKILL_COMPLETE(
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
        }

        public static async Task Encamp(GameUser user, C_TO_U_ENCAMP body)
        {
            PlayerInfo? player_info;
            CampInfo? camp_info;
            using (await PlayerInfo.Lock(user.redlock, user.player_id))
            {
                player_info = await PlayerInfo.Load(user.player_id);
                if (player_info == null)
                {
                    throw new Exception("player_info not exists");
                }

                // if (player_info.object_info.map_id == MapID.CAMPUS_1)
                // {
                //     throw new Exception("invalid map id");
                // }

                if (
                    !player_info.inventory_info.item_dict.TryGetValue(
                        body.item_uid,
                        out var target_item
                    )
                )
                {
                    throw new Exception("not found item info");
                }

                if (await CampInfo.Load(user.player_id) != null)
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
                await camp_info.Save();
                await player_info.Save();

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
            using (await PlayerInfo.Lock(user.redlock, user.player_id))
            {
                player_info = await PlayerInfo.Load(user.player_id);
                if (player_info == null)
                {
                    return;
                }

                camp_info = await CampInfo.Load(user.player_id);
                if (camp_info == null)
                {
                    return;
                }

                player_info.state = PlayerState.NONE;
                await camp_info.Delete();
                await player_info.Save();

                user.current_camp_info = null;
            }

            user.BroadcastUpdatePlayerInfo(player_info);
            BroadcastObjectDestroy(user, camp_info.object_info);
        }

        public static async Task AddSellItem(GameUser user, C_TO_U_ADD_SELL_ITEM body)
        {
            PlayerInfo? player_info;
            CampInfo? camp_info;
            ItemInfo? item_info;
            using (await PlayerInfo.Lock(user.redlock, user.player_id))
            {
                player_info = await PlayerInfo.Load(user.player_id);
                if (player_info == null)
                {
                    return;
                }

                camp_info = await CampInfo.Load(user.player_id);
                if (camp_info == null)
                {
                    return;
                }

                if (!player_info.inventory_info.item_dict.TryGetValue(body.item_uid, out item_info))
                {
                    return;
                }

                if (3 <= camp_info.cell_dict.Count)
                {
                    return;
                }

                if (item_info.is_wear || item_info.count <= 0)
                {
                    return;
                }

                camp_info.cell_dict[body.item_uid] = (item_info, body.price);
                await camp_info.Save();
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

            var update_camp_subject = MapHelper.GetUpdateCampSubject(
                camp_info.object_info.map_id,
                camp_info.object_info.map_sub_id,
                current_manage_server
            );

            // 현재 담당 서버에 전송
            user.nats_client.Publish(
                update_camp_subject,
                MessagePackSerializer.Serialize((current_position_key, camp_info))
            );

            Packet? packet = null;
            try
            {
                packet = PacketMaker.U_TO_C_ADD_SELL_ITEM(user.player_id);
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

        public static async Task DeleteSellItem(GameUser user, C_TO_U_DELETE_SELL_ITEM body)
        {
            PlayerInfo? player_info;
            CampInfo? camp_info;
            ItemInfo? item_info;
            using (await PlayerInfo.Lock(user.redlock, user.player_id))
            {
                player_info = await PlayerInfo.Load(user.player_id);
                if (player_info == null)
                {
                    return;
                }

                camp_info = await CampInfo.Load(user.player_id);
                if (camp_info == null)
                {
                    return;
                }

                if (!player_info.inventory_info.item_dict.TryGetValue(body.item_uid, out item_info))
                {
                    return;
                }

                if (item_info.is_wear || item_info.count <= 0)
                {
                    return;
                }

                camp_info.cell_dict.Remove(body.item_uid);
                await camp_info.Save();
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

            var update_camp_subject = MapHelper.GetUpdateCampSubject(
                camp_info.object_info.map_id,
                camp_info.object_info.map_sub_id,
                current_manage_server
            );

            // 현재 담당 서버에 전송
            user.nats_client.Publish(
                update_camp_subject,
                MessagePackSerializer.Serialize((current_position_key, camp_info))
            );

            Packet? packet = null;
            try
            {
                packet = PacketMaker.U_TO_C_DELETE_SELL_ITEM(user.player_id);
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

        public static async Task BuyItem(GameUser user, C_TO_U_BUY_ITEM body)
        {
            PlayerInfo? player_info;
            PlayerInfo? seller_info;
            CampInfo? camp_info;
            LogManager.WriteDebugLog("0");
            using (await PlayerInfo.Lock(user.redlock, user.player_id))
            {
                player_info = await PlayerInfo.Load(user.player_id);
                if (player_info == null)
                {
                    LogManager.WriteDebugLog("1");
                    return;
                }

                camp_info = await CampInfo.Load(body.seller_id);
                if (camp_info == null)
                {
                    LogManager.WriteDebugLog("2");
                    return;
                }

                if (!camp_info.cell_dict.TryGetValue(body.sell_item_uid, out var sell_item_info))
                {
                    LogManager.WriteDebugLog("3");
                    return;
                }

                var price = sell_item_info.Item2;
                if (player_info.gold < price)
                {
                    LogManager.WriteDebugLog("4");
                    return;
                }

                using (await PlayerInfo.Lock(user.redlock, body.seller_id))
                {
                    var item_info = sell_item_info.Item1;
                    if (item_info == null)
                    {
                        return;
                    }

                    seller_info = await PlayerInfo.Load(body.seller_id);
                    if (seller_info == null)
                    {
                        return;
                    }

                    seller_info.gold += price;
                    player_info.gold -= price;

                    camp_info.cell_dict.Remove(body.sell_item_uid);
                    seller_info.inventory_info.item_dict.Remove(body.sell_item_uid);
                    player_info.inventory_info.AddItem(sell_item_info.Item1);

                    await camp_info.Save();
                    await seller_info.Save();
                    await player_info.Save();
                }
            }

            // 텐트 업데이트
            var current_position_key = MapHelper.GetPositionKey(
                camp_info.object_info.map_id,
                camp_info.object_info.map_sub_id,
                camp_info.object_info.current_cell
            );

            var current_manage_server = MapHelper.GetServerIdByPositionKey(
                Program.game_server_num,
                current_position_key
            );

            var update_camp_subject = MapHelper.GetUpdateCampSubject(
                camp_info.object_info.map_id,
                camp_info.object_info.map_sub_id,
                current_manage_server
            );

            // 현재 담당 서버에 전송
            user.nats_client.Publish(
                update_camp_subject,
                MessagePackSerializer.Serialize((current_position_key, camp_info))
            );

            Packet? packet = null;
            try
            {
                await InventoryController.GetCurrentItemList(user);

                packet = PacketMaker.U_TO_C_BUY_ITEM(user.player_id, player_info);
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

            // 셀러 업데이트
            Packet? packet_2 = null;
            try
            {
                packet_2 = PacketMaker.U_TO_U_PLAYER_INFO(seller_info);
                user.nats_client.Publish(
                    seller_info.object_info.GetHashField(),
                    packet_2.ToBytes()
                );
            }
            catch (Exception e)
            {
                LogManager.WriteErrorLog(e);
            }
            finally
            {
                if (packet_2 != null)
                {
                    Packet.Destroy(packet_2);
                }
            }
        }

        public static void BroadcastJobResourceCreate(
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
                MapHelper.GetCreateJobResourceSubject(
                    job_resource_info.object_info.map_id,
                    job_resource_info.object_info.map_sub_id,
                    manage_server
                ),
                MessagePackSerializer.Serialize(
                    (position_key, job_resource_info, job_resource_info.object_info)
                )
            );
        }

        public static void BroadcastObjectDestroy(GameUser user, GameObjectInfo object_info)
        {
            var position_key = MapHelper.GetPositionKey(
                object_info.map_id,
                object_info.map_sub_id,
                object_info.current_cell
            );

            var manage_server = MapHelper.GetServerIdByPositionKey(
                Program.game_server_num,
                position_key
            );

            user.nats_client.Publish(
                MapHelper.GetDestroyObjectSubject(
                    object_info.map_id,
                    object_info.map_sub_id,
                    manage_server
                ),
                MessagePackSerializer.Serialize((position_key, object_info.GetHashField()))
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
