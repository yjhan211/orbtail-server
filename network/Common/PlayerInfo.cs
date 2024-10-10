using MessagePack;
using RedLockNet;
using RedLockNet.SERedis;
using StackExchange.Redis;
using network.helpers;

namespace network.common
{
    [MessagePackObject]
    public class PlayerInfo : IMessagePackObject
    {
        [IgnoreMember]
        public GameObjectInfo ObjectInfo { get; set; }

        [IgnoreMember]
        public JobInfo JobInfo { get; set; }

        [IgnoreMember]
        public InventoryInfo InventoryInfo { get; set; }

        [IgnoreMember]
        public const string HASH_KEY = "PlayerInfo";

        [Key("playerId")]
        public long PlayerId { get; set; }

        [Key("name")]
        public string Name { get; set; }

        [Key("grade")]
        public PlayerGrade Grade { get; set; }

        [Key("wearItemIdList")]
        public List<int> WearItemIdList { get; set; }

        [Key("state")]
        public PlayerState State { get; set; }

        [Key("labId")]
        public long LabId { get; set; }

        [Key("labName")]
        public string LabName { get; set; }

        [Key("gold")]
        public long Gold { get; set; }

        [Key("tutorialIndex")]
        public int TutorialIndex { get; set; }

        // 이거 없애면 안됨 MessagePack에서 씀
        public PlayerInfo()
        {
            PlayerId = 0;
            Name = "";
            Grade = PlayerGrade.NONE;
            WearItemIdList = new();
            State = PlayerState.NONE;
            ObjectInfo = new();
            JobInfo = new();
            InventoryInfo = new();
            LabId = 0;
            LabName = "";
            Gold = 0;
            TutorialIndex = 0;
        }

        public PlayerInfo(long playerId, bool isDummy)
        {
            string name = isDummy ? $"더미{playerId}" : $"플레이어{playerId}";
            Cell initCell = isDummy ? MapHelper.GetRandomCell() : Config.START_POSITION;

            PlayerId = playerId;
            Name = name;
            Grade = PlayerGrade.COMMONER;
            WearItemIdList = new();
            State = PlayerState.NONE;
            ObjectInfo = new(ObjectType.PLAYER, PlayerId, MapID.CAMPUS_1, 0, initCell);
            JobInfo = new(PlayerId);
            InventoryInfo = new(InventoryOwnerType.PLAYER, PlayerId);
            LabId = 0;
            LabName = "";
            Gold = 1000;
            TutorialIndex = 0;
        }

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
            if (!InventoryInfo.ItemDict.TryGetValue(itemUid, out var targetItem))
            {
                throw new Exception($"Item with uid {itemUid} not found");
            }

            if (!GameDesignData.IsWearableItem(targetItem.ItemId))
            {
                throw new Exception($"not wearable item {targetItem.ItemId}");
            }

            if (!GameDesignData.IsWearableJobInfo(targetItem.ItemId, JobInfo.JobStatDict))
            {
                throw new Exception($"not wearable job type. {targetItem.ItemId}");
            }

            if (targetItem.IsWear)
            {
                // 착용 해제
                WearItemIdList.Remove(targetItem.ItemId);
                targetItem.IsWear = false;
                return;
            }

            // 같은 종류의 아이템 인덱스 찾기
            var lastWearItem = InventoryInfo.ItemDict.Values.FirstOrDefault(
                item => GameDesignData.IsSameTypeItem(item.ItemId, targetItem.ItemId) && item.IsWear
            );

            if (lastWearItem != null)
            {
                // 같은 종류 아이템 착용 해제
                lastWearItem.IsWear = false;
                WearItemIdList.Remove(lastWearItem.ItemId);
            }

            // 새로운 아이템 착용
            InventoryInfo.ItemDict[itemUid].IsWear = true;
            WearItemIdList.Add(targetItem.ItemId);
        }

        public void UseItem(long itemUid, int count = 1)
        {
            if (!InventoryInfo.ItemDict.TryGetValue(itemUid, out var targetItem))
            {
                throw new Exception($"Item with uid {itemUid} not found");
            }

            if (targetItem.Count <= 0)
            {
                throw new Exception($"Item {itemUid} count invalid");
            }

            if (!GameDesignData.IsUseableItem(targetItem.ItemId))
            {
                throw new Exception($"not useable item {targetItem.ItemId}");
            }

            // TODO
        }

        public static async Task<IRedLock> Lock(RedLockFactory redlock, long playerId)
        {
            return await redlock.CreateLockAsync(PlayerInfo.GetLockKey(playerId), Config.LOCK_TTL);
        }

        public async Task<IRedLock> Lock(RedLockFactory redlock)
        {
            return await redlock.CreateLockAsync(this.GetLockKey(), Config.LOCK_TTL);
        }

        public async Task Save()
        {
            // 조회가 빈번해서 메모리에 올려뒀음. 따로 Save함
            // await GameObjectInfoController.Save(cache_helper, player_info.object_info);

            await JobInfo.Save();
            await InventoryInfo.Save();
            await CacheHelper.Instance.HashSetAsync(PlayerInfo.HASH_KEY, PlayerId, MessagePackSerializer.Serialize(this));
        }

        public static async Task<PlayerInfo?> Load(long playerId)
        {
            var serialized = await CacheHelper.Instance.HashGetAsync(PlayerInfo.HASH_KEY, playerId);
            if (serialized == RedisValue.Null)
            {
                return null;
            }

            var playerInfo = MessagePackSerializer.Deserialize<PlayerInfo>(serialized);
            if (playerInfo == null)
            {
                return null;
            }

            playerInfo.ObjectInfo = await GameObjectInfo.Load(ObjectType.PLAYER, playerId) ?? new GameObjectInfo(playerId);
            playerInfo.JobInfo = await JobInfo.Load(playerId) ?? new JobInfo(playerId);
            playerInfo.InventoryInfo = await InventoryInfo.Load(InventoryOwnerType.PLAYER, playerId) ?? new InventoryInfo(InventoryOwnerType.PLAYER, playerId);

            return playerInfo;
        }

        public static async Task<List<PlayerInfo>> LoadAll(RedisValue[] objectKeys)
        {
            var hashEntries = await CacheHelper.Instance.HashGetAsync(PlayerInfo.HASH_KEY, objectKeys);
            if (hashEntries == null)
            {
                return new();
            }

            List<RedisValue> hashStrings = hashEntries
                .Where(entry => entry != RedisValue.Null)
                .Select(entry => entry)
                .ToList();

            List<PlayerInfo> result = new();
            foreach (var hashString in hashStrings)
            {
                var player_info = MessagePackSerializer.Deserialize<PlayerInfo>(hashString);
                if (player_info == null)
                {
                    continue;
                }
                result.Add(player_info);
            }

            return result;
        }

        public async Task Delete(PlayerInfo playerInfo)
        {
            await playerInfo.ObjectInfo.Delete();
            await CacheHelper.Instance.HashDeleteAsync(PlayerInfo.HASH_KEY, playerInfo.PlayerId);
        }

        public static async Task Delete(long playerId)
        {
            var objectField = GameObjectInfo.MakeHashField(ObjectType.PLAYER, playerId);

            await GameObjectInfo.Delete(objectField);
            await JobInfo.Delete(playerId);
            await CacheHelper.Instance.HashDeleteAsync(PlayerInfo.HASH_KEY, playerId);
        }
    }
}
