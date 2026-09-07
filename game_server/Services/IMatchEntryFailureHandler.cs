using game_server.network;

namespace game_server.services;

/// <summary>
///     입장 실패 처리를 세션에서 요청하는 경계.
///     세션 테스트에서 매치 전체를 중단하지 않고 호출 여부와 실패 상황을 검증한다.
/// </summary>
internal interface IMatchEntryFailureHandler
{
    void Handle(GameClientSession session);
}
