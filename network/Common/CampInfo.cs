using MessagePack;
using network.helpers;

namespace network.common
{
    [MessagePackObject]
    public class CampInfo : IMessagePackObject
    {
        [IgnoreMember]
        public const string HASH_KEY = "CampInfo";

        [IgnoreMember]
        public GameObjectInfo ObjectInfo { get; set; }

        [Key("playerId")]
        public long PlayerId { get; set; }

        [Key("playerName")]
        public string PlayerName { get; set; }

        [Key("itemInfo")]
        public ItemInfo ItemInfo { get; set; }

        [Key("cellDict")]
        public Dictionary<long, (ItemInfo, int)> CellDict { get; set; }

        public CampInfo()
        {
            ObjectInfo = new();
            PlayerId = new();
            PlayerName = "";
            ItemInfo = new();
            CellDict = new();
        }

        public CampInfo(long playerId, string playerName, GameObjectInfo playerObjectInfo, ItemInfo itemInfo, Cell cell)
        {
            ObjectInfo = new(
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
            CellDict = new();
        }

        public async Task Save()
        {
            await ObjectInfo.Save();
            await CacheHelper.Instance.HashSetAsync(CampInfo.HASH_KEY, PlayerId, MessagePackSerializer.Serialize(this));
        }

        public static async Task<CampInfo?> Load(long playerId)
        {
            var serializedData = await CacheHelper.Instance.HashGetAsync(CampInfo.HASH_KEY, playerId);
            if (serializedData.IsNull)
            {
                return null;
            }

            var campInfo = MessagePackSerializer.Deserialize<CampInfo?>(serializedData);
            if (campInfo == null)
            {
                return null;
            }

            var objectInfo = await GameObjectInfo.Load(ObjectType.CAMP, playerId);
            if (objectInfo == null)
            {
                return null;
            }

            campInfo.ObjectInfo = objectInfo;
            return campInfo;
        }

        public async Task Delete()
        {
            await CacheHelper.Instance.HashDeleteAsync(CampInfo.HASH_KEY, PlayerId);
        }

        public static async Task Delete(long playerId)
        {
            await CacheHelper.Instance.HashDeleteAsync(CampInfo.HASH_KEY, playerId);
        }
    }
}
