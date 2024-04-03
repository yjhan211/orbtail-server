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
                        job_info.hp = 5; // TODO 삭제

                        var gift_geo = await InventoryController.CreateItem(user, 1002000001, 1);
                        gift_item_list.Add(gift_geo);
                        break;

                    default:
                        break;
                }

                var gift_food = await InventoryController.CreateItem(user, 2001000001, 1);
                gift_item_list.Add(gift_food);

                inventory_info = await InventoryController.AddItem(
                    user,
                    user.player_id,
                    gift_item_list
                );

                await JobInfoController.Save(user.cache_helper, job_info);
            }

            user.SendToClient(
                PacketMaker.U_TO_C_GET_JOB(user.player_id, ErrorCode.SUCCESS, job_info)
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

                var use_skill_id = 0;
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

                    var player_current_cell = player_info.object_info.current_cell;
                    var job_resource_current_cell = job_resource_info.object_info.current_cell;
                    if (1 < MapHelper.GetDistance(player_current_cell, job_resource_current_cell))
                    {
                        Packet error_packet = PacketMaker.U_TO_C_USE_SKILL(ErrorCode.FATAL);
                        user.SendToClient(error_packet);
                        return;
                    }

                    direction = CalcSkillDirection(player_current_cell, job_resource_current_cell);

                    var skill_list = GetSkillList(player_info);
                    var job_resource_detail = GameDesignData.GetJobResourceDetail(
                        job_resource_info.resource_id
                    );

                    var job_resource_name = job_resource_detail.Item1;
                    var job_resource_maxHp = job_resource_detail.Item2;
                    var job_resource_skill_type = job_resource_detail.Item3;

                    switch (job_resource_info.resource_id)
                    {
                        case 10001: // 무른 암석
                            foreach (var skill_id in skill_list)
                            {
                                int skill_type = (skill_id / 10000) * 10000;
                                if (skill_type == job_resource_skill_type)
                                {
                                    use_skill_id = skill_id;
                                    break;
                                }
                            }
                            break;
                    }

                    if (use_skill_id == 0)
                    {
                        Packet error_packet = PacketMaker.U_TO_C_USE_SKILL(ErrorCode.FATAL);
                        user.SendToClient(error_packet);
                        return;
                    }

                    job_resource_info.player_id = user.player_id;
                    job_resource_info.end_timestamp = DateTime.UtcNow.AddSeconds(10);

                    user.current_job_resource = job_resource_info;
                    await JobResourceController.Save(user.cache_helper, job_resource_info);
                }

                user.in_action = true;

                var skill_detail = GameDesignData.GetSkillDetail(use_skill_id);
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

        public static async Task JobSkillEnd(GameUser user)
        {
            if (DateTime.UtcNow <= user.current_job_resource!.end_timestamp)
            {
                return;
            }

            var job_resource = user.current_job_resource;
            user.current_job_resource = null;

            using (await PlayerInfoController.Lock(user.redlock, user.player_id))
            {
                var player_info = await PlayerInfoController.Load(
                    user.cache_helper,
                    user.player_id
                );
                if (player_info == null)
                {
                    return;
                }

                // 아이템 주고
                var item_info = await InventoryController.CreateItem(user, 2001000001, 1);
                player_info.inventory_info.item_list.Add(item_info);

                // 경험치 올리고
                player_info.job_info.exp += 1;

                // 스테이트 초기화
                player_info.state = PlayerState.NONE;

                // 저장
                await PlayerInfoController.Save(user.cache_helper, player_info);

                // 자원 지우고
                await JobResourceController.Delete(user.cache_helper, job_resource.resource_uid);

                // 완료 패킷 전송
                Packet packet = PacketMaker.U_TO_C_USE_SKILL_COMPLETE(
                    item_info,
                    player_info.job_info
                );

                user.SendToClient(packet);
                user.BroadcastUpdatePlayerInfo(player_info);
                await InventoryController.GetCurrentItemList(user);
            }

            // 액션 풀고
            user.in_action = false;

            // 자원 지우라고 게임서버에 전송
            var position_key = MapHelper.GetPositionKey(
                job_resource.object_info.map_id,
                job_resource.object_info.current_cell
            );

            var manage_server = MapHelper.GetServerIdByPositionKey(
                Program.game_server_num,
                position_key
            );

            user.nats_client.Publish(
                MapHelper.GetDestroyObjectSubject(job_resource.object_info.map_id, manage_server),
                MessagePackSerializer.Serialize(
                    (position_key, job_resource.object_info.GetHashField())
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
