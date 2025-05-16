// ReSharper disable All

using System;
using MessagePack;

namespace network.common.data.models
{
    [MessagePackObject]
    public partial class GameObjectInfo : IMessagePackObject
    {
        [IgnoreMember] public const string HashKey = "GameObjectInfo";

        // 기본 생성자는 MessagePack에서 사용되므로 제거하지 않을 것
        public GameObjectInfo()
        {
            ObjectType = ObjectType.NONE;
            ObjectId = 0;
            CurrentCell = new Cell(0, 0);
            TargetCell = new Cell(0, 0);
            MoveTimestamp = default;
            DebuffTimestamp = default;
            IsFlip = false;
        }

        public GameObjectInfo(long objectId)
        {
            ObjectType = ObjectType.NONE;
            ObjectId = objectId;
            CurrentCell = new Cell(0, 0);
            TargetCell = new Cell(0, 0);
            MoveTimestamp = default;
            DebuffTimestamp = default;
            IsFlip = false;
        }

        public GameObjectInfo(ObjectType objectType, long objectId, MapId mapId, long mapSubId, Cell cell,
            bool isFlip = false)
        {
            ObjectType = objectType;
            ObjectId = objectId;
            CurrentCell = Cell.Clone(cell);
            TargetCell = Cell.Clone(cell);
            MapId = mapId;
            MapSubId = mapSubId;
            MoveTimestamp = DateTime.MinValue;
            DebuffTimestamp = DateTime.MinValue;
            IsFlip = isFlip;
        }

        [Key("objectType")] public ObjectType ObjectType { get; set; }

        [Key("objectId")] public long ObjectId { get; set; }

        [Key("mapId")] public MapId MapId { get; set; }

        [Key("mapSubId")] public long MapSubId { get; set; }

        [Key("currentCell")] public Cell CurrentCell { get; set; }

        [Key("targetCell")] public Cell TargetCell { get; set; }

        [Key("moveTimestamp")] public DateTime MoveTimestamp { get; set; }

        [Key("debuffTimestamp")] public DateTime DebuffTimestamp { get; set; }

        [Key("isFlip")] public bool IsFlip { get; set; }

        public string GetGameObjectKey()
        {
            return $"{(int)ObjectType}_{ObjectId}";
        }

        public static string MakeObjectKey(ObjectType type, long objectId)
        {
            return $"{(int)type}_{objectId}";
        }

        public void SetFlip(DirectionType direction)
        {
            switch (direction)
            {
                case DirectionType.NONE:
                    break;
                
                case DirectionType.TOP_LEFT:
                case DirectionType.BOTTOM_LEFT:
                case DirectionType.LEFT: 
                case DirectionType.TOP:
                    IsFlip = false;
                    break;

                default:
                    IsFlip = true;
                    break;
            }
        }
    }
}