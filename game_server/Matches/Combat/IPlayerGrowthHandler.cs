using game_server.sessions;
using network.common.data.models;

namespace game_server.matches.combat;

/// <summary>
///     세션이 요청하는 오브 강화와 계열별 비용 갱신을 처리한다. 강화 결과 응답은 세션 핸들러가 보낸다.
///     실제 처리는 MatchGrowthService가 맡으며, 테스트에서는 대역으로 종료 경합과 전송 실패를 검증한다.
///     호출자는 해당 매치 잠금을 보유해야 한다.
/// </summary>
internal interface IPlayerGrowthHandler
{
    /// <summary>현재 보유 오브를 기준으로 계열별 레벨과 강화 비용을 반환한다.</summary>
    G_TO_C_ORB_UPGRADE_INFO GetOrbUpgradeInfo(long matchingId, long playerId);

    (bool Success, int ResultItemId, int TargetOrdinal) HandleUpgradeOrb(GameClientSession session, long matchingId, int action, long targetItemUid, long secondItemUid);
}
