// ReSharper disable All

using System.Collections.Generic;
using System.Diagnostics.CodeAnalysis;
using MessagePack;
using network.common.data;

namespace network.common.data.models
{
    [MessagePackObject]
    public partial class PlayerInfo : IMessagePackObject
    {
        [IgnoreMember] public const string HashKey = "PlayerInfo";

        // 이거 없애면 안됨 MessagePack에서 씀
        public PlayerInfo()
        {
            PlayerId = 0;
            Name = "";
            WearItemIdList = new();
            State = PlayerState.NONE;
            ObjectInfo = new GameObjectInfo();
            InventoryInfo = new InventoryInfo();
            Gold = 0;
            Hp = 0;
            Stamina = 0;
            LastMapId = MapId.None;
            LastMapSubId = 0;
            LastCell = new(0, 0);
        }

        public PlayerInfo(long playerId, bool isDummy)
        {
            var displayPlayerId = playerId < 0 ? -playerId : playerId;
            var name = $"Player{displayPlayerId}";

            // TODO 공통맵 추가 이후 활성화
            // var initCell = isDummy ? CommonMapData.GetRandomCell() : GameRuleData.StartPosition;
            // 신규 유저는 Camp(로비)에서 시작하므로 Camp 맵의 스폰 셀을 사용한다.
            // GameRuleData.StartPosition은 Camp 유효 영역 밖이라 그대로 쓰면 로비에서 영역 밖/공중 스폰된다.
            var campMapInfo = GameMapData.GetMapInfo(MapId.Camp);
            var initCell = campMapInfo != null ? campMapInfo.GetInitialPosition().position : GameRuleData.StartPosition;
            PlayerId = playerId;
            Name = name;
            WearItemIdList = new();
            State = PlayerState.NONE;
            ObjectInfo = new GameObjectInfo(ObjectType.PLAYER, PlayerId, MapId.Camp, 0, initCell);
            InventoryInfo = new InventoryInfo(InventoryOwnerType.PLAYER, PlayerId);
            Gold = 1000;
            Hp = 5000;
            Stamina = 100;
            LastMapId = MapId.Camp;
            LastMapSubId = 0;
            LastCell = initCell;
            IsNew = true;
        }

        [IgnoreMember] public GameObjectInfo ObjectInfo { get; set; }
        [IgnoreMember] public InventoryInfo InventoryInfo { get; set; }

        [Key("playerId")] public long PlayerId { get; set; }

        [Key("name")] public string Name { get; set; }

        [Key("wearItemIdList")] public List<int> WearItemIdList { get; set; }

        [Key("state")] public PlayerState State { get; set; }

        [Key("gold")] public long Gold { get; set; }

        [Key("hp")] public int Hp { get; set; }

        [Key("stamina")] public int Stamina { get; set; }

        [Key("mapId")] public MapId LastMapId { get; set; }

        [Key("mapSubId")] public long LastMapSubId { get; set; }

        [Key("lastCell")] public Cell LastCell { get; set; }
        // 신규 계정 초기화가 완전히 저장될 때까지 true로 유지한다.
        // 서버가 중간 실패 후 재시도할 수 있어야 하므로 Redis 직렬화 대상이다.
        [Key("isNew")] public bool IsNew { get; set; }

        public string GetLockKey()
        {
            return $"player_lock_{PlayerId}";
        }

        public static string GetLockKey(long playerId)
        {
            return $"player_lock_{playerId}";
        }
    }
}
