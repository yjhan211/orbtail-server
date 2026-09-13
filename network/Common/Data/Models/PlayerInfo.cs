// ReSharper disable All

using System.Collections.Generic;
using System.Diagnostics.CodeAnalysis;
using MessagePack;
using network.common.data;

namespace network.common.data.models
{
    /// <summary>플레이어의 영속 프로필. 매치 중 공간·체력·행동 상태는 GamePlayerInfo가 소유한다.</summary>
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
