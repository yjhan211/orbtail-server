namespace user_server.sessions;

/// <summary>
///     Redis에 등록한 플레이어의 현재 세션을 식별하는 정보.
///     플레이어 ID, UserServer 노드 ID, 연결 ID와 로그인 세대 번호를 보관한다.
///     OwnerValue는 현재 세션 확인·갱신·해제 시 Redis 값과 비교하는 데 사용한다.
///     유효기간 관리와 연결 종료는 이 객체가 아닌 저장소와 PlayerSession이 담당한다.
/// </summary>
public sealed record PlayerSessionLease(
    long PlayerId,
    string NodeId,
    string SessionId,
    long Generation,
    string OwnerValue);
