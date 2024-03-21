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

        public static async Task RequestWearItem(GameUser user, C_TO_U_WEAR_ITEM body)
        {
            PlayerInfo result_player_info;
            using (await PlayerInfoController.Lock(user.redlock, user.player_id))
            {
                result_player_info = await WearItem(user, body.item_uid);
            }

            Packet packet = PacketMaker.U_TO_C_WEAR_ITEM(result_player_info);
            user.SendToClient(packet);

            var position_key = MapHelper.GetPositionKey(
                result_player_info.object_info.current_cell
            );

            var target_server_list = MapHelper.GetBoundServerList(
                Program.game_server_num,
                MapHelper.GetCell(position_key)
            );

            foreach (var target_server in target_server_list)
            {
                user.nats_client!.Publish(
                    $"update_player_{target_server}",
                    MessagePackSerializer.Serialize((position_key, result_player_info))
                );
            }
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
