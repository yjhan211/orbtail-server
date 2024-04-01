namespace user_server
{
    using game_server;
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
                        job_info.hp = GameDesignData.GetMaxHP(job_info.job_grade);

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
                PacketMaker.U_TO_C_GET_JOB(
                    user.player_id,
                    ErrorCode.SUCCESS,
                    job_info,
                    inventory_info
                )
            );
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
            using (await PlayerInfoController.Lock(user.redlock, user.player_id))
            {
                player_info = await PlayerInfoController.Load(user.cache_helper, user.player_id);
                if (player_info == null)
                {
                    throw new Exception("player_info not exists");
                }
            }

            using (await JobResourceController.Lock(user.redlock, body.resource_uid))
            {
                var job_resource_info = await JobResourceController.Load(
                    user.cache_helper,
                    body.resource_uid
                );

                if (job_resource_info == null)
                {
                    throw new Exception($"job_resource_info not exists. uid : {body.resource_uid}");
                }

                if (job_resource_info.player_id != 0)
                {
                    throw new Exception($"already another player used. uid : {body.resource_uid}");
                }

                var skill_list = JobController.GetSkillList(player_info);
                bool is_useable = false;
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
                                is_useable = true;
                            }
                        }
                        break;
                }

                if (!is_useable)
                {
                    throw new Exception("not useable skill");
                }

                job_resource_info.player_id = user.player_id;
                await JobResourceController.Save(user.cache_helper, job_resource_info);
            }
        }
    }
}
