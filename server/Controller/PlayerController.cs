namespace game_server
{
    using network;
    using System.Text.Json;
    using StackExchange.Redis;

    public partial class PlayerInfo
    {
        public PlayerInfo(long player_id, string name, Cell cell)
        {
            this.player_id = player_id;
            this.object_info = new GameObjectInfo(ObjectType.PLAYER, player_id, cell);
            this.name = name;
        }

        public string GetLockKey()
        {
            return $"player_lock_{this.player_id}";
        }

        public static string GetLockKey(long player_id)
        {
            return $"player_lock_{player_id}";
        }

        public async Task Save()
        {
            await this.object_info.Save();
            await CacheHelper.HashSet(HASH_KEY, this.player_id, JsonSerializer.Serialize(this));
        }

        public void Save(Transaction transaction)
        {
            this.object_info.Save(transaction);
            transaction.HashSet(HASH_KEY, this.player_id, JsonSerializer.Serialize(this));
        }

        public async static Task<PlayerInfo?> Load(long player_id)
        {
            try
            {
                var serialized = await CacheHelper.HashGet(HASH_KEY, player_id);

                if (serialized == RedisValue.Null)
                {
                    return null;
                }

                var player_info = JsonSerializer.Deserialize<PlayerInfo>(serialized.ToString());
                if (player_info == null)
                {
                    return null;
                }

                var object_info = await GameObjectInfo.Load(ObjectType.PLAYER, player_id);
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

        public async Task Delete()
        {
            await this.object_info.Delete();
            await CacheHelper.HashDelete(HASH_KEY, player_id);
        }
    }
}
