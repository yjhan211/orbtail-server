using MessagePack;
using Microsoft.Extensions.Logging;
using network.common;
using network.common.data.models;
using network.core.abstractions;
using network.infrastructure.redis;
using network.packets;
using network.routing;
using network.utils;

namespace network.core;

/// <summary>
///     UserServer와 GameServer의 TCP 세션이 공통으로 사용하는 기반 클래스다.
///
///     UserToken이 전달한 패킷을 세션 단위로 하나씩 처리하며,
///     프로토콜 번호와 플레이어 번호, MessagePack 본문을 분리한 뒤
///     ProtocolRouter를 통해 서버별 핸들러에 전달한다.
///
///     공통 송신과 오류 처리, 연결 종료 통보를 제공하고,
///     실제 프로토콜 등록과 세션 제거 처리는 GameSession과 GameClientSession이 구현한다.
/// </summary>
public abstract class SessionBase(
    UserToken token,
    ILogger logger,
    IRedisOperations redisOperations)
    : IPeer
{
    private static readonly MessagePackSerializerOptions ClientMessagePackOptions =
        MessagePackSerializer.DefaultOptions.WithSecurity(MessagePackSecurity.UntrustedData);

    protected readonly IRedisOperations RedisOperations = redisOperations;
    protected readonly ILogger Logger = logger;
    protected readonly IProtocolRouter ProtocolRouter = new ProtocolRouter();
    private readonly SemaphoreSlim _sessionLock = new(1);
    protected readonly UserToken Token = token;

    public long? PlayerId { get; protected set; }

    public virtual async Task OnMessageFromClient(Const<byte[]> buffer)
    {
        bool lockTaken = false;
        try
        {
            await _sessionLock.WaitAsync();
            lockTaken = true;
            if (!Token.IsAcceptingMessages) return;
            if (!IsMessageLifecycleActive())
                return;

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
        catch (MessagePackSerializationException ex)
        {
            Logger.LogWarning(ex, "Invalid MessagePack payload; disconnecting client");
            Token.Disconnect();
        }
        catch (Exception ex)
        {
            Logger.LogError(ex, "Error processing client message");
            SendErrorResponse(ErrorCode.SERVER_INTERNAL_ERROR, string.Empty);
        }
        finally
        {
            if (lockTaken) _sessionLock.Release();
        }
    }

    public virtual void Send(Packet packet) => Token.Send(packet);

    public abstract void OnRemoved();

    // InitializeProtocolHandlers()는 서브클래스 생성자에서 호출
    protected abstract void InitializeProtocolHandlers();

    protected abstract bool ShouldSkipLogging(Protocol protocolId);

    protected virtual bool IsMessageLifecycleActive() => true;

    protected static async Task HandleMessage<T>(byte[] body, Func<T, Task> handler) where T : IMessagePackObject
    {
        var message = MessagePackSerializer.Deserialize<T>(body, ClientMessagePackOptions);
        await handler(message);
    }

    protected virtual void SendErrorResponse(ErrorCode errorCode, string message) { }

    public virtual void OnDisconnect()
    {
        Logger.LogInformation("Session disconnected: PlayerId={PlayerId}", PlayerId);
    }
}
