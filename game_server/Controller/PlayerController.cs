namespace game_server
{
    using StackExchange.Redis;
    using MessagePack;
    using network;

    public static class PlayerController
    {
        public static async Task Save(PlayerInfo player_info)
        {
            await GameObjecController.Save(player_info.object_info);
            await Program.cache_helper.HashSet(
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

        public static async Task<PlayerInfo?> Load(long player_id)
        {
            try
            {
                var serialized = await Program.cache_helper.HashGet(PlayerInfo.HASH_KEY, player_id);

                if (serialized == RedisValue.Null)
                {
                    return null;
                }

                var player_info = MessagePackSerializer.Deserialize<PlayerInfo>(serialized);
                if (player_info == null)
                {
                    return null;
                }

                var object_info = await GameObjecController.Load(ObjectType.PLAYER, player_id);
                if (object_info == null)
                {
                    return null;
                }

                player_info.object_info = object_info;
                return player_info;
            }
            catch (Exception e)
            {
                Console.WriteLine(e.Message + e.StackTrace);
                return null;
            }
        }

        public static async Task Delete(PlayerInfo player_info)
        {
            await GameObjecController.Delete(player_info.object_info);
            await Program.cache_helper.HashDelete(PlayerInfo.HASH_KEY, player_info.player_id);
        }
    }
}
