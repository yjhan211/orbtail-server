using MessagePack;
using Microsoft.Extensions.Logging;
using network.common;
using network.common.data;
using network.common.data.models;
using network.core;
using network.interfaces;
using network.packets;
using network.routing;
using network.utils;

namespace game_server.network;

public class GameClientSession : IPeer
{
    private readonly UserToken _token;
    private readonly SemaphoreSlim _sessionLock;
    private readonly Action<GameClientSession> _onLeaveCallback;
    private readonly IProtocolRouter _protocolRouter;
    private readonly Action<long, GameClientSession> _registerSessionCallback;
    private readonly Func<MapId, long, List<GameClientSession>> _getSessionsByInstance;

    private readonly ILogger _logger;
    private readonly ICacheHelper _cacheHelper;
    private readonly IRedLockFactory _redLock;

    public long? PlayerId { get; private set; }
    public MapId CurrentMapId { get; private set; }
    public long CurrentMapSubId { get; private set; }
    public AreaType CurrentArea { get; private set; } = AreaType.None;

    private Vector3f? _lastValidatedPosition;
    private DateTime _lastMoveTime = DateTime.UtcNow;
    private DateTime _lastSaveTime = DateTime.UtcNow;
    private bool _isFirstMove = true;

    public GameClientSession(
        UserToken token,
        IRedLockFactory redLock,
        ILogger logger,
        ICacheHelper cacheHelper,
        Action<GameClientSession> onLeaveCallback,
        Action<long, GameClientSession> registerSessionCallback,
        Func<MapId, long, List<GameClientSession>> getSessionsByInstance)
    {
        _token = token;
        _token.SetPeer(this);
        _sessionLock = new SemaphoreSlim(1);

        _redLock = redLock;
        _logger = logger;
        _cacheHelper = cacheHelper;
        _onLeaveCallback = onLeaveCallback;
        _registerSessionCallback = registerSessionCallback;
        _getSessionsByInstance = getSessionsByInstance;

        _protocolRouter = new ProtocolRouter(logger);
        InitializeProtocolHandlers();

        _logger.LogInformation("GameClientSession created");
    }

