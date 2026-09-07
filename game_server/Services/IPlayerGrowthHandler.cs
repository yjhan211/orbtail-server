using game_server.sessions;

namespace game_server.services;

/// <summary>
///     세션이 요청하는 성장 카드 선택과 오브 강화 처리를 제공한다.
///     실제 처리는 MatchGrowthService가 맡으며, 테스트에서는 대역으로 종료 경합과 전송 실패를 검증한다.
///     호출자는 해당 매치 잠금을 보유해야 한다.
/// </summary>
internal interface IPlayerGrowthHandler
{
    void HandlePick(GameClientSession session, long matchingId, int offerId, int cardIndex);
    void HandleOrbDecision(GameClientSession session, long matchingId, int action, long targetItemUid, long secondItemUid);
}
