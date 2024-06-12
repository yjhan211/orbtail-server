namespace network
{
    using MessagePack;

    public static class InventoryInfoController
    {
        public static void Save(CacheHelper cache_helper, InventoryInfo inventory_info)
        {
            cache_helper.HashSet(
                InventoryInfo.HASH_KEY,
                $"{inventory_info.owner_type}_{inventory_info.owner_id}",
                MessagePackSerializer.Serialize(inventory_info)
            );
        }

        public static InventoryInfo? Load(
            CacheHelper cache_helper,
            InventoryOwnerType owner_type,
            long owner_id
        )
        {
            var serialized_data = cache_helper.HashGet(
                InventoryInfo.HASH_KEY,
                $"{owner_type}_{owner_id}"
            );

            if (serialized_data.IsNull)
            {
                return new InventoryInfo(owner_type, owner_id);
            }

            var inventory_info = MessagePackSerializer.Deserialize<InventoryInfo?>(serialized_data);
            return inventory_info;
        }

        public static void Delete(
            CacheHelper cache_helper,
            InventoryOwnerType owner_type,
            long owner_id
        )
        {
            cache_helper.HashDelete(InventoryInfo.HASH_KEY, $"{owner_type}_{owner_id}");
        }
    }
}
