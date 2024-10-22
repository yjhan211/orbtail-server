using MessagePack;
using RedLockNet;
using RedLockNet.SERedis;
using network.helpers;

namespace network.common
{
    [MessagePackObject]
    public class LabInfo : IMessagePackObject
    {
        [IgnoreMember]
        public const string HASH_KEY = "LabInfo";

        [Key("labId")]
        public long LabId { get; set; }

        [Key("labName")]
        public string LabName { get; set; }

        [Key("masterPlayerId")]
        public long MasterPlayerId { get; set; }

        [Key("masterPlayerName")]
        public string MasterPlayerName { get; set; }

        [Key("memberDict")]
        public Dictionary<long, string> MemberDict { get; set; }

        [Key("labGrade")]
        public LabGrade LabGrade { get; set; }

        [Key("reserachInfoDict")]
        public Dictionary<int, ResearchInfo> ResearchInfoDict { get; set; }

        [Key("inventoryInfo")]
        public InventoryInfo InventoryInfo { get; set; }

        public LabInfo()
        {
            LabId = 0;
            LabName = "";
            MasterPlayerId = 0;
            MasterPlayerName = "";
            MemberDict = new();
            LabGrade = LabGrade.NONE;
            ResearchInfoDict = new();
            InventoryInfo = new();
        }

        public LabInfo(long labId, long masterPlayerId, string masterPlayerName, string labName)
        {
            LabId = labId;
            LabName = labName;
            MasterPlayerId = masterPlayerId;
            MasterPlayerName = masterPlayerName;
            MemberDict = new() { { MasterPlayerId, MasterPlayerName } };
            LabGrade = LabGrade.CLUB;
            ResearchInfoDict = new();
            InventoryInfo = new(InventoryOwnerType.LAB, LabId);
        }

        public string GetLockKey()
        {
            return $"lab_lock_{LabId}";
        }

        public static string GetLockKey(long labId)
        {
            return $"lab_lock_{labId}";
        }

        public static async Task<IRedLock> Lock(RedLockFactory redlock, long labId)
        {
            return await redlock.CreateLockAsync(LabInfo.GetLockKey(labId), Config.LOCK_TTL);
        }

        public async Task Save()
        {
            await InventoryInfo.Save();
            await CacheHelper.Instance.HashSetAsync(LabInfo.HASH_KEY, LabId, MessagePackSerializer.Serialize(this));
        }

        public static async Task<LabInfo?> Load(long labId)
        {
            var serializedData = await CacheHelper.Instance.HashGetAsync(LabInfo.HASH_KEY, labId);
            if (serializedData.IsNull)
            {
                return null;
            }

            var labInfo = MessagePackSerializer.Deserialize<LabInfo?>(serializedData);
            if (labInfo == null)
            {
                return null;
            }

            labInfo.InventoryInfo = await InventoryInfo.Load(InventoryOwnerType.LAB, labId) ?? new InventoryInfo(InventoryOwnerType.LAB, labId);
            return labInfo;
        }

        public static async Task Delete(long labId)
        {
            await CacheHelper.Instance.HashDeleteAsync(LabInfo.HASH_KEY, labId);
        }
    }

    [MessagePackObject]
    public class ResearchInfo : IMessagePackObject
    {
        [Key("researchId")]
        public int ResearchId { get; set; }

        [Key("level")]
        public int Level { get; set; }

        [Key("pointDict")]
        public Dictionary<JobType, int> PointDict { get; set; }

        public ResearchInfo()
        {
            ResearchId = 0;
            Level = 0;
            PointDict = new();
        }

        public ResearchInfo(int reserachId, int level)
        {
            ResearchId = reserachId;
            Level = level;
            PointDict = new();
            InitResearchPoint();
        }

        public void InitResearchPoint()
        {
            PointDict.Clear();

            // 기획 변경 예정
        }
    }
}
