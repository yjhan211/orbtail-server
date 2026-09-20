#pragma warning disable CS8618
using MessagePack;

namespace network.common.data.models
{
    [MessagePackObject]
    public class GroundItemInfo
    {
        // 아이템 패킷은 월드 좌표를 전송하고, 셀과 구역은 공통 맵에서 계산한다.
        [IgnoreMember] public GameObjectInfo ObjectInfo { get; } = new GameObjectInfo { ObjectType = ObjectType.ITEM, MapId = Config.SWARM_MATCH_MAP };
        [Key("groundItemUid")] public long GroundItemUid { get => ObjectInfo.ObjectId; set => ObjectInfo.ObjectId = value; }
        [Key("itemId")] public int ItemId { get; set; }
        [IgnoreMember] public int AreaType => (int)GameMapData.GetCurrentArea(ObjectInfo.MapId, ObjectInfo.Cell);
        [Key("positionX")]
        public float PositionX
        {
            get => ObjectInfo.Position.X;
            set
            {
                ObjectInfo.Position.X = value;
                ObjectInfo.Cell = network.common.data.MapCoordinateConverter.WorldToCell(ObjectInfo.MapId, ObjectInfo.Position);
            }
        }
        [Key("positionY")]
        public float PositionY
        {
            get => ObjectInfo.Position.Y;
            set
            {
                ObjectInfo.Position.Y = value;
                ObjectInfo.Cell = network.common.data.MapCoordinateConverter.WorldToCell(ObjectInfo.MapId, ObjectInfo.Position);
            }
        }
        [Key("spawnOriginX")] public float SpawnOriginX { get; set; }
        [Key("spawnOriginY")] public float SpawnOriginY { get; set; }
        [Key("sourcePlayerId")] public long SourcePlayerId { get; set; }
        [Key("isLanding")] public bool IsLanding { get; set; }
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
