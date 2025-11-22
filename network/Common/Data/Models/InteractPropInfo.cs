// ReSharper disable All

using System;
using System.Collections.Generic;
using MessagePack;

namespace network.common.data.models
{
    [MessagePackObject]
    public class InteractPropInfo : IMessagePackObject
    {
        public InteractPropInfo()
        {
            ObjectInfo = new GameObjectInfo();
            InteractPropUid = 0;
            InteractPropId = 0;
            InteractPlayers = new List<long>();
        }

        public InteractPropInfo(GameObjectInfo objectInfo, long itemUid)
        {
            ObjectInfo = objectInfo;
            InteractPropUid = itemUid;
            InteractPlayers = new List<long>();
        }

        [IgnoreMember] public GameObjectInfo ObjectInfo { get; set; }
        [Key("interactPropUid")] public long InteractPropUid { get; set; }
        [Key("interactPropId")] public int InteractPropId { get; set; }
        [Key("interactPlayers")] public List<long> InteractPlayers { get; set; }
    }
}