using network.packets;

namespace network.core;

/// <summary>
///     TCP 연결 하나에 결합되는 서버 세션의 공통 계약.
///     네트워크 계층은 수신한 메시지와 연결 종료 사실을 세션에 전달하고,
///     세션은 서버별 프로토콜을 처리한 뒤 같은 연결로 응답을 보낸다.
///     User Server와 Game Server는 서로 다른 세션을 사용하지만 연결 계층은 이 인터페이스만 의존한다.
/// </summary>
public interface IConnectionSession
{
    public Task OnMessageFromClient(ReadOnlyMemory<byte> buffer);
    public void OnDisconnect();
    public void OnRemoved();
    /// <summary>송신 큐가 패킷을 받아들였는지 반환한다. 클라이언트의 수신 완료를 뜻하지 않는다.</summary>
    public bool TrySend(Packet msg);
}
