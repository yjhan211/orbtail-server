namespace network
{
    using MessagePack;

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

        [Key("member_id_list")]
        public List<long> member_id_list { get; set; }

        [Key("grade")]
        public LabGrade lab_grade { get; set; }

        public LabInfo()
        {
            this.lab_id = 0;
            this.lab_name = "";
            this.master_player_id = 0;
            this.member_id_list = new();
            this.lab_grade = new();
        }

        public LabInfo(long lab_id, long master_player_id, string lab_name)
        {
            this.lab_id = lab_id;
            this.lab_name = lab_name;
            this.master_player_id = master_player_id;
            this.member_id_list = new() { master_player_id };
            this.lab_grade = LabGrade.CLUB;
        }
    }
}
