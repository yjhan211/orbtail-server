using MessagePack;
using RedLockNet;
using RedLockNet.SERedis;
using network.helpers;

namespace network.common
{
    [MessagePackObject]
    public class ExploreTargetInfo : IMessagePackObject
    {
        [IgnoreMember]
        public const string HASH_KEY = "ExploreTargetInfo";

        [IgnoreMember]
        public GameObjectInfo ObjectInfo { get; set; }

        [Key("exploreTargetUid")]
        public long ExploreTargetUid { get; set; }

        [Key("exploreTargetId")]
        public int ExploreTargetId { get; set; }

        [Key("playerId")]
        public long PlayerId { get; set; } // 점유중인 플레이어 아이디

        [Key("endTimestamp")]
        public DateTime EndTimestamp { get; set; } // 점유 끝나는 시간

        // 이거 없애면 안됨 MessagePack에서 씀
        public ExploreTargetInfo()
        {
            ObjectInfo = new();
            ExploreTargetUid = 0;
            ExploreTargetId = 0;
            PlayerId = 0;
        }

        public ExploreTargetInfo(long exploreTargetUid, int exploreTargetId, GameObjectInfo objectInfo)
        {
            ObjectInfo = objectInfo;
            ExploreTargetUid = exploreTargetUid;
            ExploreTargetId = exploreTargetId;
            PlayerId = 0;
        }

        public string GetLockKey()
        {
            return $"explore_target_lock_{ExploreTargetUid}";
        }

        public static string GetLockKey(long exploreTargetUid)
        {
            return $"explore_target_lock_{exploreTargetUid}";
        }

        public static async Task<IRedLock> Lock(RedLockFactory redlock, long exploreTargetUid)
        {
            return await redlock.CreateLockAsync(ExploreTargetInfo.GetLockKey(exploreTargetUid), Config.LOCK_TTL);
        }

        public async Task Save()
        {
            await ObjectInfo.Save();
            await CacheHelper.Instance.HashSetAsync(ExploreTargetInfo.HASH_KEY, ExploreTargetUid, MessagePackSerializer.Serialize(this));
        }

        public static async Task<ExploreTargetInfo?> Load(long exploreTargetUid)
        {
            var serializedData = await CacheHelper.Instance.HashGetAsync(ExploreTargetInfo.HASH_KEY, exploreTargetUid);
            if (serializedData.IsNull)
            {
                return null;
            }

            var exploreTargetInfo = MessagePackSerializer.Deserialize<ExploreTargetInfo?>(serializedData);
            if (exploreTargetInfo == null)
            {
                return null;
            }

            var objectInfo = await GameObjectInfo.Load(ObjectType.EXPLORETARGET, exploreTargetUid);
            if (objectInfo == null)
            {
                return null;
            }

            exploreTargetInfo.ObjectInfo = objectInfo;
            return exploreTargetInfo;
        }

        public async Task Delete()
        {
            await CacheHelper.Instance.HashDeleteAsync(ExploreTargetInfo.HASH_KEY, ExploreTargetUid);
        }

        public static async Task Delete(long ExploreTargetUid)
        {
            await CacheHelper.Instance.HashDeleteAsync(ExploreTargetInfo.HASH_KEY, ExploreTargetUid);
        }
    }
}
