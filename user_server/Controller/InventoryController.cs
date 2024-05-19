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

                    var target_item_index = player_info.inventory_info.item_list.FindIndex(
                        item => item.item_uid == body.item_uid
                    );

                    var target_item = player_info.inventory_info.item_list[target_item_index];
                    if (target_item == null)
                    {
                        throw new Exception($"Item with uid {body.item_uid} not found");
                    }

                    if (target_item.count <= 0)
                    {
                        throw new Exception($"Item {body.item_uid} count invalid");
                    }

                    if (target_item.count <= 1)
                    {
                        player_info.inventory_info.item_list.RemoveAt(target_item_index);
                    }
                    else
                    {
                        // TODO 일괄사용
                        player_info.inventory_info.item_list[target_item_index].count -= 1;
                    }

                    var is_create = true;
                    var is_countable = !GameDesignData.IsWearableItem(target_item.item_id);
                    if (is_countable)
                    {
                        for (int i = 0; i < lab_info.inventory_info.item_list.Count; i++)
                        {
                            if (lab_info.inventory_info.item_list[i].item_id == target_item.item_id)
                            {
                                lab_info.inventory_info.item_list[i].count += 1;
                                is_create = false;
                                break;
                            }
                        }
                    }

                    if (is_create)
                    {
                        var add_item = await CreateItem(user, target_item.item_id, 1);
                        lab_info.inventory_info.item_list.Add(add_item);
                    }

                    await LabInfoController.Save(user.cache_helper, lab_info);
                    await PlayerInfoController.Save(user.cache_helper, player_info);
                }
            }

            await GetCurrentItemList(user);
            Packet packet = PacketMaker.U_TO_U_LAB_INVENTORY(lab_info.inventory_info.item_list);
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

                    var target_item_index = lab_info.inventory_info.item_list.FindIndex(
                        (item) => item.item_uid == body.item_uid
                    );

                    if (target_item_index < 0)
                    {
                        throw new Exception("not found item info");
                    }

                    var target_item = lab_info.inventory_info.item_list[target_item_index];
                    if (target_item == null)
                    {
                        throw new Exception($"Item with uid {body.item_uid} not found");
                    }

                    if (target_item.count <= 0)
                    {
                        throw new Exception($"Item {body.item_uid} count invalid");
                    }

                    if (target_item.count <= 1)
                    {
                        lab_info.inventory_info.item_list.RemoveAt(target_item_index);
                    }
                    else
                    {
                        // TODO 일괄사용
                        lab_info.inventory_info.item_list[target_item_index].count -= 1;
                    }

                    var is_create = true;
                    var is_countable = !GameDesignData.IsWearableItem(target_item.item_id);
                    if (is_countable)
                    {
                        for (int i = 0; i < player_info.inventory_info.item_list.Count; i++)
                        {
                            if (
                                player_info.inventory_info.item_list[i].item_id
                                == target_item.item_id
                            )
                            {
                                player_info.inventory_info.item_list[i].count += target_item.count;
                                is_create = false;
                                break;
                            }
                        }
                    }

                    if (is_create)
                    {
                        var add_item = await CreateItem(user, target_item.item_id, 1);
                        player_info.inventory_info.item_list.Add(add_item);
                    }

                    await InventoryInfoController.Save(user.cache_helper, lab_info.inventory_info);
                    await PlayerInfoController.Save(user.cache_helper, player_info);
                }
            }

            await GetCurrentItemList(user);

            Packet packet = PacketMaker.U_TO_U_LAB_INVENTORY(lab_info.inventory_info.item_list);
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
            List<ItemInfo> item_list = new();
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

                    item_list = lab_info.inventory_info.item_list.ToList();
                }
            }

            user.SendLabItemList(item_list);
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

            if (inventory_info.item_list.Count == 0)
            {
                Packet packet = PacketMaker.U_TO_C_INVENTORY_ITEM_LIST(new(), true);
                user.SendToClient(packet);
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

        public static void BroadcastCurrentLabItemList(GameUser user, LabInfo lab_info)
        {
            var inventory_info = lab_info.inventory_info;
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

                Packet packet = PacketMaker.U_TO_C_LAB_INVENTORY(batch, is_ended);
                user.SendToClient(packet);
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
