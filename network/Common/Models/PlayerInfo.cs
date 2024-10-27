using System.Diagnostics.CodeAnalysis;
using MessagePack;
using network.common.data;
using network.helpers;
using RedLockNet;
using RedLockNet.SERedis;
using StackExchange.Redis;

namespace network.common.models;

[MessagePackObject]
[SuppressMessage("ReSharper", "MemberCanBePrivate.Global")]
[SuppressMessage("ReSharper", "UnusedMember.Global")]
[SuppressMessage("ReSharper", "AutoPropertyCanBeMadeGetOnly.Global")]
[SuppressMessage("ReSharper", "UnusedAutoPropertyAccessor.Global")]
[SuppressMessage("ReSharper", "NonReadonlyMemberInGetHashCode")]
[SuppressMessage("ReSharper", "PropertyCanBeMadeInitOnly.Global")]
public class PlayerInfo : IMessagePackObject
{
    [IgnoreMember] public const string HashKey = "PlayerInfo";

    // 이거 없애면 안됨 MessagePack에서 씀
    public PlayerInfo()
    {
        PlayerId = 0;
        Name = "";
        Grade = PlayerGrade.NONE;
        WearItemIdList = [];
        State = PlayerState.NONE;
        ObjectInfo = new GameObjectInfo();
        JobInfo = new JobInfo();
        InventoryInfo = new InventoryInfo();
        LabId = 0;
        LabName = "";
        Gold = 0;
        TutorialIndex = 0;
    }

    public PlayerInfo(long playerId, bool isDummy)
    {
        var name = isDummy ? $"더미{playerId}" : $"플레이어{playerId}";

        var initCell = isDummy ? CommonMapData.GetRandomCell() : GameRuleData.StartPosition;
        PlayerId = playerId;
        Name = name;
        Grade = PlayerGrade.COMMONER;
        WearItemIdList = [];
        State = PlayerState.NONE;
        ObjectInfo = new GameObjectInfo(ObjectType.PLAYER, PlayerId, MapId.LIBRARY, 1, initCell);
        JobInfo = new JobInfo(PlayerId);
        InventoryInfo = new InventoryInfo(InventoryOwnerType.PLAYER, PlayerId);
        LabId = 0;
        LabName = "";
        Gold = 1000;
        TutorialIndex = 0;
    }

    [IgnoreMember] public GameObjectInfo ObjectInfo { get; set; }

    [IgnoreMember] public JobInfo JobInfo { get; set; }

    [IgnoreMember] public InventoryInfo InventoryInfo { get; set; }

    [Key("playerId")] public long PlayerId { get; set; }

    [Key("name")] public string Name { get; set; }

    [Key("grade")] public PlayerGrade Grade { get; set; }

    // ReSharper disable once CollectionNeverUpdated.Global
    [Key("wearItemIdList")] public List<int> WearItemIdList { get; set; }

    [Key("state")] public PlayerState State { get; set; }

    [Key("labId")] public long LabId { get; set; }

    [Key("labName")] public string LabName { get; set; }

    [Key("gold")] public long Gold { get; set; }

    [Key("tutorialIndex")] public int TutorialIndex { get; set; }

    public string GetLockKey()
    {
        return $"player_lock_{PlayerId}";
    }

    public static string GetLockKey(long playerId)
    {
        return $"player_lock_{playerId}";
    }

