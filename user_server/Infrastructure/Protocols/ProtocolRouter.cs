using Microsoft.Extensions.Logging;
using network.common;

namespace user_server.infrastructure.protocols;

/// <summary>
/// 프로토콜 라우터 구현
/// Protocol ID를 해당 핸들러로 매핑하고 라우팅
/// </summary>
public class ProtocolRouter : IProtocolRouter
{
    private readonly Dictionary<Protocol, Func<byte[], Task>> _handlers;
    private readonly ILogger _logger;

    private static readonly IReadOnlyList<Protocol> NonAuthProtocols = new List<Protocol>
    {
        Protocol.C_TO_U_HEART_BEAT,
        Protocol.C_TO_U_LOGIN
    };

    public ProtocolRouter(ILogger logger)
    {
        _handlers = new Dictionary<Protocol, Func<byte[], Task>>();
        _logger = logger;
    }

    public void RegisterHandler(Protocol protocol, Func<byte[], Task> handler)
    {
        _handlers[protocol] = handler;
    }

    public async Task RouteAsync(Protocol protocol, byte[] body)
    {
        if (!_handlers.TryGetValue(protocol, out var handler))
        {
            throw new NotSupportedException($"Unsupported protocol: {protocol}");
        }

        await handler(body);
    }

    public bool IsNonAuthProtocol(Protocol protocol)
    {
        return NonAuthProtocols.Contains(protocol);
    }
}
