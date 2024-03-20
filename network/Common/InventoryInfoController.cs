namespace network
{
    using MessagePack;

    public static class InventoryInfoController
    {
        public static async Task Save(CacheHelper cache_helper, InventoryInfo inventory_info)
        {
            await cache_helper.HashSet(
                InventoryInfo.HASH_KEY,
                inventory_info.player_id,
                MessagePackSerializer.Serialize(inventory_info)
            );
        }

        public static async Task<InventoryInfo?> Load(CacheHelper cache_helper, long player_id)
        {
            var serialized_data = await cache_helper.HashGet(InventoryInfo.HASH_KEY, player_id);

            if (serialized_data.IsNull)
            {
                return null;
            }

            var job_info = MessagePackSerializer.Deserialize<InventoryInfo?>(serialized_data);
            return job_info;
        }

        public static async Task Delete(CacheHelper cache_helper, long player_id)
        {
            await cache_helper.HashDelete(InventoryInfo.HASH_KEY, player_id);
        }
    }
}
