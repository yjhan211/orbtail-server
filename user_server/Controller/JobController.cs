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
    }
}
