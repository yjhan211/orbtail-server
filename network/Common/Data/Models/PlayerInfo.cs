// ReSharper disable All

using System.Collections.Generic;
using System.Diagnostics.CodeAnalysis;
using MessagePack;
using network.common.data;

namespace network.common.data.models
{
    /// <summary>플레이어의 공통 데이터. 프로필과 행동 상태를 소유하고, 범용 공간 정보는 ObjectInfo에 둔다. 영속 저장 범위는 서버 저장 경로가 선택한다.</summary>
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
            InventoryInfo = new InventoryInfo();
            Gold = 0;
        }

        public PlayerInfo(long playerId, bool isDummy)
        {
            var displayPlayerId = playerId < 0 ? -playerId : playerId;
            var name = $"Player{displayPlayerId}";

            PlayerId = playerId;
            Name = name;
            WearItemIdList = new();
            InventoryInfo = new InventoryInfo(InventoryOwnerType.PLAYER, PlayerId);
            Gold = 1000;
            IsNew = true;
        }

        [IgnoreMember] public InventoryInfo InventoryInfo { get; set; }

        [Key("playerId")] public long PlayerId { get; set; }

        [Key("name")] public string Name { get; set; }

        [Key("wearItemIdList")] public List<int> WearItemIdList { get; set; }

        [Key("gold")] public long Gold { get; set; }

        // 신규 계정 초기화가 완전히 저장될 때까지 true로 유지한다.
        // 서버가 중간 실패 후 재시도할 수 있어야 하므로 Redis 직렬화 대상이다.
        [Key("isNew")] public bool IsNew { get; set; }

        [Key("objectInfo")] public GameObjectInfo ObjectInfo { get; set; }

        [Key("state")] public PlayerState State { get; set; } = PlayerState.IDLE;

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