    private void InitializeProtocolHandlers()
    {
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

            _logger.LogInformation("[GameClient] Protocol: {ProtocolId}, PlayerId: {L}", protocolId, playerId);

            await _protocolRouter.RouteAsync(protocolId, body);
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

    private static async Task HandleMessage<T>(byte[] body, Func<T, Task> handler) where T : IMessagePackObject
    {
        var message = MessagePackSerializer.Deserialize<T>(body);
        await handler(message);
    }

    private async Task BroadcastPlayerJoin()
    {
        if (!PlayerId.HasValue)
        {
            return;
        }

        try
        {
            var allSessions = _getSessionsByInstance(CurrentMapId, CurrentMapSubId);
            // 같은 Area의 플레이어만 필터링
            var sameAreaSessions = allSessions.Where(s => s.PlayerId != PlayerId && s.CurrentArea == CurrentArea && s.PlayerId.HasValue).ToList();

            // 1. 나에게 같은 Area의 다른 플레이어들 정보 전송
            if (sameAreaSessions.Count > 0)
            {
                var playerInfoList = new List<PlayerInfo>();
                foreach (var session in sameAreaSessions)
                {
                    await using var playerLock = await PlayerInfo.Lock(_redLock, session.PlayerId!.Value);
                    var playerInfo = await PlayerInfo.Load(_cacheHelper, session.PlayerId!.Value);
                    if (playerInfo != null)
                    {
                        playerInfoList.Add(playerInfo);
                    }
                }

                if (playerInfoList.Count > 0)
                {
                    using var packet = PacketMaker.G_TO_C_PLAYER_INFO(playerInfoList);
                    Send(packet);
                    _logger.LogInformation("Sent {Count} PlayerInfo in Area {Area} to PlayerId={L}", playerInfoList.Count, CurrentArea, PlayerId);
                }
            }

            // 2. 내 정보 로드
            await using var myPlayerLock = await PlayerInfo.Lock(_redLock, PlayerId.Value);
            var myPlayerInfo = await PlayerInfo.Load(_cacheHelper, PlayerId.Value);

            if (myPlayerInfo != null)
            {
                // 3. 같은 Area의 다른 플레이어들에게 내 정보 브로드캐스트
                using var myPacket = PacketMaker.G_TO_C_PLAYER_INFO([myPlayerInfo]);
                foreach (var session in sameAreaSessions)
                {
                    session.Send(myPacket);
                }
                _logger.LogInformation("Broadcasted my PlayerInfo (PlayerId={L}) to {Count} players in Area {Area}", PlayerId, sameAreaSessions.Count, CurrentArea);
            }
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to broadcast player join for PlayerId={L}", PlayerId);
        }
    }

    private async Task HandleConnect(C_TO_G_CONNECT msg)
    {
        try
        {
            _logger.LogInformation("Client connection request: PlayerId={MsgPlayerId}, MatchingId={MsgMatchingId}", msg.PlayerId, msg.MatchingId);

            // TODO: MatchingId 검증 (Redis에서 매칭 정보 확인)
            // 지금은 간단하게 PlayerId만 설정

            PlayerId = msg.PlayerId;
            CurrentMapId = MapId.School; // TODO: 매칭 정보에서 가져오기
            CurrentMapSubId = msg.MatchingId;

            // 세션 등록
            _registerSessionCallback(PlayerId.Value, this);

            // 초기 위치 로드
            await using var playerLock = await PlayerInfo.Lock(_redLock, PlayerId.Value);
            var playerInfo = await PlayerInfo.Load(_cacheHelper, PlayerId.Value);

            if (playerInfo != null)
            {
                _lastValidatedPosition = playerInfo.ObjectInfo.Position;
                // 초기 Area 설정
                CurrentArea = GameMapData.GetCurrentArea(CurrentMapId, playerInfo.ObjectInfo.Cell);
                _logger.LogInformation("Player {PlayerId} initial Area: {Area}", PlayerId, CurrentArea);
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

            _logger.LogInformation("Client connected successfully: PlayerId={L}", PlayerId);

            // 다른 플레이어들 정보 전송 & 내 정보 브로드캐스트
            await BroadcastPlayerJoin();
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to handle connect");

            // 연결 실패 응답
            using var packet = Packet.Create((int)Protocol.G_TO_C_CONNECT_RESULT);
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

            _logger.LogDebug("Player {PlayerId} C_TO_G_MOVE: Position=({X},{Y},{Z}), Velocity=({VX},{VY},{VZ})",
                PlayerId, msg.Position.X, msg.Position.Y, msg.Position.Z, msg.Velocity.X, msg.Velocity.Y, msg.Velocity.Z);

            // 1. 위치 검증 (간단한 치트 방지)
            var validatedPosition = ValidatePosition(msg.Position, msg.Velocity, deltaTime);

            _logger.LogDebug("Player {PlayerId} validatedPosition=({X},{Y},{Z})",
                PlayerId, validatedPosition.X, validatedPosition.Y, validatedPosition.Z);

            // 2. 주기적 저장 (1초마다)
            var needsDbUpdate = now - _lastSaveTime > TimeSpan.FromSeconds(1) || _lastValidatedPosition == null;
            var isIdle = msg.Velocity.Magnitude() < 0.01f;

            if (needsDbUpdate && !isIdle)
            {
                await using var playerLock = await PlayerInfo.Lock(_redLock, PlayerId.Value);
                var playerInfo = await PlayerInfo.Load(_cacheHelper, PlayerId.Value);

                if (playerInfo != null)
                {
                    playerInfo.ObjectInfo.Position = validatedPosition;
                    playerInfo.ObjectInfo.Velocity = msg.Velocity;
                    playerInfo.ObjectInfo.Rotation = msg.Rotation;
                    playerInfo.ObjectInfo.MoveTimestamp = now;
                    playerInfo.ObjectInfo.UpdateCellFromPosition(); // Position에서 Cell 자동 계산

                    await playerInfo.Save(_cacheHelper);
                    _lastSaveTime = now;
                }
            }

            _lastValidatedPosition = validatedPosition;

            // 3. Area 체크 및 변경 감지
            var serverTimestamp = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
            var currentCell = new Cell(
                (int)Math.Round(validatedPosition.X),
                (int)Math.Round(validatedPosition.Y)  // 2D 게임: Y축이 세로
            );
            var newArea = GameMapData.GetCurrentArea(CurrentMapId, currentCell);

            // Area 변경 시 진입/퇴장 이벤트 전송
            if (newArea != CurrentArea)
            {
                _logger.LogInformation("Player {PlayerId} Area change at Cell({CellX},{CellY}): {OldArea} → {NewArea}",
                    PlayerId, currentCell.X, currentCell.Y, CurrentArea, newArea);
                await HandleAreaChange(CurrentArea, newArea);
                CurrentArea = newArea;
            }

            // 4. 브로드캐스트 (같은 Area의 플레이어에게만 전송)
            using var packet = PacketMaker.G_TO_C_MOVE(
                PlayerId.Value,
                validatedPosition,
                msg.Velocity,
                msg.Rotation,
                currentCell,
                msg.InputSequence,
                serverTimestamp
            );

            var otherSessions = _getSessionsByInstance(CurrentMapId, CurrentMapSubId);
            var sameAreaSessions = otherSessions.Where(s => s.PlayerId != PlayerId && s.CurrentArea == CurrentArea).ToList();

            foreach (var session in sameAreaSessions)
            {
                session.Send(packet);
            }

            _logger.LogDebug("Player {L} move broadcasted to {Count} clients in Area {Area}", PlayerId, sameAreaSessions.Count, CurrentArea);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, $"HandleMove error for player {PlayerId}");
        }
    }

    private Vector3f ValidatePosition(Vector3f clientPos, Vector3f velocity, float deltaTime)
    {
        const float maxSpeed = 20f; // 최대 속도 (m/s)
        const float positionTolerance = 1.2f; // 위치 검증 여유 (20%)
        const float mapMinX = -1000f;
        const float mapMaxX = 1000f;
        const float mapMinZ = -1000f;
        const float mapMaxZ = 1000f;

        // 1. 속도 제한 체크
        var speed = velocity.Magnitude();
        if (speed > maxSpeed)
        {
            _logger.LogWarning($"Player {PlayerId} 속도 초과: {speed:F2} > {maxSpeed}");
            velocity = velocity.Normalized() * maxSpeed;
        }

        // 2. 이동 거리 검증 (텔레포트 방지) - 첫 이동은 건너뜀
        if (_lastValidatedPosition != null && !_isFirstMove)
        {
            var lastPos = _lastValidatedPosition;
            var delta = clientPos - lastPos;
            var distance = delta.Magnitude();
            var maxDistance = maxSpeed * deltaTime * positionTolerance;

            // 클라이언트가 물리적으로 불가능한 거리를 이동했다면
            if (distance > maxDistance && deltaTime > 0)
            {
                _logger.LogWarning($"Player {PlayerId} 텔레포트 감지: " + $"distance={distance:F2}m, maxAllowed={maxDistance:F2}m, deltaTime={deltaTime:F3}s");

                // 서버 계산 위치로 보정
                var correctedPos = lastPos + velocity * deltaTime;
                clientPos = correctedPos;
            }
        }

        // 첫 이동 플래그 해제
        if (_isFirstMove)
        {
            _isFirstMove = false;
        }

        // 3. 맵 경계 체크
        if (clientPos.X < mapMinX) clientPos.X = mapMinX;
        if (clientPos.X > mapMaxX) clientPos.X = mapMaxX;
        if (clientPos.Z < mapMinZ) clientPos.Z = mapMinZ;
        if (clientPos.Z > mapMaxZ) clientPos.Z = mapMaxZ;

        // TODO: 4. 장애물 충돌 체크
        // if (IsCollidingWithObstacle(clientPos))
        // {
        //     clientPos = _lastValidatedPosition ?? clientPos;
        // }

        return clientPos;
    }

    private async Task HandleAreaChange(AreaType oldArea, AreaType newArea)
    {
        try
        {
            if (!PlayerId.HasValue) return;

            _logger.LogInformation("Player {PlayerId} moved from Area {OldArea} to {NewArea}", PlayerId, oldArea, newArea);

            var allSessions = _getSessionsByInstance(CurrentMapId, CurrentMapSubId);
            var playerInfo = await PlayerInfo.Load(_cacheHelper, PlayerId.Value);

            if (playerInfo == null) return;

            // 1. 이전 Area의 플레이어들에게 퇴장 알림
            if (oldArea != AreaType.None)
            {
                var oldAreaSessions = allSessions.Where(s => s.PlayerId != PlayerId && s.CurrentArea == oldArea).ToList();
                using var leavePacket = PacketMaker.G_TO_C_AREA_PLAYER_LEAVE(PlayerId.Value);

                foreach (var session in oldAreaSessions)
                {
                    session.Send(leavePacket);
                }

                _logger.LogDebug("Sent LEAVE to {Count} players in old Area {OldArea}", oldAreaSessions.Count, oldArea);
            }

            // 2. 새 Area의 플레이어들에게 진입 알림
            if (newArea != AreaType.None)
            {
                var newAreaSessions = allSessions.Where(s => s.PlayerId != PlayerId && s.CurrentArea == newArea).ToList();
                using var enterPacket = PacketMaker.G_TO_C_AREA_PLAYER_ENTER(playerInfo);

                foreach (var session in newAreaSessions)
                {
                    session.Send(enterPacket);
                }

                _logger.LogDebug("Sent ENTER to {Count} players in new Area {NewArea}", newAreaSessions.Count, newArea);

                // 3. 나에게 새 Area의 다른 플레이어 정보 전송
                foreach (var session in newAreaSessions)
                {
                    if (!session.PlayerId.HasValue) continue;

                    var otherPlayerInfo = await PlayerInfo.Load(_cacheHelper, session.PlayerId.Value);
                    if (otherPlayerInfo != null)
                    {
                        using var otherEnterPacket = PacketMaker.G_TO_C_AREA_PLAYER_ENTER(otherPlayerInfo);
                        Send(otherEnterPacket);
                    }
                }

                _logger.LogDebug("Sent {Count} existing players to Player {PlayerId}", newAreaSessions.Count, PlayerId);
            }
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "HandleAreaChange error for player {PlayerId}", PlayerId);
        }
    }


    private Task HandleAttack(C_TO_G_ATTACK msg)
    {
        _logger.LogInformation($"Player {PlayerId} attack: {msg.TargetId}");
        // TODO: 공격 처리
        return Task.CompletedTask;
    }

    private Task HandleInteract(C_TO_G_INTERACT msg)
    {
        _logger.LogInformation($"Player {PlayerId} interact: {msg.TargetId}");
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
        _logger.LogInformation($"GameClient removed: PlayerId={PlayerId}");
        _onLeaveCallback(this);
    }

    public void OnDisconnect()
    {
        _logger.LogInformation($"GameClient disconnected: PlayerId={PlayerId}");
        _onLeaveCallback(this);
    }

    public Task<UserToken?> Release()
    {
        return Task.FromResult<UserToken?>(_token);
    }
}
