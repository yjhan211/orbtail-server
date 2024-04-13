namespace network
{
    using MessagePack;

    public static class InventoryInfoController
    {
        public static async Task Save(CacheHelper cache_helper, InventoryInfo inventory_info)
        {
            await cache_helper.HashSet(
                InventoryInfo.HASH_KEY,
                $"{inventory_info.owner_type}_{inventory_info.owner_id}",
                MessagePackSerializer.Serialize(inventory_info)
            );
        }

        public static async Task<InventoryInfo?> Load(
            CacheHelper cache_helper,
            InventoryOwnerType owner_type,
            long owner_id
        )
        {
            var serialized_data = await cache_helper.HashGet(
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

        public static async Task Delete(
            CacheHelper cache_helper,
            InventoryOwnerType owner_type,
            long owner_id
        )
        {
            await cache_helper.HashDelete(InventoryInfo.HASH_KEY, $"{owner_type}_{owner_id}");
        }
    }
}
