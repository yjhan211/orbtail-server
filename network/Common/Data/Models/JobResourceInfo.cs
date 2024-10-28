// ReSharper disable All

using System;
using MessagePack;

namespace network.common.data.models
{
    [MessagePackObject]
    public partial class JobResourceInfo : IMessagePackObject
    {
        [IgnoreMember] public const string HashKey = "JobResourceInfo";

        // 이거 없애면 안됨 MessagePack에서 씀
        public JobResourceInfo()
        {
            ObjectInfo = new GameObjectInfo();
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

        [IgnoreMember] public GameObjectInfo ObjectInfo { get; set; }

        [Key("resourceUid")] public long ResourceUid { get; set; } // 유니크 아이디

        [Key("resourceId")] public int ResourceId { get; set; } // 리소스 종류. 네모난 돌, 동그란 돌, 잡초..

        [Key("playerId")] public long PlayerId { get; set; } // 점유중인 플레이어 아이디

        [Key("endTimestamp")] public DateTime EndTimestamp { get; set; } // 점유 끝나는 시간

        public string GetLockKey()
        {
            return $"job_resource_lock_{ResourceUid}";
        }

        public static string GetLockKey(long resourceUid)
        {
            return $"job_resource_lock_{resourceUid}";
        }
    }
}