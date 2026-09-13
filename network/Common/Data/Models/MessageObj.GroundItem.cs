#pragma warning disable CS8618
using System.Collections.Generic;
using MessagePack;

namespace network.common.data.models
{
    [MessagePackObject]
    public class GroundItemInfo
    {
        // ID·구역·위치는 공통 공간 정보에 보관한다. 맵·셀·속도·회전은 이 패킷의 동기화 대상이 아니다.
        [IgnoreMember] public GameObjectInfo ObjectInfo { get; } = new GameObjectInfo { ObjectType = ObjectType.ITEM };
        [Key("groundItemUid")] public long GroundItemUid { get => ObjectInfo.ObjectId; set => ObjectInfo.ObjectId = value; }
        [Key("itemId")] public int ItemId { get; set; }
        [Key("areaType")] public int AreaType { get => (int)ObjectInfo.Area; set => ObjectInfo.Area = (network.common.AreaType)value; }
        [Key("positionX")] public float PositionX { get => ObjectInfo.Position.X; set => ObjectInfo.Position.X = value; }
        [Key("positionY")] public float PositionY { get => ObjectInfo.Position.Y; set => ObjectInfo.Position.Y = value; }
        [Key("spawnOriginX")] public float SpawnOriginX { get; set; }
        [Key("spawnOriginY")] public float SpawnOriginY { get; set; }
        [Key("sourcePlayerId")] public long SourcePlayerId { get; set; }
    }

    [MessagePackObject]
    public class G_TO_C_GROUND_ITEM_SNAPSHOT : IMessagePackObject
    {
        [Key("areaType")] public int AreaType { get; set; }
        [Key("items")] public List<GroundItemInfo> Items { get; set; } = new();
    }

    [MessagePackObject]
    public class G_TO_C_GROUND_ITEM_SPAWN : IMessagePackObject
    {
        [Key("areaType")] public int AreaType { get; set; }
        [Key("items")] public List<GroundItemInfo> Items { get; set; } = new();
    }

    [MessagePackObject]
    public class G_TO_C_GROUND_ITEM_REMOVED : IMessagePackObject
    {
        [Key("groundItemUid")] public long GroundItemUid { get; set; }
        [Key("pickerPlayerId")] public long PickerPlayerId { get; set; }
        [Key("autoUsed")] public bool AutoUsed { get; set; }
    }

    [MessagePackObject]
    public class G_TO_C_GROUND_ITEM_PICKUP_RESULT : IMessagePackObject
    {
        [Key("groundItemUid")] public long GroundItemUid { get; set; }
        [Key("itemId")] public int ItemId { get; set; }
        [Key("success")] public bool Success { get; set; }
        [Key("autoUsed")] public bool AutoUsed { get; set; }
        [Key("errorCode")] public ErrorCode ErrorCode { get; set; }
    }
}
