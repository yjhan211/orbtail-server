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
    private readonly Action<long, GameClientSession> _registerSessionCallback;
    private readonly Func<MapId, long, List<GameClientSession>> _getSessionsByInstance;

    public readonly ILogger Logger;
    public readonly ICacheHelper CacheHelper;
    public readonly IRedLockFactory RedLock;
    public readonly INatsClient NatsClient;
    public readonly IServerConfig ServerConfig;

    public long? PlayerId { get; private set; }
    public MapId CurrentMapId { get; private set; }
    public long CurrentMapSubId { get; private set; }

    // 이동 검증용 상태
    private Vector3f? _lastValidatedPosition;
    private DateTime _lastMoveTime = DateTime.UtcNow;
    private DateTime _lastSaveTime = DateTime.UtcNow;

    public GameClientSession(
        UserToken token,
        IRedLockFactory redLock,
        INatsClient natsClient,
        ILogger logger,
        ICacheHelper cacheHelper,
        Action<GameClientSession> onLeaveCallback,
        IServerConfig serverConfig,
        Action<long, GameClientSession> registerSessionCallback,
        Func<MapId, long, List<GameClientSession>> getSessionsByInstance)
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
        _registerSessionCallback = registerSessionCallback;
        _getSessionsByInstance = getSessionsByInstance;

        _protocolRouter = new ProtocolRouter(logger);
        InitializeProtocolHandlers();

        Logger.LogInformation("GameClientSession created");
    }

    private void InitializeProtocolHandlers()
    {
        // 클라이언트로부터 받는 실시간 패킷들
        _protocolRouter.RegisterHandler(Protocol.C_TO_G_CONNECT, async (bytes) => await HandleMessage<C_TO_G_CONNECT>(bytes, HandleConnect));
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

    private async Task HandleSpawnOnConnect()
    {
        if (!PlayerId.HasValue) return;

        try
        {
            // 1. 같은 인스턴스의 다른 플레이어들 정보 가져오기
            var otherSessions = _getSessionsByInstance(CurrentMapId, CurrentMapSubId);
            var otherPlayerIds = otherSessions
                .Where(s => s.PlayerId != PlayerId && s.PlayerId.HasValue)
                .Select(s => s.PlayerId.Value)
                .ToList();

            // 2. 나에게 다른 플레이어들 정보 전송 (G_TO_C_SPAWN + MAP_UPDATE)
            if (otherPlayerIds.Count > 0)
            {
                // SPAWN 패킷 전송
                var otherPlayerKeys = otherPlayerIds.Select(id => $"PLAYER_{id}").ToList();
                using var spawnPacket = PacketMaker.G_TO_C_SPAWN(otherPlayerKeys, new List<Cell>());
                Send(spawnPacket);

                // 각 플레이어의 ObjectInfo를 MAP_UPDATE로 전송
                var objectInfoList = new List<GameObjectInfo>();
                foreach (var otherId in otherPlayerIds)
                {
                    await using var playerLock = await PlayerInfo.Lock(RedLock, otherId);
                    var playerInfo = await PlayerInfo.Load(CacheHelper, otherId);
                    if (playerInfo != null)
                    {
                        objectInfoList.Add(playerInfo.ObjectInfo);
                    }
                }

                if (objectInfoList.Count > 0)
                {
                    using var mapUpdatePacket = PacketMaker.U_TO_C_MAP_UPDATE(objectInfoList, DateTime.UtcNow);
                    Send(mapUpdatePacket);
                }

                Logger.LogInformation($"Sent SPAWN + MAP_UPDATE to PlayerId={PlayerId} for {otherPlayerIds.Count} other players");
            }

            // 3. 내 ObjectInfo 로드
            await using var myPlayerLock = await PlayerInfo.Lock(RedLock, PlayerId.Value);
            var myPlayerInfo = await PlayerInfo.Load(CacheHelper, PlayerId.Value);

            if (myPlayerInfo != null)
            {
                // 4. 다른 플레이어들에게 내 정보 브로드캐스트 (G_TO_C_SPAWN + MAP_UPDATE)
                var myPlayerKey = $"PLAYER_{PlayerId}";
                using var mySpawnPacket = PacketMaker.G_TO_C_SPAWN(new List<string> { myPlayerKey }, new List<Cell>());
                using var myMapUpdatePacket = PacketMaker.U_TO_C_MAP_UPDATE(new List<GameObjectInfo> { myPlayerInfo.ObjectInfo }, DateTime.UtcNow);

                foreach (var session in otherSessions)
                {
                    if (session.PlayerId != PlayerId)
                    {
                        session.Send(mySpawnPacket);
                        session.Send(myMapUpdatePacket);
                    }
                }
                Logger.LogInformation($"Broadcasted my SPAWN + MAP_UPDATE (PlayerId={PlayerId}) to {otherSessions.Count - 1} other players");
            }
        }
        catch (Exception ex)
        {
            Logger.LogError(ex, $"Failed to handle spawn for PlayerId={PlayerId}");
        }
    }

    private async Task HandleConnect(C_TO_G_CONNECT msg)
    {
        try
        {
            Logger.LogInformation($"Client connection request: PlayerId={msg.PlayerId}, MatchingId={msg.MatchingId}");

            // TODO: MatchingId 검증 (Redis에서 매칭 정보 확인)
            // 지금은 간단하게 PlayerId만 설정

            PlayerId = msg.PlayerId;
            CurrentMapId = MapId.School; // TODO: 매칭 정보에서 가져오기
            CurrentMapSubId = msg.MatchingId;

            // 세션 등록
            _registerSessionCallback(PlayerId.Value, this);

            // 초기 위치 로드
            await using var playerLock = await PlayerInfo.Lock(RedLock, PlayerId.Value);
            var playerInfo = await PlayerInfo.Load(CacheHelper, PlayerId.Value);

            if (playerInfo != null)
            {
                _lastValidatedPosition = playerInfo.ObjectInfo.Position;
            }

            // 연결 성공 응답
            using var connectResultPacket = Packet.Create((int)Protocol.G_TO_C_CONNECT_RESULT, PlayerId.Value);
            var response = new G_TO_C_CONNECT_RESULT
            {
                Success = true,
                ErrorCode = ErrorCode.SUCCESS,
                Message = "Connected to GameServer"
            };
            connectResultPacket.SetBody(MessagePackSerializer.Serialize(response));
            Send(connectResultPacket);

            Logger.LogInformation($"Client connected successfully: PlayerId={PlayerId}");

            // SPAWN 처리: 다른 플레이어들 정보 전송 & 내 정보 브로드캐스트
            await HandleSpawnOnConnect();
        }
        catch (Exception ex)
        {
            Logger.LogError(ex, "Failed to handle connect");

            // 연결 실패 응답
            using var packet = Packet.Create((int)Protocol.G_TO_C_CONNECT_RESULT, 0);
            var response = new G_TO_C_CONNECT_RESULT
            {
                Success = false,
                ErrorCode = ErrorCode.FATAL,
                Message = ex.Message
            };
            packet.SetBody(MessagePackSerializer.Serialize(response));
            Send(packet);
        }
    }

    private async Task HandleMove(C_TO_G_MOVE msg)
    {
        if (PlayerId == null) return;

        try
        {
            var now = DateTime.UtcNow;
            var deltaTime = (float)(now - _lastMoveTime).TotalSeconds;
            _lastMoveTime = now;

            // 1. 위치 검증 (치트 방지 강화)
            var validatedPosition = ValidatePosition(msg.Position, msg.Velocity, deltaTime);

            // 2. 현재 타일 계산
            var currentCell = CoordinateConverter.WorldToCell(validatedPosition);

            // 3. 타일 변경 여부 확인 (Redis 로드 최소화)
            bool needsDbUpdate = false;
            bool cellChanged = false;
            Cell oldCell = new Cell(0, 0);

            // 주기적 저장 (1초마다) 또는 타일 변경 시에만 DB 접근
            if (now - _lastSaveTime > TimeSpan.FromSeconds(1) ||
                _lastValidatedPosition == null)
            {
                needsDbUpdate = true;
            }

            PlayerInfo? playerInfo = null;

            // 정지 중인지 확인 (velocity 크기가 거의 0)
            bool isIdle = msg.Velocity.Magnitude() < 0.01f;

            if (needsDbUpdate)
            {
                // Redis Lock + Load + Save
                await using var playerLock = await PlayerInfo.Lock(RedLock, PlayerId.Value);
                playerInfo = await PlayerInfo.Load(CacheHelper, PlayerId.Value);

                if (playerInfo != null)
                {
                    oldCell = playerInfo.ObjectInfo.CurrentCell;
                    cellChanged = !oldCell.Equals(currentCell);

                    // 위치 업데이트
                    playerInfo.ObjectInfo.Position = validatedPosition;
                    playerInfo.ObjectInfo.Velocity = msg.Velocity;
                    playerInfo.ObjectInfo.Rotation = msg.Rotation;
                    playerInfo.ObjectInfo.CurrentCell = currentCell;
                    playerInfo.ObjectInfo.MoveTimestamp = now;

                    // 저장 조건: (타일 변경) 또는 (1초 경과 && 움직이는 중)
                    // 정지 중에는 DB 저장 스킵 (불필요한 Redis 쓰기 방지)
                    bool shouldSave = cellChanged || (!isIdle && now - _lastSaveTime >= TimeSpan.FromSeconds(1));

                    if (shouldSave)
                    {
                        await playerInfo.Save(CacheHelper);
                        _lastSaveTime = now;
                    }

                    // 타일 변경 이벤트
                    if (cellChanged)
                    {
                        Logger.LogInformation($"Player {PlayerId} 타일 이동: {oldCell} → {currentCell}");
                        await OnCellChanged(playerInfo, oldCell, currentCell);
                    }
                }
            }

            // 검증된 위치 저장
            _lastValidatedPosition = validatedPosition;

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

            // 같은 인스턴스의 다른 클라이언트들에게 직접 전송
            var otherSessions = _getSessionsByInstance(CurrentMapId, CurrentMapSubId);
            foreach (var session in otherSessions)
            {
                // 자기 자신에게는 보내지 않음
                if (session.PlayerId != PlayerId)
                {
                    session.Send(packet);
                }
            }

            Logger.LogDebug($"Player {PlayerId} move broadcasted to {otherSessions.Count - 1} clients: {validatedPosition}");
        }
        catch (Exception ex)
        {
            Logger.LogError(ex, $"HandleMove error for player {PlayerId}");
        }
    }

    private Vector3f ValidatePosition(
        Vector3f clientPos,
        Vector3f velocity,
        float deltaTime)
    {
        const float MAX_SPEED = 20f; // 최대 속도 (m/s)
        const float POSITION_TOLERANCE = 1.2f; // 위치 검증 여유 (20%)
        const float MAP_MIN_X = -1000f;
        const float MAP_MAX_X = 1000f;
        const float MAP_MIN_Z = -1000f;
        const float MAP_MAX_Z = 1000f;

        // 1. 속도 제한 체크
        float speed = velocity.Magnitude();
        if (speed > MAX_SPEED)
        {
            Logger.LogWarning($"Player {PlayerId} 속도 초과: {speed:F2} > {MAX_SPEED}");
            velocity = velocity.Normalized() * MAX_SPEED;
        }

        // 2. 이동 거리 검증 (텔레포트 방지)
        if (_lastValidatedPosition != null)
        {
            var lastPos = _lastValidatedPosition;
            var delta = clientPos - lastPos;
            var distance = delta.Magnitude();
            var maxDistance = MAX_SPEED * deltaTime * POSITION_TOLERANCE;

            // 클라이언트가 물리적으로 불가능한 거리를 이동했다면
            if (distance > maxDistance && deltaTime > 0)
            {
                Logger.LogWarning(
                    $"Player {PlayerId} 텔레포트 감지: " +
                    $"distance={distance:F2}m, maxAllowed={maxDistance:F2}m, deltaTime={deltaTime:F3}s");

                // 서버 계산 위치로 보정
                var correctedPos = lastPos + velocity * deltaTime;
                clientPos = correctedPos;
            }
        }

        // 3. 맵 경계 체크
        if (clientPos.X < MAP_MIN_X) clientPos.X = MAP_MIN_X;
        if (clientPos.X > MAP_MAX_X) clientPos.X = MAP_MAX_X;
        if (clientPos.Z < MAP_MIN_Z) clientPos.Z = MAP_MIN_Z;
        if (clientPos.Z > MAP_MAX_Z) clientPos.Z = MAP_MAX_Z;

        // TODO: 4. 장애물 충돌 체크
        // if (IsCollidingWithObstacle(clientPos))
        // {
        //     clientPos = _lastValidatedPosition ?? clientPos;
        // }

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
