using System.Diagnostics.CodeAnalysis;
using MessagePack;
using network.helpers;
using StackExchange.Redis;

namespace network.common;

[MessagePackObject]
[SuppressMessage("ReSharper", "MemberCanBePrivate.Global")]
[SuppressMessage("ReSharper", "UnusedMember.Global")]
[SuppressMessage("ReSharper", "AutoPropertyCanBeMadeGetOnly.Global")]
[SuppressMessage("ReSharper", "UnusedAutoPropertyAccessor.Global")]
[SuppressMessage("ReSharper", "NonReadonlyMemberInGetHashCode")]
[SuppressMessage("ReSharper", "PropertyCanBeMadeInitOnly.Global")]
public class GameObjectInfo : IMessagePackObject
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
        var serializedData = await CacheHelper.Instance.HashGetAsync(HashKey, MakeObjectKey(type, objectId));
        if (serializedData.IsNull) return null;

        return MessagePackSerializer.Deserialize<GameObjectInfo?>(serializedData);
    }

    public static async Task<List<GameObjectInfo>> LoadAll(RedisValue[] objectKeys)
    {
        var hashEntries = await CacheHelper.Instance.HashGetAsync(HashKey, objectKeys);
        var hashStrings = hashEntries
            .Where(entry => entry != RedisValue.Null)
            .Select(entry => entry)
            .ToList();

        return hashStrings.Select(hashString => MessagePackSerializer.Deserialize<GameObjectInfo>(hashString)).ToList();
    }

    public static async Task Delete(string hashField)
    {
        await CacheHelper.Instance.HashDeleteAsync(HashKey, hashField);
    }

    public static async Task<bool> Exist(string hashField)
    {
        return await CacheHelper.Instance.HashExistsAsync(HashKey, hashField);
    }

    public async Task Save()
    {
        await CacheHelper.Instance.HashSetAsync(HashKey, GetGameObjectKey(), MessagePackSerializer.Serialize(this));
    }

    public async Task Delete()
    {
        await CacheHelper.Instance.HashDeleteAsync(HashKey, GetGameObjectKey());
    }
}