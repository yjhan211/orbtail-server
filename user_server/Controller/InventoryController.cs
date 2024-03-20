namespace user_server
{
    using game_server;
    using network;

    public static class InventoryController
    {
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
            var result = await WearItem(user, body.item_id);

            Packet packet = PacketMaker.U_TO_C_WEAR_ITEM(result);
            user.SendToClient(packet);
        }

        public static async Task<PlayerInfo> WearItem(GameUser user, long item_id)
        {
            var player_info = await PlayerInfoController.Load(user.cache_helper, user.player_id);
            if (player_info == null)
            {
                throw new Exception("player_info not exists");
            }

            var target_item = player_info.inventory_info.item_list.Find(
                item => item.item_id == item_id
            );

            if (target_item == null)
            {
                return player_info;
            }

            var item_type = (int)(item_id / 10000);

            // 이전에 착용한 같은 타입의 아이템 제거
            var previous_wear_items = player_info.wear_items
                .Where(id => (int)(id / 10000) == item_type)
                .ToList();

            foreach (var previous_wear_item_id in previous_wear_items)
            {
                player_info.wear_items.Remove(previous_wear_item_id);

                var previous_item = player_info.inventory_info.item_list.Find(
                    item => item.item_id == previous_wear_item_id
                );

                if (previous_item != null)
                {
                    previous_item.is_wear = false;
                }
            }

            // 새로운 아이템 착용
            target_item.is_wear = true;
            player_info.wear_items.Add(item_id);

            await PlayerInfoController.Save(user.cache_helper, player_info);

            return player_info;
        }
    }
}
