using game_server.services;
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

public enum PlayerState
{
    Idle,       // 일반 상태 (이동 가능)
    Exploring   // 탐색 중 (이동 불가)
}

public class GameClientSession : IPeer
{
    private readonly UserToken _token;
    private readonly SemaphoreSlim _sessionLock;
    private readonly Action<GameClientSession> _onLeaveCallback;
    private readonly IProtocolRouter _protocolRouter;
    private readonly Action<long, GameClientSession> _registerSessionCallback;
    private readonly Func<MapId, long, List<GameClientSession>> _getSessionsByInstance;
    private readonly InteractableStateManager _interactableStateManager;
    private readonly InGameInventoryManager _inGameInventoryManager;

    private readonly ILogger _logger;
    private readonly ICacheHelper _cacheHelper;
    private readonly IRedLockFactory _redLock;

    public long? PlayerId { get; private set; }
    public MapId CurrentMapId { get; private set; }
    public long CurrentMapSubId { get; private set; }
    public AreaType CurrentArea { get; private set; } = AreaType.None;
    public PlayerState CurrentState { get; private set; } = PlayerState.Idle;
    public int? CurrentExploringInteractId { get; private set; }

    private Vector3f? _lastValidatedPosition;
    private DateTime _lastMoveTime = DateTime.UtcNow;
    private DateTime _lastSaveTime = DateTime.UtcNow;
    private DateTime _lastHeartbeatTime = DateTime.UtcNow;

    // 하트비트 타임아웃 (초)
    private const int HeartbeatTimeoutSeconds = 30;

    public GameClientSession(
        UserToken token,
        IRedLockFactory redLock,
        ILogger logger,
        ICacheHelper cacheHelper,
        Action<GameClientSession> onLeaveCallback,
        Action<long, GameClientSession> registerSessionCallback,
        Func<MapId, long, List<GameClientSession>> getSessionsByInstance,
        InteractableStateManager interactableStateManager,
        InGameInventoryManager inGameInventoryManager)
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
        _interactableStateManager = interactableStateManager;
        _inGameInventoryManager = inGameInventoryManager;

        _protocolRouter = new ProtocolRouter(logger);
        InitializeProtocolHandlers();

