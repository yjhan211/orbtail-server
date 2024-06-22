namespace network
{
    using MessagePack;
    using RedLockNet;
    using RedLockNet.SERedis;

    [MessagePackObject]
    public class JobResourceInfo : IMessagePackObject
    {
        [IgnoreMember]
        public const string HASH_KEY = "job_resource_info";

        [IgnoreMember]
        public GameObjectInfo object_info { get; set; }

        [Key("resource_uid")]
        public long resource_uid { get; set; } // 유니크 아이디

        [Key("resource_id")]
        public int resource_id { get; set; } // 리소스 종류. 네모난 돌, 동그란 돌, 잡초..

        [Key("player_id")]
        public long player_id { get; set; } // 점유중인 플레이어 아이디

        [Key("end_timestamp")]
        public DateTime end_timestamp { get; set; } // 점유 끝나는 시간

        // 이거 없애면 안됨 MessagePack에서 씀
        public JobResourceInfo()
        {
            this.object_info = new();
            this.resource_uid = 0;
            this.resource_id = 0;
            this.player_id = 0;
        }

        public JobResourceInfo(long resource_uid, int resource_id, GameObjectInfo object_info)
        {
            this.object_info = object_info;
            this.resource_uid = resource_uid;
            this.resource_id = resource_id;
            this.player_id = 0;
        }

        public string GetLockKey()
        {
            return $"job_resource_lock_{this.resource_uid}";
        }

        public static string GetLockKey(long resource_uid)
        {
            return $"job_resource_lock_{resource_uid}";
        }

        public static async Task<IRedLock> Lock(RedLockFactory redlock, long resource_uid)
        {
            return await redlock.CreateLockAsync(
                JobResourceInfo.GetLockKey(resource_uid),
                Config.LOCK_TTL
            );
        }

        public async Task Save()
        {
            await this.object_info.Save();
            await CacheHelper.Instance.HashSetAsync(
                JobResourceInfo.HASH_KEY,
                this.resource_uid,
                MessagePackSerializer.Serialize(this)
            );
        }

        public static async Task<JobResourceInfo?> Load(long resource_uid)
        {
            var serialized_data = await CacheHelper.Instance.HashGetAsync(
                JobResourceInfo.HASH_KEY,
                resource_uid
            );

            if (serialized_data.IsNull)
            {
                return null;
            }

            var resource_info = MessagePackSerializer.Deserialize<JobResourceInfo?>(
                serialized_data
            );

            if (resource_info == null)
            {
                return null;
            }

            var object_info = await GameObjectInfo.Load(ObjectType.JOBRESOURCE, resource_uid);
            if (object_info == null)
            {
                return null;
            }

            resource_info.object_info = object_info;
            return resource_info;
        }

        public async Task Delete()
        {
            await CacheHelper.Instance.HashDeleteAsync(JobResourceInfo.HASH_KEY, this.resource_uid);
        }

        public static async Task Delete(long resource_uid)
        {
            await CacheHelper.Instance.HashDeleteAsync(JobResourceInfo.HASH_KEY, resource_uid);
        }
    }
}
