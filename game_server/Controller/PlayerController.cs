namespace game_server
{
    using StackExchange.Redis;
    using MessagePack;
    using network;

    public static class PlayerController
    {
        public static async Task Save(CacheHelper cache_helper, PlayerInfo player_info)
        {
            await GameObjecController.Save(cache_helper, player_info.object_info);
            await cache_helper.HashSet(
                PlayerInfo.HASH_KEY,
                player_info.player_id,
                MessagePackSerializer.Serialize(player_info)
            );
        }

        // public void Save(Transaction transaction)
        // {
        //     this.object_info.Save(transaction);
        //     transaction.HashSet(HASH_KEY, this.player_id, MessagePackSerializer.Serialize(this));
        // }

        public static async Task<PlayerInfo?> Load(CacheHelper cache_helper, long player_id)
        {
            try
            {
                var serialized = await cache_helper.HashGet(PlayerInfo.HASH_KEY, player_id);

                if (serialized == RedisValue.Null)
                {
                    return null;
                }

                var player_info = MessagePackSerializer.Deserialize<PlayerInfo>(serialized);
                if (player_info == null)
                {
                    return null;
                }

                var object_info = await GameObjecController.Load(
                    cache_helper,
                    ObjectType.PLAYER,
                    player_id
                );

                if (object_info == null)
                {
                    return null;
                }

                player_info.object_info = object_info;
                return player_info;
            }
            catch (Exception e)
            {
                Console.WriteLine($"[GameServer] {e.Message} {e.StackTrace}");
                return null;
            }
        }

        public static async Task Delete(CacheHelper cache_helper, PlayerInfo player_info)
        {
            await GameObjecController.Delete(cache_helper, player_info.object_info);
            await cache_helper.HashDelete(PlayerInfo.HASH_KEY, player_info.player_id);
        }
    }
}
