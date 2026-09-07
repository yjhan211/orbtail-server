using MessagePack;
using Microsoft.Extensions.Logging;
using network.common;
using network.common.data.models;
using network.infrastructure.redis;
using network.packets;
using network.routing;

namespace network.core;

/// <summary>
///     UserServer와 GameServer의 TCP 세션이 공통으로 사용하는 기반 클래스다.
///
///     TcpConnection이 전달한 패킷을 세션 단위로 하나씩 처리하며,
///     프로토콜 번호와 플레이어 번호, MessagePack 본문을 분리한 뒤
///     ProtocolRouter를 통해 서버별 핸들러에 전달한다.
///
///     공통 송신과 오류 처리, 연결 종료 통보를 제공하고,
///     실제 프로토콜 등록과 세션 제거 처리는 PlayerSession과 GameClientSession이 구현한다.
/// </summary>
public abstract class SessionBase(
    TcpConnection connection,
    ILogger logger,
    IRedisOperations redisOperations)
    : IConnectionSession
{
    private static readonly MessagePackSerializerOptions ClientMessagePackOptions =
        MessagePackSerializer.DefaultOptions.WithSecurity(MessagePackSecurity.UntrustedData);

    protected readonly IRedisOperations RedisOperations = redisOperations;
    protected readonly ILogger Logger = logger;
    protected readonly ProtocolRouter ProtocolRouter = new();
    private readonly SemaphoreSlim _sessionLock = new(1);
    protected readonly TcpConnection Connection = connection;

    public long? PlayerId { get; protected set; }

    public virtual async Task OnMessageFromClient(ReadOnlyMemory<byte> buffer)
    {
        try
        {
            if (!Connection.IsAcceptingMessages) return;

            Protocol protocolId;
            long playerId;
            byte[] body;
            using (var packet = Packet.Create(buffer))
            {
                protocolId = (Protocol)packet.PopProtocolId();
                playerId = packet.PopPlayerId();
                body = packet.PopBody();
            }

            await ScheduleMessageAsync(protocolId, body,
                () => ProcessMessageAsync(protocolId, playerId, body));
        }
        catch (MessagePackSerializationException ex)
        {
            Logger.LogWarning(ex, "Invalid MessagePack payload; disconnecting client");
            Connection.Disconnect();
        }
        catch (Exception ex)
        {
            Logger.LogError(ex, "Error processing client message");
            SendErrorResponse(ErrorCode.SERVER_INTERNAL_ERROR, string.Empty);
        }
    }

    /// <summary>
    ///     기본은 즉시 처리한다. 서버별로 대기 요청을 합칠 경우에도 실제 처리는 dispatch로 수행하고,
    ///     반환 Task는 처리 또는 취소가 끝날 때 완료해야 연결 종료가 정리를 기다릴 수 있다.
    /// </summary>
    protected virtual Task ScheduleMessageAsync(Protocol protocolId, byte[] body, Func<Task> dispatch) => dispatch();

    private async Task ProcessMessageAsync(Protocol protocolId, long playerId, byte[] body)
    {
        await _sessionLock.WaitAsync();
        try
        {
            if (!Connection.IsAcceptingMessages || !IsMessageLifecycleActive()) return;

            if (!await CanProcessMessageAsync(protocolId)) return;
            if (!Connection.IsAcceptingMessages || !IsMessageLifecycleActive()) return;

            if (!ShouldSkipLogging(protocolId))
                Logger.LogInformation("[Receive] Protocol: {Protocol}, PlayerId: {PlayerId}", protocolId, playerId);

            await ProtocolRouter.RouteAsync(protocolId, body);

            if (!ShouldSkipLogging(protocolId))
                Logger.LogInformation("[Processed] Protocol: {Protocol} completed", protocolId);
        }
        finally
        {
            _sessionLock.Release();
        }
    }

    public virtual bool TrySend(Packet packet) => Connection.TrySend(packet);

    public abstract void OnRemoved();

    protected abstract bool ShouldSkipLogging(Protocol protocolId);

    protected virtual bool IsMessageLifecycleActive() => true;

    protected virtual Task<bool> CanProcessMessageAsync(Protocol protocolId) => Task.FromResult(true);

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
