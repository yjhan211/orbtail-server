namespace network
{
    using MessagePack;

    [MessagePackObject]
    public class JobResourceInfo : IMessagePackObject
    {
        [IgnoreMember]
        public const string HASH_KEY = "job_resource_info";

        [IgnoreMember]
        public GameObjectInfo object_info { get; set; }

        [Key("resource_uid")]
        public long resource_uid { get; set; } // 유니크 아이디

        [Key("resource_id")]
        public int resource_id { get; set; } // 리소스 종류. 네모난 돌, 동그란 돌, 잡초..

        [Key("player_id")]
        public long player_id { get; set; } // 점유중인 플레이어 아이디

        [Key("end_timestamp")]
        public DateTime end_timestamp { get; set; } // 점유 끝나는 시간

        // 이거 없애면 안됨 MessagePack에서 씀
        public JobResourceInfo()
        {
            this.object_info = new();
            this.resource_uid = 0;
            this.resource_id = 0;
            this.player_id = 0;
        }

        public JobResourceInfo(long resource_uid, int resource_id, GameObjectInfo object_info)
        {
            this.object_info = object_info;
            this.resource_uid = resource_uid;
            this.resource_id = resource_id;
            this.player_id = 0;
        }

        public string GetLockKey()
        {
            return $"job_resource_lock_{this.resource_uid}";
        }

        public static string GetLockKey(long resource_uid)
        {
            return $"job_resource_lock_{resource_uid}";
        }
    }
}
