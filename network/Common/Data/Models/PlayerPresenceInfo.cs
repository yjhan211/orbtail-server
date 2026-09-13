#pragma warning disable CS8618
using MessagePack;

namespace network.common.data.models
{
    /// <summary>등장 전송 단위. 플레이어 정보와 별도 직렬화하는 공간 정보를 함께 전달한다.</summary>
    [MessagePackObject]
    public class PlayerPresenceInfo
    {
        [Key("player")] public PlayerInfo Player { get; set; }
        [Key("objectInfo")] public GameObjectInfo ObjectInfo { get; set; }
    }
}
