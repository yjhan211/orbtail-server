using network.common;

namespace network.routing;

/// <summary>
///     프로토콜 ID에 맞는 패킷 처리 함수를 찾아 실행한다.
///     세션 초기화 시 RegisterHandler로 처리 함수를 등록하고,
///     패킷 수신 시 RouteAsync로 해당 함수에 본문 바이트를 전달한다.
///
///     같은 프로토콜을 다시 등록하면 기존 함수를 교체한다.
///     등록되지 않은 프로토콜을 받으면 예외를 발생시킨다.
/// </summary>
public class ProtocolRouter
{
    private readonly Dictionary<Protocol, Func<byte[], Task>> _handlers = new();

    public void RegisterHandler(Protocol protocol, Func<byte[], Task> handler)
    {
        _handlers[protocol] = handler;
    }

    public async Task RouteAsync(Protocol protocol, byte[] body)
    {
        if (!_handlers.TryGetValue(protocol, out var handler))
            throw new NotSupportedException($"Unsupported protocol: {protocol}");

        await handler(body);
    }
}
