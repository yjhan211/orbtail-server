using MessagePack;
using RedLockNet;
using RedLockNet.SERedis;
using network.helpers;

namespace network.common
{
    [MessagePackObject]
    public class JobResourceInfo : IMessagePackObject
    {
        [IgnoreMember]
        public const string HASH_KEY = "JobResourceInfo";

        [IgnoreMember]
        public GameObjectInfo ObjectInfo { get; set; }

        [Key("resourceUid")]
        public long ResourceUid { get; set; } // 유니크 아이디

        [Key("resourceId")]
        public int ResourceId { get; set; } // 리소스 종류. 네모난 돌, 동그란 돌, 잡초..

        [Key("playerId")]
        public long PlayerId { get; set; } // 점유중인 플레이어 아이디

        [Key("endTimestamp")]
        public DateTime EndTimestamp { get; set; } // 점유 끝나는 시간

        // 이거 없애면 안됨 MessagePack에서 씀
        public JobResourceInfo()
        {
            ObjectInfo = new();
            ResourceUid = 0;
            ResourceId = 0;
            PlayerId = 0;
        }

        public JobResourceInfo(long resourceUid, int resourceId, GameObjectInfo objectInfo)
        {
            ObjectInfo = objectInfo;
            ResourceUid = resourceUid;
            ResourceId = resourceId;
            PlayerId = 0;
        }

        public string GetLockKey()
        {
            return $"job_resource_lock_{ResourceUid}";
        }

        public static string GetLockKey(long resourceUid)
        {
            return $"job_resource_lock_{resourceUid}";
        }

        public static async Task<IRedLock> Lock(RedLockFactory redlock, long resourceUid)
        {
            return await redlock.CreateLockAsync(JobResourceInfo.GetLockKey(resourceUid), Config.LOCK_TTL);
        }

        public async Task Save()
        {
            await ObjectInfo.Save();
            await CacheHelper.Instance.HashSetAsync(JobResourceInfo.HASH_KEY, ResourceUid, MessagePackSerializer.Serialize(this));
        }

        public static async Task<JobResourceInfo?> Load(long resourceUid)
        {
            var serializedData = await CacheHelper.Instance.HashGetAsync(JobResourceInfo.HASH_KEY, resourceUid);
            if (serializedData.IsNull)
            {
                return null;
            }

            var resourceInfo = MessagePackSerializer.Deserialize<JobResourceInfo?>(serializedData);
            if (resourceInfo == null)
            {
                return null;
            }

            var objectInfo = await GameObjectInfo.Load(ObjectType.JOBRESOURCE, resourceUid);
            if (objectInfo == null)
            {
                return null;
            }

            resourceInfo.ObjectInfo = objectInfo;
            return resourceInfo;
        }

        public async Task Delete()
        {
            await CacheHelper.Instance.HashDeleteAsync(JobResourceInfo.HASH_KEY, ResourceUid);
        }

        public static async Task Delete(long resourceUid)
        {
            await CacheHelper.Instance.HashDeleteAsync(JobResourceInfo.HASH_KEY, resourceUid);
        }
    }
}
