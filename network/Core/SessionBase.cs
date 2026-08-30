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
///     GameClientSession / GameSession 공통 세션 로직 추상 클래스.
///     패킷 파싱, 프로토콜 라우팅, 세션 잠금, 기본 IPeer 구현을 공유한다.
/// </summary>
public abstract class SessionBase : IPeer
{
    private const string InternalErrorMessage = "요청 처리 중 오류가 발생했습니다";
    private static readonly MessagePackSerializerOptions ClientMessagePackOptions =
        MessagePackSerializer.DefaultOptions.WithSecurity(MessagePackSecurity.UntrustedData);

    protected readonly ICacheHelper CacheHelper;
    protected readonly ILogger Logger;
    protected readonly IProtocolRouter ProtocolRouter;
    protected readonly IRedLockFactory RedLock;
    private readonly SemaphoreSlim _sessionLock;
    protected readonly UserToken Token;

    protected SessionBase(
        UserToken token,
        ILogger logger,
        ICacheHelper cacheHelper,
        IRedLockFactory redLock)
    {
        Token = token;
        _sessionLock = new SemaphoreSlim(1);
        Logger = logger;
        CacheHelper = cacheHelper;
        RedLock = redLock;

        ProtocolRouter = new ProtocolRouter();
        // InitializeProtocolHandlers()는 서브클래스 생성자에서 호출
        // (base 생성자 시점에는 서브클래스 필드가 아직 초기화되지 않음)
        Token.SetPeer(this);
    }

    // ReSharper disable once UnusedAutoPropertyAccessor.Global — 서브클래스(GameClientSession, GameSession)에서 사용
    public long? PlayerId { get; protected set; }

    public virtual async Task OnMessageFromClient(Const<byte[]> buffer)
    {
        bool lockTaken = false;
        try
        {
            await _sessionLock.WaitAsync();
            lockTaken = true;
            if (!Token.IsAcceptingMessages) return;

            if (!TryAcquireMessageScope(out IDisposable? messageScope))
                return;
            using (messageScope)
            {
                using var packet = Packet.Create(buffer);
                var protocolId = (Protocol)packet.PopProtocolId();
                long playerId = packet.PopPlayerId();
                byte[] body = packet.PopBody();

                if (!ShouldSkipLogging(protocolId))
                    Logger.LogInformation("[Receive] Protocol: {Protocol}, PlayerId: {PlayerId}",
                        protocolId, playerId);

                await ProtocolRouter.RouteAsync(protocolId, body);

                if (!ShouldSkipLogging(protocolId))
                    Logger.LogInformation("[Processed] Protocol: {Protocol} completed", protocolId);
            }
        }
        catch (MessagePackSerializationException ex)
        {
            Logger.LogWarning(ex, "Invalid MessagePack payload; disconnecting client");
            Token.Disconnect();
        }
        catch (Exception ex)
        {
            Logger.LogError(ex, "Error processing client message");
            SendErrorResponse(ErrorCode.SERVER_INTERNAL_ERROR, InternalErrorMessage);
        }
        finally
        {
            if (lockTaken) _sessionLock.Release();
        }
    }

    public virtual void Send(IPacket packet)
    {
        if (packet is Packet p) Token.Send(p);
    }

    public abstract void OnRemoved();

    /// <summary>
    ///     프로토콜 핸들러를 ProtocolRouter에 등록한다.
    /// </summary>
    protected abstract void InitializeProtocolHandlers();

    /// <summary>
    ///     로깅 대상에서 제외할 프로토콜인지 판단한다.
    /// </summary>
    protected abstract bool ShouldSkipLogging(Protocol protocolId);

    /// <summary>
    ///     Lets a derived session keep an external lifecycle alive for the complete asynchronous
    ///     protocol handler. Returning false drops work whose lifecycle is already terminal.
    /// </summary>
    protected virtual bool TryAcquireMessageScope(out IDisposable? scope)
    {
        scope = null;
        return true;
    }

    protected static async Task HandleMessage<T>(byte[] body, Func<T, Task> handler) where T : IMessagePackObject
    {
        var message = MessagePackSerializer.Deserialize<T>(body, ClientMessagePackOptions);
        await handler(message);
    }

    /// <summary>
    ///     미처리 예외 발생 시 클라이언트에 에러 응답을 전송한다.
    ///     서브클래스에서 오버라이드하여 적절한 에러 패킷(U_TO_C_ERROR / G_TO_C_ERROR)을 전송한다.
    /// </summary>
    protected virtual void SendErrorResponse(ErrorCode errorCode, string message) { }

    public virtual void OnDisconnect()
    {
        Logger.LogInformation("Session disconnected: PlayerId={PlayerId}", PlayerId);
    }

    public Task<UserToken?> Release()
    {
        return Task.FromResult<UserToken?>(Token);
    }
}
