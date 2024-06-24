namespace network
{
    using MessagePack;
    using RedLockNet;
    using RedLockNet.SERedis;

    [MessagePackObject]
    public class ExploreTargetInfo : IMessagePackObject
    {
        [IgnoreMember]
        public const string HASH_KEY = "explore_target_info";

        [IgnoreMember]
        public GameObjectInfo object_info { get; set; }

        [Key("explore_target_uid")]
        public long explore_target_uid { get; set; }

        [Key("explore_target_id")]
        public int explore_target_id { get; set; }

        [Key("player_id")]
        public long player_id { get; set; } // 점유중인 플레이어 아이디

        [Key("end_timestamp")]
        public DateTime end_timestamp { get; set; } // 점유 끝나는 시간

        // 이거 없애면 안됨 MessagePack에서 씀
        public ExploreTargetInfo()
        {
            this.object_info = new();
            this.explore_target_uid = 0;
            this.explore_target_id = 0;
            this.player_id = 0;
        }

        public ExploreTargetInfo(
            long explore_target_uid,
            int explore_target_id,
            GameObjectInfo object_info
        )
        {
            this.object_info = object_info;
            this.explore_target_uid = explore_target_uid;
            this.explore_target_id = explore_target_id;
            this.player_id = 0;
        }

        public string GetLockKey()
        {
            return $"explore_target_lock_{this.explore_target_uid}";
        }

        public static string GetLockKey(long explore_target_uid)
        {
            return $"explore_target_lock_{explore_target_uid}";
        }

        public static async Task<IRedLock> Lock(RedLockFactory redlock, long explore_target_uid)
        {
            return await redlock.CreateLockAsync(
                ExploreTargetInfo.GetLockKey(explore_target_uid),
                Config.LOCK_TTL
            );
        }

        public async Task Save()
        {
            await this.object_info.Save();
            await CacheHelper.Instance.HashSetAsync(
                ExploreTargetInfo.HASH_KEY,
                this.explore_target_uid,
                MessagePackSerializer.Serialize(this)
            );
        }

        public static async Task<ExploreTargetInfo?> Load(long explore_target_uid)
        {
            var serialized_data = await CacheHelper.Instance.HashGetAsync(
                ExploreTargetInfo.HASH_KEY,
                explore_target_uid
            );

            if (serialized_data.IsNull)
            {
                return null;
            }

            var explore_target_info = MessagePackSerializer.Deserialize<ExploreTargetInfo?>(
                serialized_data
            );

            if (explore_target_info == null)
            {
                return null;
            }

            var object_info = await GameObjectInfo.Load(ObjectType.JOBRESOURCE, explore_target_uid);
            if (object_info == null)
            {
                return null;
            }

            explore_target_info.object_info = object_info;
            return explore_target_info;
        }

        public async Task Delete()
        {
            await CacheHelper.Instance.HashDeleteAsync(
                ExploreTargetInfo.HASH_KEY,
                this.explore_target_uid
            );
        }

        public static async Task Delete(long explore_target_uid)
        {
            await CacheHelper.Instance.HashDeleteAsync(
                ExploreTargetInfo.HASH_KEY,
                explore_target_uid
            );
        }
    }
}
