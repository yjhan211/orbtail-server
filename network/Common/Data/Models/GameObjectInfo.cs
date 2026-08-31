// ReSharper disable All

using System;
using MessagePack;
using network.common.data;

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
            Cell = new Cell(0, 0);
            Position = new Vector3f(0, 0, 0);
            Velocity = new Vector3f(0, 0, 0);
            Rotation = 0f;
            MoveTimestamp = default;
            DebuffTimestamp = default;
            IsFlip = false;
        }

        public GameObjectInfo(long objectId)
        {
            ObjectType = ObjectType.NONE;
            ObjectId = objectId;
            Cell = new Cell(0, 0);
            Position = new Vector3f(0, 0, 0);
            Velocity = new Vector3f(0, 0, 0);
            Rotation = 0f;
            MoveTimestamp = default;
            DebuffTimestamp = default;
            IsFlip = false;
        }

        public GameObjectInfo(ObjectType objectType, long objectId, MapId mapId, long mapSubId, Cell cell,
            bool isFlip = false)
        {
            cell ??= new Cell(0, 0);
            ObjectType = objectType;
            ObjectId = objectId;
            Cell = Cell.Clone(cell);
            Position = new Vector3f(cell.X, cell.Y, 0);
            Velocity = new Vector3f(0, 0, 0);
            Rotation = 0f;
            MapId = mapId;
            MapSubId = mapSubId;
            MoveTimestamp = DateTime.MinValue;
            DebuffTimestamp = DateTime.MinValue;
            IsFlip = isFlip;
        }

        [Key("objectType")]
        public ObjectType ObjectType { get; set; }

        [Key("objectId")]
        public long ObjectId { get; set; }

        [Key("mapId")]
        public MapId MapId { get; set; }

        [Key("mapSubId")]
        public long MapSubId { get; set; }

        // 현재 위치한 셀 (Position에서 자동 계산)
        [Key("cell")]
        public Cell Cell { get; set; }

        // 자유 이동 필드
        [Key("position")]
        public Vector3f Position { get; set; }

        [Key("velocity")]
        public Vector3f Velocity { get; set; }

        [Key("rotation")]
        public float Rotation { get; set; }

        [Key("moveTimestamp")]
        public DateTime MoveTimestamp { get; set; }

        [Key("debuffTimestamp")]
        public DateTime DebuffTimestamp { get; set; }

        [Key("isFlip")]
        public bool IsFlip { get; set; }

        // 하위 호환성을 위한 속성 (Deprecated)
        [IgnoreMember]
        public Cell CurrentCell
        {
            get => Cell;
            set => Cell = value;
        }

        [IgnoreMember]
        public Cell TargetCell
        {
            get => Cell;
            set => Cell = value;
        }

        public string GetGameObjectKey()
        {
            return $"{(int)ObjectType}_{ObjectId}";
        }

        public static string MakeObjectKey(ObjectType type, long objectId)
        {
            return $"{(int)type}_{objectId}";
        }
    }
}
