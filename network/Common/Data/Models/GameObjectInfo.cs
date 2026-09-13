// ReSharper disable All

using MessagePack;
using network.common.data;

namespace network.common.data.models
{
    /// <summary>게임 공간에 놓인 객체의 현재 맵·위치·속도·회전. 플레이어·아이템·몬스터가 공유하며 종류별 상태는 싣지 않는다.</summary>
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
        }

        public GameObjectInfo(long objectId)
        {
            ObjectType = ObjectType.NONE;
            ObjectId = objectId;
            Cell = new Cell(0, 0);
            Position = new Vector3f(0, 0, 0);
            Velocity = new Vector3f(0, 0, 0);
            Rotation = 0f;
        }

        public GameObjectInfo(ObjectType objectType, long objectId, MapId mapId, Cell cell)
        {
            cell ??= new Cell(0, 0);
            ObjectType = objectType;
            ObjectId = objectId;
            Cell = Cell.Clone(cell);
            Position = new Vector3f(cell.X, cell.Y, 0);
            Velocity = new Vector3f(0, 0, 0);
            Rotation = 0f;
            MapId = mapId;
            Area = GameMapData.GetCurrentArea(mapId, cell);
        }

        [Key("objectType")]
        public ObjectType ObjectType { get; set; }

        [Key("objectId")]
        public long ObjectId { get; set; }

        [Key("mapId")]
        public MapId MapId { get; set; }

        [Key("area")]
        public AreaType Area { get; set; }

        [Key("cell")]
        public Cell Cell { get; set; }

        [Key("position")]
        public Vector3f Position { get; set; }

        [Key("velocity")]
        public Vector3f Velocity { get; set; }

        [Key("rotation")]
        public float Rotation { get; set; }

        public GameObjectInfo Clone()
        {
            return new GameObjectInfo
            {
                ObjectType = ObjectType,
                ObjectId = ObjectId,
                MapId = MapId,
                Area = Area,
                Cell = Cell.Clone(Cell),
                Position = new Vector3f(Position.X, Position.Y, Position.Z),
                Velocity = new Vector3f(Velocity.X, Velocity.Y, Velocity.Z),
                Rotation = Rotation
            };
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
