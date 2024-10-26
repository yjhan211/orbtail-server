using MessagePack;
using StackExchange.Redis;
using network.helpers;

namespace network.common
{
    [MessagePackObject]
    public class GameObjectInfo : IMessagePackObject
    {
        [IgnoreMember]
        public const string HASH_KEY = "GameObjectInfo";

        [Key("objectType")]
        public ObjectType ObjectType { get; set; }

        [Key("objectId")]
        public long ObjectId { get; set; }

        [Key("mapId")]
        public MapID MapId { get; set; }

        [Key("mapSubId")]
        public long MapSubId { get; set; }

        [Key("currentCell")]
        public Cell CurrentCell { get; set; }

        [Key("targetCell")]
        public Cell TargetCell { get; set; }

        [Key("moveTimestamp")]
        public DateTime MoveTimestamp { get; set; }

        [Key("debuffTimestamp")]
        public DateTime DebuffTimestamp { get; set; }

        [Key("isFlip")]
        public bool IsFlip { get; set; }

        public string GetGameObjectKey() => $"{(int)ObjectType}_{ObjectId}";

        public static string MakeObjectKey(ObjectType type, long objectId)
        {
            return $"{(int)type}_{objectId}";
        }

        // 이거 없애면 안됨 MessagePack에서 씀
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

        public GameObjectInfo(ObjectType objectType, long objectId, MapID mapId, long mapSubId, Cell cell, bool isFlip = false)
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

        public void SetFlip(DirectionType direction)
        {
            switch (direction)
            {
                case DirectionType.TOP_LEFT:
                case DirectionType.BOTTOM_LEFT:
                    IsFlip = true;
                    break;

                default:
                    IsFlip = false;
                    break;
            }
        }

        public static async Task<GameObjectInfo?> Load(ObjectType type, long objectId)
        {
            var serializedData = await CacheHelper.Instance.HashGetAsync(GameObjectInfo.HASH_KEY, GameObjectInfo.MakeObjectKey(type, objectId));
            if (serializedData.IsNull)
            {
                return null;
            }

            return MessagePackSerializer.Deserialize<GameObjectInfo?>(serializedData);
        }

        public static async Task<List<GameObjectInfo>> LoadAll(RedisValue[] objectKeys)
        {
            var hashEntries = await CacheHelper.Instance.HashGetAsync(GameObjectInfo.HASH_KEY, objectKeys);
            if (hashEntries == null)
            {
                return new();
            }

            List<RedisValue> hashStrings = hashEntries
                .Where(entry => entry != RedisValue.Null)
                .Select(entry => entry)
                .ToList();

            List<GameObjectInfo> result = new();
            foreach (var hashString in hashStrings)
            {
                var gameObjectInfo = MessagePackSerializer.Deserialize<GameObjectInfo>(hashString);
                if (gameObjectInfo == null)
                {
                    continue;
                }

                result.Add(gameObjectInfo);
            }

            return result;
        }

        public static async Task Delete(string hashField)
        {
            await CacheHelper.Instance.HashDeleteAsync(GameObjectInfo.HASH_KEY, hashField);
        }

        public static async Task<bool> Exist(string hashField)
        {
            return await CacheHelper.Instance.HashExistsAsync(GameObjectInfo.HASH_KEY, hashField);
        }

        public async Task Save()
        {
            await CacheHelper.Instance.HashSetAsync(GameObjectInfo.HASH_KEY, this.GetGameObjectKey(), MessagePackSerializer.Serialize(this));
        }

        public async Task Delete()
        {
            await CacheHelper.Instance.HashDeleteAsync(GameObjectInfo.HASH_KEY, this.GetGameObjectKey());
        }
    }
}
