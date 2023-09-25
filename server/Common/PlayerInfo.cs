namespace game_server
{
    using System.Text.Json.Serialization;
    using MessagePack;

    [MessagePackObject]
    public partial class PlayerInfo : IMessagePackObject
    {
        // 원래는 PlayerInfo가 GameObjectInfo를 상속받는 형식을 의도했으나
        // MessagePackObject 역직렬화 과정에 문제가 있어서 이렇게 됨
        [JsonIgnore]
        [IgnoreMember]
        public GameObjectInfo object_info { get; set; }

        /*-----------------------------------------------------------------*/

        [JsonIgnore]
        [IgnoreMember]
        public const string HASH_KEY = "player_info";

        [Key("player_id")]
        public long player_id { get; set; }

        [Key("name")]
        public string name { get; set; }

        public PlayerInfo()
        {
            this.player_id = 0;
            this.name = String.Empty;
            this.object_info = new GameObjectInfo();
        }
    }
}