        _logger.LogInformation("GameClientSession created");
    }

    private void InitializeProtocolHandlers()
    {
        _protocolRouter.RegisterHandler(Protocol.C_TO_G_HEART_BEAT, async (_) => await HandleHeartbeat());
        _protocolRouter.RegisterHandler(Protocol.C_TO_G_CONNECT, async (bytes) => await HandleMessage<C_TO_G_CONNECT>(bytes, HandleConnect));
        _protocolRouter.RegisterHandler(Protocol.C_TO_G_MOVE, async (bytes) => await HandleMessage<C_TO_G_MOVE>(bytes, HandleMove));
        _protocolRouter.RegisterHandler(Protocol.C_TO_G_ATTACK, async (bytes) => await HandleMessage<C_TO_G_ATTACK>(bytes, HandleAttack));
        _protocolRouter.RegisterHandler(Protocol.C_TO_G_INTERACT, async (bytes) => await HandleMessage<C_TO_G_INTERACT>(bytes, HandleInteract));
        _protocolRouter.RegisterHandler(Protocol.C_TO_G_EXPLORE_START, async (bytes) => await HandleMessage<C_TO_G_EXPLORE_START>(bytes, HandleExploreStart));
        _protocolRouter.RegisterHandler(Protocol.C_TO_G_EXPLORE_SELECT, async (bytes) => await HandleMessage<C_TO_G_EXPLORE_SELECT>(bytes, HandleExploreSelect));
        _protocolRouter.RegisterHandler(Protocol.C_TO_G_USE_INGAME_ITEM, async (bytes) => await HandleMessage<C_TO_G_USE_INGAME_ITEM>(bytes, HandleUseInGameItem));
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

    private Task HandleHeartbeat()
    {
        _lastHeartbeatTime = DateTime.UtcNow;

        // 하트비트 응답 전송
        var serverTimestamp = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
        using var packet = PacketMaker.G_TO_C_HEART_BEAT(DateTime.UtcNow);
        Send(packet);

        return Task.CompletedTask;
    }

    /// <summary>
    /// 하트비트 타임아웃 체크. 타임아웃되면 true 반환
    /// </summary>
    public bool IsHeartbeatTimedOut()
    {
        var elapsed = (DateTime.UtcNow - _lastHeartbeatTime).TotalSeconds;
        return elapsed > HeartbeatTimeoutSeconds;
    }

    /// <summary>
    /// 강제 연결 해제
    /// </summary>
    public void ForceDisconnect()
    {
        _logger.LogWarning("Force disconnecting PlayerId={PlayerId} due to heartbeat timeout", PlayerId);
        _token.Disconnect();
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

                // 초기 Area의 Interactable 목록 전송
                if (CurrentArea != AreaType.None)
                {
                    SendInteractableList(CurrentArea);
                }
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

            // 인게임 인벤토리 목록 전송
            SendInGameInventoryList();

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

        // 탐색 중에는 이동 불가
        if (CurrentState == PlayerState.Exploring)
        {
            _logger.LogDebug("Player {PlayerId} tried to move while exploring, ignoring", PlayerId);
            return;
        }

        try
        {
            var now = DateTime.UtcNow;
            var deltaTime = (float)(now - _lastMoveTime).TotalSeconds;
            _lastMoveTime = now;

            // 1. 클라이언트 Position 검증
            var validatedPosition = ValidatePosition(msg.Position, msg.Velocity, deltaTime);

            _logger.LogDebug("Player {PlayerId} C_TO_G_MOVE: ClientPos=({CX},{CY}), Velocity=({VX},{VY}), ValidatedPos=({X},{Y})",
                PlayerId, msg.Position.X, msg.Position.Y, msg.Velocity.X, msg.Velocity.Y,
                validatedPosition.X, validatedPosition.Y);

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
            var currentCell = WorldPositionToCell(validatedPosition);
            var newArea = GameMapData.GetCurrentArea(CurrentMapId, currentCell);

            _logger.LogDebug("Player {PlayerId} WorldToCell: Pos=({PX},{PY}) → Cell=({CX},{CY}) → Area={Area}",
                PlayerId, validatedPosition.X, validatedPosition.Y, currentCell.X, currentCell.Y, newArea);

            // Area 변경 시 진입/퇴장 이벤트 전송
            if (newArea != CurrentArea)
            {
                _logger.LogInformation("Player {PlayerId} Area change at Cell({CellX},{CellY}): {OldArea} → {NewArea}",
                    PlayerId, currentCell.X, currentCell.Y, CurrentArea, newArea);
                var oldArea = CurrentArea;
                CurrentArea = newArea; // 먼저 Area 업데이트 (다른 플레이어의 MOVE 수신 가능하도록)
                await HandleAreaChange(oldArea, newArea);
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

    /// <summary>
    /// 클라이언트 Position 검증 (치트 방지)
    /// 정상이면 클라이언트 Position 사용, 비정상이면 서버 계산 Position 사용
    /// </summary>
    private Vector3f ValidatePosition(Vector3f clientPos, Vector3f velocity, float deltaTime)
    {
        const float maxSpeed = 10f; // 최대 속도 (units/s)
        const float tolerance = 1.5f; // 허용 오차 (50%)
        const float mapMinX = -1000f;
        const float mapMaxX = 1000f;
        const float mapMinY = -1000f;
        const float mapMaxY = 1000f;

        // null 체크: 클라이언트 데이터가 null이면 마지막 유효 위치 또는 기본값 반환
        if (clientPos == null || velocity == null)
        {
            _logger.LogWarning("Player {PlayerId} ValidatePosition: null data received (clientPos={ClientPos}, velocity={Velocity})",
                PlayerId, clientPos == null ? "null" : "ok", velocity == null ? "null" : "ok");
            return _lastValidatedPosition ?? new Vector3f(0, 0, 0);
        }

        // Z값은 항상 0으로 고정
        clientPos.Z = 0;

        // 1. 속도 제한 체크
        var speed = velocity.Magnitude();
        if (speed > maxSpeed)
        {
            _logger.LogWarning("Player {PlayerId} 속도 초과: {Speed:F2} > {MaxSpeed}", PlayerId, speed, maxSpeed);
            // 클라이언트 위치를 신뢰하지 않고 서버 계산 위치 사용
            if (_lastValidatedPosition != null)
            {
                var correctedVelocity = velocity.Normalized() * maxSpeed;
                return new Vector3f(
                    _lastValidatedPosition.X + correctedVelocity.X * deltaTime,
                    _lastValidatedPosition.Y + correctedVelocity.Y * deltaTime,
                    0
                );
            }
        }

        // 2. 이동 거리 검증 (텔레포트 방지)
        if (_lastValidatedPosition != null && deltaTime > 0)
        {
            var delta = clientPos - _lastValidatedPosition;
            var distance = delta.Magnitude();
            var maxDistance = maxSpeed * deltaTime * tolerance;

            if (distance > maxDistance)
            {
                _logger.LogWarning("Player {PlayerId} 텔레포트 감지: distance={Distance:F2}, maxAllowed={MaxDistance:F2}",
                    PlayerId, distance, maxDistance);
                // 서버 계산 위치로 보정
                return new Vector3f(
                    _lastValidatedPosition.X + velocity.X * deltaTime,
                    _lastValidatedPosition.Y + velocity.Y * deltaTime,
                    0
                );
            }
        }

        // 3. 맵 경계 체크
        if (clientPos.X < mapMinX) clientPos.X = mapMinX;
        if (clientPos.X > mapMaxX) clientPos.X = mapMaxX;
        if (clientPos.Y < mapMinY) clientPos.Y = mapMinY;
        if (clientPos.Y > mapMaxY) clientPos.Y = mapMaxY;

        // 검증 통과: 클라이언트 Position 사용
        return clientPos;
    }

    #region Isometric 좌표 변환 (Unity Isometric Z as Y 타일맵)

    /// <summary>
    /// World Position을 Cell 좌표로 변환
    /// Unity Isometric Z as Y 타일맵의 WorldToCell과 동일한 로직
    ///
    /// 클라이언트 MapController.WorldToCell:
    ///   var unityCell = tileMap.WorldToCell(position);
    ///   return new Vector3Int(unityCell.x + CellOffsetX, unityCell.y + CellOffsetY + 1, 0);
    ///
    /// Unity Isometric Z as Y 역변환:
    ///   unityCellX = floor(WorldX + 2 * WorldY)
    ///   unityCellY = floor(2 * WorldY - WorldX)
    ///
    /// 최종 Cell = unityCell + CellOffset (Y는 +1 추가)
    /// </summary>
    private static Cell WorldPositionToCell(Vector3f worldPos)
    {
        // Unity Isometric Z as Y 역변환 공식
        // Unity WorldToCell 결과를 그대로 반환 (CellOffset은 map_region.csv에 이미 반영됨)
        int cellX = (int)Math.Floor(worldPos.X + 2f * worldPos.Y);
        int cellY = (int)Math.Floor(2f * worldPos.Y - worldPos.X);

        return new Cell(cellX, cellY);
    }

    #endregion

    private async Task HandleAreaChange(AreaType oldArea, AreaType newArea)
    {
        try
        {
            if (!PlayerId.HasValue) return;

            _logger.LogInformation("Player {PlayerId} moved from Area {OldArea} to {NewArea}", PlayerId, oldArea, newArea);

            var allSessions = _getSessionsByInstance(CurrentMapId, CurrentMapSubId);
            var playerInfo = await PlayerInfo.Load(_cacheHelper, PlayerId.Value);

            if (playerInfo == null) return;

            // 내 최신 위치로 playerInfo 업데이트
            if (_lastValidatedPosition != null)
            {
                var latestCell = WorldPositionToCell(_lastValidatedPosition);
                playerInfo.ObjectInfo.Position = _lastValidatedPosition;
                playerInfo.ObjectInfo.Cell = latestCell;
                playerInfo.LastCell = latestCell;
            }

            // 1. 이전 Area의 플레이어들에게 퇴장 알림 + 나에게 기존 플레이어 삭제 알림
            if (oldArea != AreaType.None)
            {
                var oldAreaSessions = allSessions.Where(s => s.PlayerId != PlayerId && s.CurrentArea == oldArea).ToList();
                using var leavePacket = PacketMaker.G_TO_C_AREA_PLAYER_LEAVE(PlayerId.Value);

                foreach (var session in oldAreaSessions)
                {
                    // 이전 Area 플레이어들에게 내 퇴장 알림
                    session.Send(leavePacket);

                    // 나에게 이전 Area 플레이어들 삭제 알림
                    if (session.PlayerId.HasValue)
                    {
                        using var removePacket = PacketMaker.G_TO_C_AREA_PLAYER_LEAVE(session.PlayerId.Value);
                        Send(removePacket);
                    }
                }

                _logger.LogDebug("Sent LEAVE to {Count} players in old Area {OldArea}, removed them from my view",
                    oldAreaSessions.Count, oldArea);
            }

            // 2. 새 Area의 플레이어들에게 진입 알림 (내 최신 Cell 포함)
            if (newArea != AreaType.None)
            {
                var newAreaSessions = allSessions.Where(s => s.PlayerId != PlayerId && s.CurrentArea == newArea).ToList();
                var myCell = _lastValidatedPosition != null
                    ? WorldPositionToCell(_lastValidatedPosition)
                    : playerInfo.ObjectInfo.Cell;
                using var enterPacket = PacketMaker.G_TO_C_AREA_PLAYER_ENTER(playerInfo, myCell);

                foreach (var session in newAreaSessions)
                {
                    session.Send(enterPacket);
                }

                _logger.LogDebug("Sent ENTER to {Count} players in new Area {NewArea}", newAreaSessions.Count, newArea);

                // 3. 나에게 새 Area의 다른 플레이어 정보 전송 (세션의 최신 Cell 사용)
                foreach (var session in newAreaSessions)
                {
                    if (!session.PlayerId.HasValue) continue;

                    var otherPlayerInfo = await PlayerInfo.Load(_cacheHelper, session.PlayerId.Value);
                    if (otherPlayerInfo != null)
                    {
                        // 세션의 최신 위치에서 Cell 계산 (없으면 캐시된 Cell 사용)
                        var otherCell = session._lastValidatedPosition != null
                            ? WorldPositionToCell(session._lastValidatedPosition)
                            : otherPlayerInfo.ObjectInfo.Cell;

                        _logger.LogInformation("Sending Player {OtherId} to Player {MyId}: Cell=({CellX},{CellY})",
                            session.PlayerId, PlayerId, otherCell.X, otherCell.Y);

                        using var otherEnterPacket = PacketMaker.G_TO_C_AREA_PLAYER_ENTER(otherPlayerInfo, otherCell);
                        Send(otherEnterPacket);
                    }
                }

                _logger.LogDebug("Sent {Count} existing players to Player {PlayerId}", newAreaSessions.Count, PlayerId);

                // 4. 나에게 새 Area의 Interactable 목록 전송
                SendInteractableList(newArea);
            }
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "HandleAreaChange error for player {PlayerId}", PlayerId);
        }
    }

    private void SendInteractableList(AreaType areaType)
    {
        var objects = _interactableStateManager.GetAreaObjectStates(CurrentMapSubId, areaType);
        if (objects.Count == 0)
        {
            _logger.LogDebug("No interactable objects in area {AreaType}", areaType);
            return;
        }

        using var packet = PacketMaker.G_TO_C_INTERACTABLE_LIST(areaType, objects);
        Send(packet);
        _logger.LogDebug("Sent {Count} interactable objects for area {AreaType} to Player {PlayerId} (MatchingId={MatchingId})",
            objects.Count, areaType, PlayerId, CurrentMapSubId);
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

    private Task HandleExploreStart(C_TO_G_EXPLORE_START msg)
    {
        if (!PlayerId.HasValue) return Task.CompletedTask;

        // 이미 탐색 중이면 무시
        if (CurrentState == PlayerState.Exploring)
        {
            _logger.LogWarning("Player {PlayerId} already exploring, ignoring explore start", PlayerId);
            return Task.CompletedTask;
        }

        _logger.LogInformation("Player {PlayerId} started exploring InteractId={InteractId}", PlayerId, msg.InteractId);

        // 상태 변경
        CurrentState = PlayerState.Exploring;
        CurrentExploringInteractId = msg.InteractId;

        // 같은 Area의 다른 플레이어들에게 탐색 시작 브로드캐스트
        var allSessions = _getSessionsByInstance(CurrentMapId, CurrentMapSubId);
        var sameAreaSessions = allSessions.Where(s => s.PlayerId != PlayerId && s.CurrentArea == CurrentArea).ToList();

        using var packet = PacketMaker.G_TO_C_EXPLORE_START(PlayerId.Value, msg.InteractId);
        foreach (var session in sameAreaSessions)
        {
            session.Send(packet);
        }

        _logger.LogDebug("Broadcasted EXPLORE_START to {Count} players in Area {Area}", sameAreaSessions.Count, CurrentArea);

        return Task.CompletedTask;
    }

    private Task HandleExploreSelect(C_TO_G_EXPLORE_SELECT msg)
    {
        if (!PlayerId.HasValue) return Task.CompletedTask;

        // 탐색 중이 아니거나 다른 오브젝트를 탐색 중이면 무시
        if (CurrentState != PlayerState.Exploring || CurrentExploringInteractId != msg.InteractId)
        {
            _logger.LogWarning("Player {PlayerId} invalid explore select: State={State}, ExploringId={ExploringId}, RequestedId={RequestedId}",
                PlayerId, CurrentState, CurrentExploringInteractId, msg.InteractId);

            // 에러 응답
            SendExploreResult(false, msg.InteractId, msg.ActionId, 0, ErrorCode.FATAL);
            return Task.CompletedTask;
        }

        _logger.LogInformation("Player {PlayerId} selected action: InteractId={InteractId}, ActionId={ActionId}",
            PlayerId, msg.InteractId, msg.ActionId);

        // 탐색 처리 (InteractableStateManager에서 상태 업데이트 - MatchingId별 독립 관리)
        var success = _interactableStateManager.TryExplore(CurrentMapSubId, msg.InteractId, msg.ActionId, PlayerId.Value, out var state);

        // CSV에서 보상 정보 가져오기
        var interactable = GameInteractableData.Get(msg.InteractId);
        var action = interactable?.Actions.FirstOrDefault(a => a.ActionId == msg.ActionId);
        var rewardType = action?.RewardType ?? RewardType.NONE;
        var rewardId = action?.RewardId ?? 0;

        if (success)
        {
            _logger.LogInformation("Player {PlayerId} explored InteractId={InteractId}, ActionId={ActionId} successfully",
                PlayerId, msg.InteractId, msg.ActionId);

            // 보상 처리 및 최종 아이템 ID 결정
            var rewardItemId = ProcessExploreReward(rewardType, rewardId);

            // 성공 응답 (최종 결정된 아이템 ID 전송)
            SendExploreResult(true, msg.InteractId, msg.ActionId, rewardItemId, ErrorCode.SUCCESS);

            // 같은 Area의 모든 플레이어에게 상태 업데이트 브로드캐스트
            BroadcastInteractableUpdate(msg.InteractId, msg.ActionId, true, PlayerId.Value);
        }
        else
        {
            _logger.LogInformation("Player {PlayerId} tried to explore already explored action: InteractId={InteractId}, ActionId={ActionId}",
                PlayerId, msg.InteractId, msg.ActionId);

            // 이미 탐색됨 - 아이템 없이 성공 응답 (결과 표시만)
            SendExploreResult(true, msg.InteractId, msg.ActionId, 0, ErrorCode.SUCCESS);
        }

        // 탐색 종료 처리
        EndExplore();

        return Task.CompletedTask;
    }

    private void SendExploreResult(bool success, int interactId, int actionId, int itemId, ErrorCode errorCode)
    {
        if (!PlayerId.HasValue) return;

        using var packet = PacketMaker.G_TO_C_EXPLORE_RESULT(success, interactId, actionId, itemId, errorCode);
        Send(packet);
    }

    /// <summary>
    /// 탐색 보상 처리 - 최종 결정된 아이템 ID 반환
    /// </summary>
    /// <returns>획득한 아이템 ID (0이면 아이템 없음)</returns>
    private int ProcessExploreReward(RewardType rewardType, int rewardId)
    {
        int resultItemId = 0;

        switch (rewardType)
        {
            case RewardType.ITEM:
                // 고정 아이템 지급
                if (rewardId > 0)
                {
                    AddInGameItem(rewardId);
                    resultItemId = rewardId;
                }
                break;

            case RewardType.CONDITION_RANDOM:
                // 컨디션 버프를 가진 아이템 풀에서 랜덤 선택
                var conditionItem = GameItemData.GetRandomConsumableByBuffSubType(BuffSubType.CONDITION_ADD);
                if (conditionItem != null)
                {
                    AddInGameItem(conditionItem.Id);
                    resultItemId = conditionItem.Id;
                    _logger.LogInformation("Player {PlayerId} received CONDITION_RANDOM reward: ItemId={ItemId}", PlayerId, conditionItem.Id);
                }
                else
                {
                    _logger.LogWarning("Player {PlayerId} CONDITION_RANDOM reward failed: no items available", PlayerId);
                }
                break;

            case RewardType.CORRUPTION_RANDOM:
                // 오염도 버프를 가진 아이템 풀에서 랜덤 선택
                var corruptionItem = GameItemData.GetRandomConsumableByBuffSubType(BuffSubType.CORRUPTION_DOWN);
                if (corruptionItem != null)
                {
                    AddInGameItem(corruptionItem.Id);
                    resultItemId = corruptionItem.Id;
                    _logger.LogInformation("Player {PlayerId} received CORRUPTION_RANDOM reward: ItemId={ItemId}", PlayerId, corruptionItem.Id);
                }
                else
                {
                    _logger.LogWarning("Player {PlayerId} CORRUPTION_RANDOM reward failed: no items available", PlayerId);
                }
                break;

            case RewardType.RULE:
                // 규칙은 아이템이 아님
                _logger.LogDebug("Player {PlayerId} received RULE reward: {RewardId}", PlayerId, rewardId);
                break;

            case RewardType.RULE_RANDOM:
                // 규칙은 아이템이 아님
                _logger.LogDebug("Player {PlayerId} received RULE_RANDOM reward", PlayerId);
                break;

            case RewardType.NONE:
            default:
                break;
        }

        return resultItemId;
    }

    private void BroadcastInteractableUpdate(int interactId, int actionId, bool isExplored, long exploredBy)
    {
        var allSessions = _getSessionsByInstance(CurrentMapId, CurrentMapSubId);
        var sameAreaSessions = allSessions.Where(s => s.CurrentArea == CurrentArea).ToList();

        using var packet = PacketMaker.G_TO_C_INTERACTABLE_UPDATE(interactId, actionId, isExplored, exploredBy);
        foreach (var session in sameAreaSessions)
        {
            session.Send(packet);
        }

        _logger.LogDebug("Broadcasted INTERACTABLE_UPDATE to {Count} players in Area {Area}", sameAreaSessions.Count, CurrentArea);
    }

    private void EndExplore()
    {
        if (!PlayerId.HasValue) return;

        // 상태 복원
        CurrentState = PlayerState.Idle;
        CurrentExploringInteractId = null;

        // 같은 Area의 다른 플레이어들에게 탐색 종료 브로드캐스트
        var allSessions = _getSessionsByInstance(CurrentMapId, CurrentMapSubId);
        var sameAreaSessions = allSessions.Where(s => s.PlayerId != PlayerId && s.CurrentArea == CurrentArea).ToList();

        using var packet = PacketMaker.G_TO_C_EXPLORE_END(PlayerId.Value);
        foreach (var session in sameAreaSessions)
        {
            session.Send(packet);
        }

        _logger.LogDebug("Broadcasted EXPLORE_END to {Count} players in Area {Area}", sameAreaSessions.Count, CurrentArea);
    }

    #region 인게임 인벤토리

    /// <summary>
    /// 인게임 인벤토리 전체 목록 전송
    /// </summary>
    private void SendInGameInventoryList()
    {
        if (!PlayerId.HasValue) return;

        var items = _inGameInventoryManager.GetAllItems(CurrentMapSubId, PlayerId.Value);
        using var packet = PacketMaker.G_TO_C_INGAME_INVENTORY_LIST(items);
        Send(packet);

        _logger.LogDebug("Sent InGameInventory list to PlayerId={PlayerId}, ItemCount={Count}", PlayerId, items.Count);
    }

    /// <summary>
    /// 인게임 인벤토리 업데이트 전송 (아이템 추가/제거 시)
    /// </summary>
    private void SendInGameInventoryUpdate(InGameItemInfo item)
    {
        if (!PlayerId.HasValue) return;

        using var packet = PacketMaker.G_TO_C_INGAME_INVENTORY_UPDATE([item]);
        Send(packet);

        _logger.LogDebug("Sent InGameInventory update to PlayerId={PlayerId}, ItemUid={ItemUid}, ItemId={ItemId}, Count={Count}",
            PlayerId, item.ItemUid, item.ItemId, item.Count);
    }

    /// <summary>
    /// 인게임 아이템 추가 (탐색 보상 등)
    /// </summary>
    private void AddInGameItem(int itemId, int count = 1)
    {
        if (!PlayerId.HasValue) return;

        var item = _inGameInventoryManager.AddItem(CurrentMapSubId, PlayerId.Value, itemId, count);
        SendInGameInventoryUpdate(item);
    }

    /// <summary>
    /// 인게임 아이템 사용 요청 처리
    /// </summary>
    private Task HandleUseInGameItem(C_TO_G_USE_INGAME_ITEM msg)
    {
        if (!PlayerId.HasValue) return Task.CompletedTask;

        var success = _inGameInventoryManager.TryRemoveItem(CurrentMapSubId, PlayerId.Value, msg.ItemUid, msg.Count, out var updatedItem);

        if (success && updatedItem != null)
        {
            // 아이템 사용 성공 - 인벤토리 업데이트 전송
            SendInGameInventoryUpdate(updatedItem);

            // 사용 결과 전송
            using var resultPacket = PacketMaker.G_TO_C_USE_INGAME_ITEM_RESULT(true, msg.ItemUid, ErrorCode.SUCCESS);
            Send(resultPacket);

            _logger.LogInformation("Player {PlayerId} used InGameItem: ItemUid={ItemUid}, Count={Count}", PlayerId, msg.ItemUid, msg.Count);
        }
        else
        {
            // 아이템 사용 실패
            using var resultPacket = PacketMaker.G_TO_C_USE_INGAME_ITEM_RESULT(false, msg.ItemUid, ErrorCode.FATAL);
            Send(resultPacket);

            _logger.LogWarning("Player {PlayerId} failed to use InGameItem: ItemUid={ItemUid}, Count={Count}", PlayerId, msg.ItemUid, msg.Count);
        }

        return Task.CompletedTask;
    }

    #endregion

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
