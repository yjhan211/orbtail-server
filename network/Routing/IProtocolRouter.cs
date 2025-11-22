using network.common;

namespace network.routing;

/// <summary>
/// 프로토콜 ID를 핸들러로 라우팅하는 인터페이스
/// </summary>
public interface IProtocolRouter
{
    /// <summary>
    /// 프로토콜 핸들러 등록
    /// </summary>
    void RegisterHandler(Protocol protocol, Func<byte[], Task> handler);

    /// <summary>
    /// 프로토콜 메시지 처리
    /// </summary>
    Task RouteAsync(Protocol protocol, byte[] body);

    /// <summary>
    /// 프로토콜이 인증 없이 처리 가능한지 확인
    /// </summary>
    bool IsNonAuthProtocol(Protocol protocol);
}
