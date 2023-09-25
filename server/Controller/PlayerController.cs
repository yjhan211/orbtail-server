namespace game_server
{
    using System.Text.Json;
    using MessagePack;
    using StackExchange.Redis;

    public partial class PlayerInfo
    {
        public PlayerInfo(long player_id, string name, Cell cell)
        {
            this.player_id = player_id;
            this.object_info = new GameObjectInfo(ObjectType.PLAYER, player_id, cell);
            this.name = name;
        }

        public async Task Save()
        {
            await this.object_info.Save();
            await Program.redis_client.BasicRetryAsync(
                (db) => db.HashSetAsync(HASH_KEY, this.player_id, JsonSerializer.Serialize(this))
            );
        }

        public async static Task<PlayerInfo?> Load(long player_id)
        {
            try
            {
                var serialized = await Program.redis_client.BasicRetryAsync(
                    (db) => db.HashGetAsync(HASH_KEY, player_id)
                );

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
            await Program.redis_client.BasicRetryAsync(
                (db) => db.HashDeleteAsync(HASH_KEY, player_id)
            );
        }
    }
}
