using network.common;

namespace network.routing;

/// <summary>
///     프로토콜 라우터 구현
///     Protocol ID를 해당 핸들러로 매핑하고 라우팅
/// </summary>
public class ProtocolRouter : IProtocolRouter
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
