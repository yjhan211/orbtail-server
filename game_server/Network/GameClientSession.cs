using MessagePack;
using Microsoft.Extensions.Logging;
using network.common;
using network.common.data.models;
using network.config;
using network.core;
using network.helpers;
using network.interfaces;
using network.packets;
using network.routing;

namespace game_server.network;

/// <summary>
/// GameServer에 직접 연결된 클라이언트 세션
/// 실시간 게임 패킷 처리 (위치 동기화, 전투, 오브젝트 생성/파괴)
/// </summary>
public class GameClientSession : IPeer
{
    private readonly UserToken _token;
    private readonly SemaphoreSlim _sessionLock;
    private readonly Action<GameClientSession> _onLeaveCallback;
    private readonly IProtocolRouter _protocolRouter;

    public readonly ILogger Logger;
    public readonly ICacheHelper CacheHelper;
    public readonly IRedLockFactory RedLock;
    public readonly INatsClient NatsClient;
    public readonly IServerConfig ServerConfig;

    public long? PlayerId { get; private set; }
    public MapId CurrentMapId { get; private set; }
    public long CurrentMapSubId { get; private set; }

    public GameClientSession(
        UserToken token,
        IRedLockFactory redLock,
        INatsClient natsClient,
        ILogger logger,
        ICacheHelper cacheHelper,
        Action<GameClientSession> onLeaveCallback,
        IServerConfig serverConfig)
    {
        _token = token;
        _token.SetPeer(this);
        _sessionLock = new SemaphoreSlim(1);

        RedLock = redLock;
        NatsClient = natsClient;
        Logger = logger;
        CacheHelper = cacheHelper;
        ServerConfig = serverConfig;
        _onLeaveCallback = onLeaveCallback;

        _protocolRouter = new ProtocolRouter(logger);
        InitializeProtocolHandlers();

        Logger.LogInformation("GameClientSession created");
    }

    private void InitializeProtocolHandlers()
    {
        // 클라이언트로부터 받는 실시간 패킷들
        _protocolRouter.RegisterHandler(Protocol.C_TO_G_MOVE, async (bytes) => await HandleMessage<C_TO_G_MOVE>(bytes, HandleMove));
        _protocolRouter.RegisterHandler(Protocol.C_TO_G_ATTACK, async (bytes) => await HandleMessage<C_TO_G_ATTACK>(bytes, HandleAttack));
        _protocolRouter.RegisterHandler(Protocol.C_TO_G_INTERACT, async (bytes) => await HandleMessage<C_TO_G_INTERACT>(bytes, HandleInteract));
    }

    public async Task OnMessageFromClient(Const<byte[]> buffer)
    {
        try
        {
            await _sessionLock.WaitAsync();

            using var packet = Packet.Create(buffer);
            var protocolId = (Protocol)packet.PopProtocolId();
            var playerId = packet.PopPlayerId();
            var body = packet.PopBody();

            Logger.LogInformation($"[GameClient] Protocol: {protocolId}, PlayerId: {playerId}");

            await _protocolRouter.RouteAsync(protocolId, body);
        }
        catch (Exception ex)
        {
            Logger.LogError(ex, "Error processing client message");
        }
        finally
        {
            _sessionLock.Release();
        }
    }

    private async Task HandleMessage<T>(byte[] body, Func<T, Task> handler) where T : IMessagePackObject
    {
        var message = MessagePackSerializer.Deserialize<T>(body);
        await handler(message);
    }

    private Task HandleMove(C_TO_G_MOVE msg)
    {
        Logger.LogInformation($"Player {PlayerId} move: {msg.TargetPosition}");
        // TODO: 이동 처리 및 브로드캐스트
        return Task.CompletedTask;
    }

    private Task HandleAttack(C_TO_G_ATTACK msg)
    {
        Logger.LogInformation($"Player {PlayerId} attack: {msg.TargetId}");
        // TODO: 공격 처리
        return Task.CompletedTask;
    }

    private Task HandleInteract(C_TO_G_INTERACT msg)
    {
        Logger.LogInformation($"Player {PlayerId} interact: {msg.TargetId}");
        // TODO: 상호작용 처리
        return Task.CompletedTask;
    }

    public void Send(IPacket packet)
    {
        if (packet is Packet p)
        {
            _token.Send(p.ToBytes());
        }
    }

    public void OnRemoved()
    {
        Logger.LogInformation($"GameClient removed: PlayerId={PlayerId}");
        _onLeaveCallback(this);
    }

    public void OnDisconnect()
    {
        Logger.LogInformation($"GameClient disconnected: PlayerId={PlayerId}");
        _onLeaveCallback(this);
    }

    public Task<UserToken?> Release()
    {
        return Task.FromResult<UserToken?>(_token);
    }
}

// 임시 프로토콜 메시지 정의 (나중에 network/Common에 추가)
[MessagePackObject]
public class C_TO_G_MOVE : IMessagePackObject
{
    [Key(0)] public Cell TargetPosition { get; set; }
}

[MessagePackObject]
public class C_TO_G_ATTACK : IMessagePackObject
{
    [Key(0)] public string TargetId { get; set; }
}

[MessagePackObject]
public class C_TO_G_INTERACT : IMessagePackObject
{
    [Key(0)] public string TargetId { get; set; }
}
