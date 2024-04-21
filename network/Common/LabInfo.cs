namespace network
{
    using MessagePack;
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
            JobType master_player_job_type,
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

            ResearchInfo reserach = new ResearchInfo();
            switch (master_player_job_type)
            {
                case JobType.GEOIOGIST:
                    reserach.research_id = 1;
                    reserach.geo_level = 1;
                    break;

                case JobType.BOTANIST:
                    reserach.research_id = 2;
                    reserach.botan_level = 1;
                    break;

                case JobType.BIOLOGY:
                    reserach.research_id = 3;
                    reserach.bio_level = 1;
                    break;
            }

            this.reserach_info_dict.Add(reserach.research_id, reserach);
        }

        public string GetLockKey()
        {
            return $"lab_lock_{this.lab_id}";
        }

        public static string GetLockKey(long player_id)
        {
            return $"lab_lock_{player_id}";
        }
    }

    [MessagePackObject]
    public class ResearchInfo : IMessagePackObject
    {
        [Key("research_id")]
        public int research_id { get; set; }

        [Key("geo_level")]
        public int geo_level { get; set; }

        [Key("botan_level")]
        public int botan_level { get; set; }

        [Key("bio_level")]
        public int bio_level { get; set; }
    }
}
