using network.common;

namespace game_server.network;

/// <summary>
///     GameServer가 세션 상태 스냅샷을 구성할 때 사용하는 내부 조회 속성.
/// </summary>
public partial class GameClientSession
{
    /// <summary>스태미나 (0~100)</summary>
    internal int AdminStamina => Stamina;

    /// <summary>오염도 (0~100)</summary>
    internal int AdminCorruption => Corruption;

    /// <summary>봇 플레이어 여부 (PlayerId < 0)</summary>
    internal bool IsBot => PlayerId.HasValue && PlayerId.Value < 0;

}
