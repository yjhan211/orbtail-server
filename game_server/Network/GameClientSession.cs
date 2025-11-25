using MessagePack;
using Microsoft.Extensions.Logging;
using network.common;
using network.common.data.helpers;
using network.common.data.models;
using network.config;
using network.core;
using network.helpers;
using network.interfaces;
using network.packets;
using network.routing;
using network.utils;

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

    private async Task HandleMove(C_TO_G_MOVE msg)
    {
        if (PlayerId == null) return;

        try
        {
            // 1. 위치 검증
            var validatedPosition = ValidatePosition(msg.Position, msg.Velocity);

            // 2. 현재 타일 계산 (자동!)
            var currentCell = CoordinateConverter.WorldToCell(validatedPosition);

            // 3. PlayerInfo 로드 및 업데이트
            await using var playerLock = await PlayerInfo.Lock(RedLock, PlayerId.Value);
            var playerInfo = await PlayerInfo.Load(CacheHelper, PlayerId.Value);

            if (playerInfo != null)
            {
                var oldCell = playerInfo.ObjectInfo.CurrentCell;

                // 위치 업데이트
                playerInfo.ObjectInfo.Position = validatedPosition;
                playerInfo.ObjectInfo.Velocity = msg.Velocity;
                playerInfo.ObjectInfo.Rotation = msg.Rotation;
                playerInfo.ObjectInfo.CurrentCell = currentCell;
                playerInfo.ObjectInfo.MoveTimestamp = DateTime.UtcNow;

                // 타일 변경 감지
                bool cellChanged = !oldCell.Equals(currentCell);

                if (cellChanged)
                {
                    Logger.LogInformation($"Player {PlayerId} 타일 이동: {oldCell} → {currentCell}");
                    await OnCellChanged(playerInfo, oldCell, currentCell);
                }

                await playerInfo.Save(CacheHelper);
            }

            // 4. 브로드캐스트 (같은 맵의 다른 플레이어들에게)
            var serverTimestamp = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
            using var packet = PacketMaker.G_TO_C_MOVE(
                PlayerId.Value,
                validatedPosition,
                msg.Velocity,
                msg.Rotation,
                currentCell,
                msg.InputSequence,
                serverTimestamp
            );

            // NATS로 같은 맵의 플레이어들에게 전송
            var subject = SubjectHelper.GetUpdateInfoSubject(
                CurrentMapId,
                CurrentMapSubId,
                ServerConfig.ServerId
            );
            NatsClient.Publish(subject, packet.ToBytes());

            Logger.LogDebug($"Player {PlayerId} move broadcasted: {validatedPosition}");
        }
        catch (Exception ex)
        {
            Logger.LogError(ex, $"HandleMove error for player {PlayerId}");
        }
    }

    private Vector3f ValidatePosition(
        Vector3f clientPos,
        Vector3f velocity)
    {
        // 속도 제한 체크 (치트 방지)
        const float MAX_SPEED = 20f; // 최대 속도 (조정 필요)
        float speed = velocity.Magnitude();

        if (speed > MAX_SPEED)
        {
            Logger.LogWarning($"Player {PlayerId} 속도 초과: {speed:F2} > {MAX_SPEED}");
            // 속도 클램핑
            velocity = velocity.Normalized() * MAX_SPEED;
        }

        // TODO: 맵 경계 체크
        // TODO: 장애물 충돌 체크

        return clientPos;
    }

    private async Task OnCellChanged(
        PlayerInfo playerInfo,
        Cell oldCell,
        Cell newCell)
    {
        // 타일 기반 로직들

        // TODO: 1. 트리거 체크 (특정 타일 진입 시)
        // await CheckTileTriggers(newCell);

        // TODO: 2. AOI (Area of Interest) 업데이트
        // await UpdateAreaOfInterest(playerInfo, oldCell, newCell);

        // TODO: 3. 타일별 이벤트 (함정, 버프 존 등)
        // await ProcessTileEvents(newCell);

        Logger.LogInformation($"OnCellChanged: {oldCell} → {newCell} (플레이어: {playerInfo.PlayerId})");
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
            _token.Send(p);
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