    public void WearItem(long itemUid)
    {
        // if (!InventoryInfo.ItemDict.TryGetValue(itemUid, out var targetItem))
        //     throw new Exception($"Item with uid {itemUid} not found");
        //
        // if (!GameDataHelper.IsWearableItem(targetItem.ItemId))
        //     throw new Exception($"not wearable item {targetItem.ItemId}");
        //
        // if (!GameDataHelper.IsWearableJobInfo(targetItem.ItemId, JobInfo.JobStatDict))
        //     throw new Exception($"not wearable job type. {targetItem.ItemId}");
        //
        // if (targetItem.IsWear)
        // {
        //     // 착용 해제
        //     WearItemIdList.Remove(targetItem.ItemId);
        //     targetItem.IsWear = false;
        //     return;
        // }
        //
        // // 같은 종류의 아이템 인덱스 찾기
        // var lastWearItem = InventoryInfo.ItemDict.Values.FirstOrDefault(
        //     item => GameDataHelper.IsSameTypeItem(item.ItemId, targetItem.ItemId) && item.IsWear
        // );
        //
        // if (lastWearItem != null)
        // {
        //     // 같은 종류 아이템 착용 해제
        //     lastWearItem.IsWear = false;
        //     WearItemIdList.Remove(lastWearItem.ItemId);
        // }
        //
        // // 새로운 아이템 착용
        // InventoryInfo.ItemDict[itemUid].IsWear = true;
        // WearItemIdList.Add(targetItem.ItemId);
    }

    public void UseItem(long itemUid, int count = 1)
    {
        // if (!InventoryInfo.ItemDict.TryGetValue(itemUid, out var targetItem))
        //     throw new Exception($"Item with uid {itemUid} not found");
        //
        // if (targetItem.Count <= 0) throw new Exception($"Item {itemUid} count invalid");
        //
        // if (!GameDataHelper.IsUseableItem(targetItem.ItemId))
        //     throw new Exception($"not usable item {targetItem.ItemId}");

        // TODO
    }

    public static async Task<IRedLock> Lock(RedLockFactory redLock, long playerId)
    {
        return await redLock.CreateLockAsync(GetLockKey(playerId), Config.LOCK_TTL);
    }

    public async Task<IRedLock> Lock(RedLockFactory redLock)
    {
        return await redLock.CreateLockAsync(GetLockKey(), Config.LOCK_TTL);
    }

    public async Task Save()
    {
        // 조회가 빈번해서 메모리에 올려뒀음. 따로 Save함
        // await GameObjectInfoController.Save(cache_helper, player_info.object_info);

        await JobInfo.Save();
        await InventoryInfo.Save();
        await CacheHelper.Instance.HashSetAsync(HashKey, PlayerId, MessagePackSerializer.Serialize(this));
    }

    public static async Task<PlayerInfo?> Load(long playerId)
    {
        var serialized = await CacheHelper.Instance.HashGetAsync(HashKey, playerId);
        if (serialized == RedisValue.Null) return null;

        var playerInfo = MessagePackSerializer.Deserialize<PlayerInfo>(serialized);

        playerInfo.ObjectInfo = await GameObjectInfo.Load(ObjectType.PLAYER, playerId) ?? new GameObjectInfo(playerId);
        playerInfo.JobInfo = await JobInfo.Load(playerId) ?? new JobInfo(playerId);
        playerInfo.InventoryInfo = await InventoryInfo.Load(InventoryOwnerType.PLAYER, playerId) ??
                                   new InventoryInfo(InventoryOwnerType.PLAYER, playerId);

        return playerInfo;
    }

    public static async Task<List<PlayerInfo>> LoadAll(RedisValue[] objectKeys)
    {
        var hashEntries = await CacheHelper.Instance.HashGetAsync(HashKey, objectKeys);
        var hashStrings = hashEntries
            .Where(entry => entry != RedisValue.Null)
            .Select(entry => entry)
            .ToList();

        return hashStrings.Select(hashString => MessagePackSerializer.Deserialize<PlayerInfo>(hashString)).ToList();
    }

    public async Task Delete(PlayerInfo playerInfo)
    {
        await playerInfo.ObjectInfo.Delete();
        await CacheHelper.Instance.HashDeleteAsync(HashKey, playerInfo.PlayerId);
    }

    public static async Task Delete(long playerId)
    {
        var objectField = GameObjectInfo.MakeObjectKey(ObjectType.PLAYER, playerId);

        await GameObjectInfo.Delete(objectField);
        await JobInfo.Delete(playerId);
        await CacheHelper.Instance.HashDeleteAsync(HashKey, playerId);
    }
}