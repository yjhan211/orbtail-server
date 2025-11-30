// ReSharper disable All

using System;
using MessagePack;

namespace network.common.data.models
{
    [MessagePackObject]
    public partial class ExploreTargetInfo : IMessagePackObject
    {
        [IgnoreMember] public const string HashKey = "ExploreTargetInfo";

        // 이거 없애면 안됨 MessagePack에서 씀
        public ExploreTargetInfo()
        {
            ObjectInfo = new GameObjectInfo();
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

        [IgnoreMember] public GameObjectInfo ObjectInfo { get; set; }

        [Key("exploreTargetUid")] public long ExploreTargetUid { get; set; }

        [Key("exploreTargetId")] public int ExploreTargetId { get; set; }

        [Key("playerId")] public long PlayerId { get; set; } // 점유중인 플레이어 아이디

        [Key("endTimestamp")] public DateTime EndTimestamp { get; set; } // 점유 끝나는 시간
    }
}
