using System.Diagnostics.CodeAnalysis;
using MessagePack;
using network.helpers;

namespace network.common.models;

[MessagePackObject]
[SuppressMessage("ReSharper", "MemberCanBePrivate.Global")]
[SuppressMessage("ReSharper", "UnusedMember.Global")]
[SuppressMessage("ReSharper", "AutoPropertyCanBeMadeGetOnly.Global")]
[SuppressMessage("ReSharper", "UnusedAutoPropertyAccessor.Global")]
public class CampInfo : IMessagePackObject
{
    [IgnoreMember] public const string HashKey = "CampInfo";

    public CampInfo()
    {
        ObjectInfo = new GameObjectInfo();
        PlayerId = new long();
        PlayerName = "";
        ItemInfo = new ItemInfo();
        CellDict = new Dictionary<long, (ItemInfo, int)>();
    }

    public CampInfo(long playerId, string playerName, GameObjectInfo playerObjectInfo, ItemInfo itemInfo, Cell cell)
    {
        ObjectInfo = new GameObjectInfo(
            ObjectType.CAMP,
            playerId,
            playerObjectInfo.MapId,
            playerObjectInfo.MapSubId,
            Cell.Clone(cell),
            playerObjectInfo.IsFlip
        );

        PlayerId = playerId;
        PlayerName = playerName;
        ItemInfo = itemInfo;
        CellDict = new Dictionary<long, (ItemInfo, int)>();
    }

    [IgnoreMember] public GameObjectInfo ObjectInfo { get; set; }

    [Key("playerId")] public long PlayerId { get; set; }

    [Key("playerName")] public string PlayerName { get; set; }

    [Key("itemInfo")] public ItemInfo ItemInfo { get; set; }

    [Key("cellDict")] public Dictionary<long, (ItemInfo, int)> CellDict { get; set; }

    public async Task Save()
    {
        await ObjectInfo.Save();
        await CacheHelper.Instance.HashSetAsync(HashKey, PlayerId, MessagePackSerializer.Serialize(this));
    }

    public static async Task<CampInfo?> Load(long playerId)
    {
        var serializedData = await CacheHelper.Instance.HashGetAsync(HashKey, playerId);
        if (serializedData.IsNull) return null;

        var campInfo = MessagePackSerializer.Deserialize<CampInfo?>(serializedData);
        if (campInfo == null) return null;

        var objectInfo = await GameObjectInfo.Load(ObjectType.CAMP, playerId);
        if (objectInfo == null) return null;

        campInfo.ObjectInfo = objectInfo;
        return campInfo;
    }

    public async Task Delete()
    {
        await CacheHelper.Instance.HashDeleteAsync(HashKey, PlayerId);
    }

    public static async Task Delete(long playerId)
    {
        await CacheHelper.Instance.HashDeleteAsync(HashKey, playerId);
    }
}