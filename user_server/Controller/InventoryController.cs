namespace user_server
{
    using game_server;
    using network;
    using MessagePack;

    public static class InventoryController
    {
        public static async Task<ItemInfo> CreateItem(GameUser user, int item_id, int count)
        {
            // TODO RDB PK로 교체 예정
            long item_uid = await user.cache_helper.StringIncrement("temp_item_uid");
            return new ItemInfo(item_uid, item_id, count);
        }

        public static async Task<InventoryInfo> AddItem(
            GameUser user,
            long player_id,
            ItemInfo item_info
        )
        {
            var inventory_info = await InventoryInfoController.Load(user.cache_helper, player_id);
            if (inventory_info == null)
            {
                throw new Exception("inventory_info not exists");
            }

            inventory_info.item_list.Add(item_info);
            await InventoryInfoController.Save(user.cache_helper, inventory_info);

            return inventory_info;
        }

        public static async Task<InventoryInfo> AddItem(
            GameUser user,
            long player_id,
            List<ItemInfo> item_info_list
        )
        {
            var inventory_info = await InventoryInfoController.Load(user.cache_helper, player_id);
            if (inventory_info == null)
            {
                throw new Exception("inventory_info not exists");
            }

            foreach (var item_info in item_info_list)
            {
                inventory_info.item_list.Add(item_info);
            }
            await InventoryInfoController.Save(user.cache_helper, inventory_info);

            return inventory_info;
        }

        public static async Task RequestWearItem(GameUser user, C_TO_U_WEAR_ITEM body)
        {
            if (user.in_action)
            {
                throw new Exception("player in action");
            }

            PlayerInfo player_info;
            using (await PlayerInfoController.Lock(user.redlock, user.player_id))
            {
                player_info = await WearItem(user, body.item_uid);
            }

            Packet packet = PacketMaker.U_TO_C_WEAR_ITEM(player_info);
            user.SendToClient(packet);
            await GetCurrentItemList(user);

            user.BroadcastUpdatePlayerInfo(player_info);
        }

        public static async Task GetCurrentItemList(GameUser user)
        {
            var player_info = await PlayerInfoController.Load(user.cache_helper, user.player_id);
            if (player_info == null)
            {
                throw new Exception("player_info not exists");
            }

            var inventory_info = player_info.inventory_info;
            if (inventory_info == null)
            {
                throw new Exception("inventory_info not exists");
            }

            for (int i = 0; i < inventory_info.item_list.Count; i += Config.BROADCAST_UNIT)
            {
                List<ItemInfo> batch = inventory_info.item_list
                    .Skip(i)
                    .Take(Config.BROADCAST_UNIT)
                    .ToList();

                var remain = inventory_info.item_list.Count - i - Config.BROADCAST_UNIT;
                var is_ended = remain <= 0;

                Packet packet = PacketMaker.U_TO_C_INVENTORY_ITEM_LIST(batch, is_ended);
                user.SendToClient(packet);
            }
        }

        public static async Task RequestUseItem(GameUser user, C_TO_U_USE_ITEM body)
        {
            PlayerInfo player_info;
            using (await PlayerInfoController.Lock(user.redlock, user.player_id))
            {
                player_info = await UseItem(user, body.item_uid);
            }

            Packet packet = PacketMaker.U_TO_C_USE_ITEM(player_info.job_info);
            user.SendToClient(packet);

            await GetCurrentItemList(user);
        }

        public static async Task<PlayerInfo> UseItem(GameUser user, long item_uid)
        {
            var player_info = await PlayerInfoController.Load(user.cache_helper, user.player_id);
            if (player_info == null)
            {
                throw new Exception("player_info not exists");
            }

            var target_item_index = player_info.inventory_info.item_list.FindIndex(
                item => item.item_uid == item_uid
            );

            var target_item = player_info.inventory_info.item_list[target_item_index];
            if (target_item == null)
            {
                throw new Exception($"Item with uid {item_uid} not found");
            }

            if (!GameDesignData.IsUseableItem(target_item.item_id))
            {
                throw new Exception($"not wearable item {target_item.item_id}");
            }

            switch (target_item.item_id)
            {
                case 2001000001:
                    player_info.job_info.hp = Math.Min(
                        GameDesignData.GetMaxHP(player_info.job_info.job_grade),
                        player_info.job_info.hp + 20
                    );
                    break;
            }

            player_info.inventory_info.item_list.RemoveAt(target_item_index);
            await PlayerInfoController.Save(user.cache_helper, player_info);

            return player_info;
        }

        public static async Task<PlayerInfo> WearItem(GameUser user, long item_uid)
        {
            var player_info = await PlayerInfoController.Load(user.cache_helper, user.player_id);
            if (player_info == null)
            {
                throw new Exception("player_info not exists");
            }

            var target_item_index = player_info.inventory_info.item_list.FindIndex(
                item => item.item_uid == item_uid
            );

            var target_item = player_info.inventory_info.item_list[target_item_index];

            if (target_item == null)
            {
                throw new Exception($"Item with uid {item_uid} not found");
            }

            if (!GameDesignData.IsWearableItem(target_item.item_id))
            {
                throw new Exception($"not wearable item {target_item.item_id}");
            }

            if (target_item.is_wear)
            {
                // 착용 해제
                player_info.wear_items.Remove(target_item.item_id);
                target_item.is_wear = false;
            }
            else
            {
                // 같은 종류의 아이템 인덱스 찾기
                var last_wear_item_index = player_info.inventory_info.item_list.FindIndex(
                    item =>
                        GameDesignData.IsSameTypeItem(item.item_id, target_item.item_id)
                        && item.is_wear
                );

                if (last_wear_item_index != -1)
                {
                    // 같은 종류 아이템 착용 해제
                    player_info.inventory_info.item_list[last_wear_item_index].is_wear = false;
                    player_info.wear_items.Remove(
                        player_info.inventory_info.item_list[last_wear_item_index].item_id
                    );
                }

                // 새로운 아이템 착용
                player_info.inventory_info.item_list[target_item_index].is_wear = true;
                player_info.wear_items.Add(target_item.item_id);
            }

            await PlayerInfoController.Save(user.cache_helper, player_info);

            return player_info;
        }
    }
}
