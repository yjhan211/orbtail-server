using game_server.sessions;

namespace game_server.services;

/// <summary>
///     세션이 요청하는 오브 강화를 처리하고 결과를 반환한다. 응답 전송은 세션 핸들러가 맡는다.
///     실제 처리는 MatchGrowthService가 맡으며, 테스트에서는 대역으로 종료 경합과 전송 실패를 검증한다.
///     호출자는 해당 매치 잠금을 보유해야 한다.
/// </summary>
internal interface IPlayerGrowthHandler
{
    (bool Success, int ResultItemId, int TargetOrdinal) HandleUpgradeOrb(GameClientSession session, long matchingId, int action, long targetItemUid, long secondItemUid);
}
