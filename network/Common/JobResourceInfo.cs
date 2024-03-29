namespace network
{
    using MessagePack;

    [MessagePackObject]
    public class JobResourceInfo : IMessagePackObject
    {
        [IgnoreMember]
        public GameObjectInfo object_info { get; set; }

        [Key("resource_uid")]
        public long resource_uid { get; set; } // 유니크 아이디

        [Key("resource_id")]
        public int resource_id { get; set; } // 리소스 종류. 네모난 돌, 동그란 돌, 잡초..

        // 이거 없애면 안됨 MessagePack에서 씀
        public JobResourceInfo()
        {
            this.object_info = new();
            this.resource_uid = 0;
            this.resource_id = 0;
        }

        public JobResourceInfo(long resource_uid, int resource_id, GameObjectInfo object_info)
        {
            this.object_info = object_info;
            this.resource_uid = resource_uid;
            this.resource_id = resource_id;
        }
    }
}
