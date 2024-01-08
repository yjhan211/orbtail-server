namespace network
{
    using MessagePack;

    [MessagePackObject]
    public class PlayerInfo : IMessagePackObject
    {
        // 원래는 PlayerInfo가 GameObjectInfo를 상속받는 형식을 의도했으나
        // MessagePackObject 역직렬화 과정에 문제가 있어서 이렇게 됨
        [IgnoreMember]
        public GameObjectInfo object_info { get; set; }

        /*-----------------------------------------------------------------*/

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

        public PlayerInfo(long player_id, string name, Cell cell)
        {
            this.player_id = player_id;
            this.object_info = new GameObjectInfo(ObjectType.PLAYER, player_id, cell);
            this.name = name;
        }

        public string GetLockKey()
        {
            return $"player_lock_{this.player_id}";
        }

        public static string GetLockKey(long player_id)
        {
            return $"player_lock_{player_id}";
        }
    }
}
