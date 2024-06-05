namespace user_server
{
    using game_server;
    using network;

    public static class InventoryController
    {
        public static async Task<ItemInfo> CreateItem(GameUser user, int item_id, int count)
        {
            // TODO RDB PK로 교체 예정
            long item_uid = await user.cache_helper.StringIncrement("temp_item_uid");
            return new ItemInfo(item_uid, item_id, count);
        }

        public static async Task AddLabItem(GameUser user, C_TO_U_LAB_INVENTORY_ADD_ITEM body)
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

                using (await LabInfoController.Lock(user.redlock, player_info.lab_id))
                {
                    lab_info = await LabInfoController.Load(user.cache_helper, player_info.lab_id);
                    if (lab_info == null)
                    {
                        throw new Exception("lab_info not exists");
                    }

                    if (
                        !player_info.inventory_info.item_dict.TryGetValue(
                            body.item_uid,
                            out var target_item
                        )
                    )
                    {
                        throw new Exception($"Item with uid {body.item_uid} not found");
                    }

                    if (target_item.count <= 0)
                    {
                        throw new Exception($"Item {body.item_uid} count invalid");
                    }

                    if (target_item.count <= 1)
                    {
                        player_info.inventory_info.item_dict.Remove(body.item_uid);
                    }
                    else
                    {
                        // TODO 일괄사용
                        target_item.count -= 1;
                    }

                    var is_countable = !GameDesignData.IsWearableItem(target_item.item_id);
                    var is_exist = lab_info.inventory_info.item_dict.ContainsKey(
                        target_item.item_uid
                    );

                    if (is_countable && is_exist)
                    {
                        lab_info.inventory_info.item_dict[target_item.item_uid].count += 1;
                    }
                    else
                    {
                        var add_item = await CreateItem(user, target_item.item_id, 1);
                        lab_info.inventory_info.item_dict[add_item.item_uid] = add_item;
                    }

                    await LabInfoController.Save(user.cache_helper, lab_info);
                    await PlayerInfoController.Save(user.cache_helper, player_info);
                }
            }

            await GetCurrentItemList(user);

            Packet packet = PacketMaker.U_TO_U_LAB_INVENTORY(lab_info.inventory_info.item_dict);
            foreach (var lab_member in lab_info.member_dict)
            {
                user.nats_client.Publish(
                    GameObjectInfo.MakeHashField(ObjectType.PLAYER, lab_member.Key),
                    packet.ToBytes()
                );
            }

            Packet.Destroy(packet);
        }

        public static async Task TakeLabItem(GameUser user, C_TO_U_LAB_INVENTORY_TAKE_ITEM body)
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

                using (await LabInfoController.Lock(user.redlock, player_info.lab_id))
                {
                    lab_info = await LabInfoController.Load(user.cache_helper, player_info.lab_id);
                    if (lab_info == null)
                    {
                        throw new Exception("lab_info not exists");
                    }

                    if (
                        !lab_info.inventory_info.item_dict.TryGetValue(
                            body.item_uid,
                            out var target_item
                        )
                    )
                    {
                        throw new Exception("not found item info");
                    }

                    if (target_item.count <= 0)
                    {
                        throw new Exception($"Item {body.item_uid} count invalid");
                    }

                    if (target_item.count <= 1)
                    {
                        lab_info.inventory_info.item_dict.Remove(body.item_uid);
                    }
                    else
                    {
                        // TODO 일괄사용
                        lab_info.inventory_info.item_dict[body.item_uid].count -= 1;
                    }

                    var is_countable = !GameDesignData.IsWearableItem(target_item.item_id);
                    var is_exist = lab_info.inventory_info.item_dict.ContainsKey(
                        target_item.item_uid
                    );

                    if (is_countable && is_exist)
                    {
                        player_info.inventory_info.item_dict[target_item.item_uid].count +=
                            target_item.count;
                    }
                    else
                    {
                        var add_item = await CreateItem(user, target_item.item_id, 1);
                        player_info.inventory_info.item_dict[add_item.item_uid] = add_item;
                    }

                    await InventoryInfoController.Save(user.cache_helper, lab_info.inventory_info);
                    await PlayerInfoController.Save(user.cache_helper, player_info);
                }
            }

            await GetCurrentItemList(user);

            Packet packet = PacketMaker.U_TO_U_LAB_INVENTORY(lab_info.inventory_info.item_dict);
            foreach (var lab_member in lab_info.member_dict)
            {
                user.nats_client.Publish(
                    GameObjectInfo.MakeHashField(ObjectType.PLAYER, lab_member.Key),
                    packet.ToBytes()
                );
            }
            Packet.Destroy(packet);
        }

        public static async Task GetLabInventory(GameUser user)
        {
            PlayerInfo? player_info;
            LabInfo? lab_info;
            Dictionary<long, ItemInfo> item_dict = new();
            using (await PlayerInfoController.Lock(user.redlock, user.player_id))
            {
                player_info = await PlayerInfoController.Load(user.cache_helper, user.player_id);
                if (player_info == null)
                {
                    throw new Exception("player_info not exists");
                }

                using (await LabInfoController.Lock(user.redlock, player_info.lab_id))
                {
                    lab_info = await LabInfoController.Load(user.cache_helper, player_info.lab_id);
                    if (lab_info == null)
                    {
                        throw new Exception("lab_info not exists");
                    }

                    item_dict = lab_info.inventory_info.item_dict;
                }
            }

            user.SendLabItemList(item_dict);
        }

        public static async Task RequestWearItem(GameUser user, C_TO_U_WEAR_ITEM body)
        {
            if (user.in_action)
            {
                throw new Exception("player in action");
            }

            PlayerInfo? player_info = await PlayerInfoController.Load(
                user.cache_helper,
                user.player_id
            );

            if (player_info == null)
            {
                throw new Exception("cannot found player info");
            }

            using (await PlayerInfoController.Lock(user.redlock, user.player_id))
            {
                player_info.WearItem(body.item_uid);
                await PlayerInfoController.Save(user.cache_helper, player_info);
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

            if (inventory_info.item_dict.Count == 0)
            {
                Packet packet = PacketMaker.U_TO_C_INVENTORY_ITEM_LIST(new(), true);
                user.SendToClient(packet);
                return;
            }

            int index = 0;
            var item_keys = inventory_info.item_dict.Keys.ToArray();

            while (index < item_keys.Length)
            {
                var batch_dict = new Dictionary<long, ItemInfo>();

                for (int i = index; i < index + Config.BROADCAST_UNIT && i < item_keys.Length; i++)
                {
                    var key = item_keys[i];
                    batch_dict[key] = inventory_info.item_dict[key];
                }

                var is_ended = index + Config.BROADCAST_UNIT >= item_keys.Length;

                Packet packet = PacketMaker.U_TO_C_INVENTORY_ITEM_LIST(batch_dict, is_ended);
                user.SendToClient(packet);

                index += Config.BROADCAST_UNIT;
            }
        }

        public static void BroadcastCurrentLabItemList(GameUser user, LabInfo lab_info)
        {
            var inventory_info = lab_info.inventory_info;
            if (inventory_info == null)
            {
                throw new Exception("inventory_info not exists");
            }

            int index = 0;
            var item_keys = inventory_info.item_dict.Keys.ToArray();

            while (index < item_keys.Length)
            {
                var batch_dict = new Dictionary<long, ItemInfo>();

                for (int i = index; i < index + Config.BROADCAST_UNIT && i < item_keys.Length; i++)
                {
                    var key = item_keys[i];
                    batch_dict[key] = inventory_info.item_dict[key];
                }

                var is_ended = index + Config.BROADCAST_UNIT >= item_keys.Length;

                Packet packet = PacketMaker.U_TO_C_LAB_INVENTORY(batch_dict, is_ended);
                user.SendToClient(packet);

                index += Config.BROADCAST_UNIT;
            }
        }

        public static async Task RequestUseItem(GameUser user, C_TO_U_USE_ITEM body)
        {
            PlayerInfo? player_info;
            using (await PlayerInfoController.Lock(user.redlock, user.player_id))
            {
                player_info = await PlayerInfoController.Load(user.cache_helper, user.player_id);
                if (player_info == null)
                {
                    throw new Exception("cannot found player info");
                }

                player_info.UseItem(body.item_uid);
                await PlayerInfoController.Save(user.cache_helper, player_info);
            }

            Packet packet = PacketMaker.U_TO_C_USE_ITEM(player_info.job_info);
            user.SendToClient(packet);

            await GetCurrentItemList(user);
        }
    }
}
