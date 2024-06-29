namespace network
{
    using MessagePack;
    using RedLockNet;
    using RedLockNet.SERedis;
    using System.Collections.Generic;

    [MessagePackObject]
    public class LabInfo : IMessagePackObject
    {
        [IgnoreMember]
        public const string HASH_KEY = "lab_info";

        [Key("lab_id")]
        public long lab_id { get; set; }

        [Key("lab_name")]
        public string lab_name { get; set; }

        [Key("master_player_id")]
        public long master_player_id { get; set; }

        [Key("member_dict")]
        public Dictionary<long, string> member_dict { get; set; }

        [Key("grade")]
        public LabGrade lab_grade { get; set; }

        [Key("reserach_info_dict")]
        public Dictionary<int, ResearchInfo> reserach_info_dict { get; set; }

        [Key("inventory_info")]
        public InventoryInfo inventory_info { get; set; }

        public LabInfo()
        {
            this.lab_id = 0;
            this.lab_name = "";
            this.master_player_id = 0;
            this.member_dict = new();
            this.lab_grade = new();
            this.reserach_info_dict = new();
            this.inventory_info = new();
        }

        public LabInfo(
            long lab_id,
            long master_player_id,
            string master_player_name,
            string lab_name
        )
        {
            this.lab_id = lab_id;
            this.lab_name = lab_name;
            this.master_player_id = master_player_id;
            this.member_dict = new() { { master_player_id, master_player_name } };
            this.lab_grade = LabGrade.CLUB;
            this.reserach_info_dict = new();
            this.inventory_info = new(InventoryOwnerType.LAB, lab_id);
        }

        public string GetLockKey()
        {
            return $"lab_lock_{this.lab_id}";
        }

        public static string GetLockKey(long player_id)
        {
            return $"lab_lock_{player_id}";
        }

        public static async Task<IRedLock> Lock(RedLockFactory redlock, long lab_id)
        {
            return await redlock.CreateLockAsync(LabInfo.GetLockKey(lab_id), Config.LOCK_TTL);
        }

        public async Task Save()
        {
            await this.inventory_info.Save();
            await CacheHelper.Instance.HashSetAsync(
                LabInfo.HASH_KEY,
                this.lab_id,
                MessagePackSerializer.Serialize(this)
            );
        }

        public static async Task<LabInfo?> Load(long lab_id)
        {
            var serialized_data = await CacheHelper.Instance.HashGetAsync(LabInfo.HASH_KEY, lab_id);
            if (serialized_data.IsNull)
            {
                return null;
            }

            var lab_info = MessagePackSerializer.Deserialize<LabInfo?>(serialized_data);
            if (lab_info == null)
            {
                return null;
            }

            lab_info.inventory_info =
                await InventoryInfo.Load(InventoryOwnerType.LAB, lab_id)
                ?? new InventoryInfo(InventoryOwnerType.LAB, lab_id);

            return lab_info;
        }

        public static async Task Delete(long lab_id)
        {
            await CacheHelper.Instance.HashDeleteAsync(LabInfo.HASH_KEY, lab_id);
        }
    }

    [MessagePackObject]
    public class ResearchInfo : IMessagePackObject
    {
        [Key("research_id")]
        public int research_id { get; set; }

        [Key("level")]
        public int level { get; set; }

        [Key("point_dict")]
        public Dictionary<JobType, int> point_dict { get; set; }

        public ResearchInfo()
        {
            this.research_id = 0;
            this.level = 0;
            this.point_dict = new();
        }

        public ResearchInfo(int reserach_id, int level)
        {
            this.research_id = reserach_id;
            this.level = level;
            this.point_dict = new();

            this.InitResearchPoint();
        }

        public void InitResearchPoint()
        {
            this.point_dict.Clear();

            switch (this.research_id)
            {
                case 1:
                    this.point_dict[JobType.ENGINEER] = 0;
                    break;
                case 2:
                    this.point_dict[JobType.ENGINEER] = 0;
                    this.point_dict[JobType.CHEMIST] = 0;
                    break;
                case 3:
                    this.point_dict[JobType.CHEMIST] = 0;
                    break;
                case 4:
                    this.point_dict[JobType.ENGINEER] = 0;
                    break;
                case 5:
                    this.point_dict[JobType.ENGINEER] = 0;
                    this.point_dict[JobType.CHEMIST] = 0;
                    break;
                case 6:
                    this.point_dict[JobType.CHEMIST] = 0;
                    break;
                case 7:
                    this.point_dict[JobType.ENGINEER] = 0;
                    break;
                case 8:
                    this.point_dict[JobType.ENGINEER] = 0;
                    this.point_dict[JobType.CHEMIST] = 0;
                    break;
                case 9:
                    this.point_dict[JobType.CHEMIST] = 0;
                    break;
                case 10:
                    this.point_dict[JobType.ENGINEER] = 0;
                    break;
                case 11:
                    this.point_dict[JobType.ENGINEER] = 0;
                    this.point_dict[JobType.CHEMIST] = 0;
                    break;
                case 12:
                    this.point_dict[JobType.CHEMIST] = 0;
                    break;
            }
        }
    }
}
