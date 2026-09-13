using MessagePack;

namespace network.common.data.models
{
    /// <summary>매치 중 플레이어의 공간·체력·행동·참가 상태. 프로필 저장과 독립적인 공통 모델.</summary>
    [MessagePackObject]
    public class GamePlayerInfo
    {
        [Key("objectInfo")] public GameObjectInfo ObjectInfo { get; set; } = new GameObjectInfo { ObjectType = ObjectType.PLAYER };
        [Key("health")] public int Health { get; set; } = Config.MAX_HEALTH;
        [Key("state")] public PlayerState State { get; set; } = PlayerState.IDLE;
        [Key("status")] public PlayerMatchStatus Status { get; set; } = PlayerMatchStatus.ACTIVE;
    }
}
