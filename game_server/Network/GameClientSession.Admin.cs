using network.common;

namespace game_server.network;

/// <summary>
///     운영 어드민용 internal getter 모음.
///     게임 로직 외부(AdminEndpointsService)에서 세션 상태를 직렬화할 때만 사용한다.
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
