using MessagePack;
using Microsoft.Extensions.Logging;
using network.common;
using network.common.data.models;
using network.interfaces;
using network.packets;
using network.routing;
using network.utils;

namespace network.core;

/// <summary>
/// GameClientSession / GameSession 공통 세션 로직 추상 클래스.
/// 패킷 파싱, 프로토콜 라우팅, 세션 잠금, 기본 IPeer 구현을 공유한다.
/// </summary>
public abstract class SessionBase : IPeer
{
    protected readonly UserToken _token;
    protected readonly SemaphoreSlim _sessionLock;
    protected readonly ILogger _logger;
    protected readonly ICacheHelper _cacheHelper;
    protected readonly IRedLockFactory _redLock;
    protected readonly IProtocolRouter _protocolRouter;

    public long? PlayerId { get; protected set; }

    protected SessionBase(
        UserToken token,
        ILogger logger,
        ICacheHelper cacheHelper,
        IRedLockFactory redLock)
    {
        _token = token;
        _token.SetPeer(this);
        _sessionLock = new SemaphoreSlim(1);
        _logger = logger;
        _cacheHelper = cacheHelper;
        _redLock = redLock;

        _protocolRouter = new ProtocolRouter(logger);
        // InitializeProtocolHandlers()는 서브클래스 생성자에서 호출
        // (base 생성자 시점에는 서브클래스 필드가 아직 초기화되지 않음)
    }

    /// <summary>
    /// 프로토콜 핸들러를 _protocolRouter에 등록한다.
    /// </summary>
    protected abstract void InitializeProtocolHandlers();

    /// <summary>
    /// 로깅 대상에서 제외할 프로토콜인지 판단한다.
    /// </summary>
    protected abstract bool ShouldSkipLogging(Protocol protocolId);

    public virtual async Task OnMessageFromClient(Const<byte[]> buffer)
    {
        try
        {
            await _sessionLock.WaitAsync();

            using var packet = Packet.Create(buffer);
            var protocolId = (Protocol)packet.PopProtocolId();
            var playerId = packet.PopPlayerId();
            var body = packet.PopBody();

            if (!ShouldSkipLogging(protocolId))
            {
                _logger.LogInformation("[Receive] Protocol: {Protocol}, PlayerId: {PlayerId}",
                    protocolId, playerId);
            }

            await _protocolRouter.RouteAsync(protocolId, body);

            if (!ShouldSkipLogging(protocolId))
            {
                _logger.LogInformation("[Processed] Protocol: {Protocol} completed", protocolId);
            }
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error processing client message");
        }
        finally
        {
            _sessionLock.Release();
        }
    }

    protected static async Task HandleMessage<T>(byte[] body, Func<T, Task> handler) where T : IMessagePackObject
    {
        var message = MessagePackSerializer.Deserialize<T>(body);
        await handler(message);
    }

    public virtual void Send(IPacket packet)
    {
        if (packet is Packet p)
        {
            _token.Send(p);
        }
    }

    public abstract void OnRemoved();

    public virtual void OnDisconnect()
    {
        _logger.LogInformation("Session disconnected: PlayerId={PlayerId}", PlayerId);
    }

    public Task<UserToken?> Release()
    {
        return Task.FromResult<UserToken?>(_token);
    }
}
