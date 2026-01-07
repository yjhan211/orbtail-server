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
    private readonly AreaRuleManager _areaRuleManager;
    private readonly ExitInstanceManager _exitInstanceManager;
    private readonly ItemPoolManager _itemPoolManager;
    private readonly CorridorRuleManager _corridorRuleManager;
    private readonly InteractRuleManager _interactRuleManager;
    private readonly DoorStateManager _doorStateManager;

    private readonly ILogger _logger;
    private readonly ICacheHelper _cacheHelper;
    private readonly IRedLockFactory _redLock;

    public long? PlayerId { get; private set; }
    public MapId CurrentMapId { get; private set; }
    public long CurrentMapSubId { get; private set; }
    public AreaType CurrentArea { get; private set; } = AreaType.None;
    public PlayerState CurrentState { get; private set; } = PlayerState.Idle;
    public int? CurrentExploringInteractId { get; private set; }

    // 인게임 스탯 (게임 종료 시 초기화)
    public int Stamina { get; private set; } = 100;
    public int Corruption { get; private set; } = 0;
    private const int MaxStamina = 100;
    private const int MaxCorruption = 100;

    private Vector3f? _lastValidatedPosition;
    private Cell? _lastValidCell;
    private DateTime _lastMoveTime = DateTime.UtcNow;
    private DateTime _lastSaveTime = DateTime.UtcNow;
    private DateTime _lastHeartbeatTime = DateTime.UtcNow;

    // 하트비트 타임아웃 (초)
    private const int HeartbeatTimeoutSeconds = 30;

    // 게임 타이머 설정 (Config에서 참조)
    private static int GameDurationMinutes => Config.GAME_DURATION_MINUTES;
    private static int GameDurationSeconds => Config.GAME_DURATION_SECONDS;
    private static readonly Dictionary<long, Timer> _gameTimers = new();
    private static readonly object _timerLock = new();

    public GameClientSession(
        UserToken token,
        IRedLockFactory redLock,
        ILogger logger,
        ICacheHelper cacheHelper,
        Action<GameClientSession> onLeaveCallback,
        Action<long, GameClientSession> registerSessionCallback,
        Func<MapId, long, List<GameClientSession>> getSessionsByInstance,
        InteractableStateManager interactableStateManager,
        InGameInventoryManager inGameInventoryManager,
        AreaRuleManager areaRuleManager,
        ExitInstanceManager exitInstanceManager,
        ItemPoolManager itemPoolManager,
        CorridorRuleManager corridorRuleManager,
        InteractRuleManager interactRuleManager,
        DoorStateManager doorStateManager)
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
        _areaRuleManager = areaRuleManager;
        _exitInstanceManager = exitInstanceManager;
        _itemPoolManager = itemPoolManager;
        _corridorRuleManager = corridorRuleManager;
        _interactRuleManager = interactRuleManager;
        _doorStateManager = doorStateManager;

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
        _protocolRouter.RegisterHandler(Protocol.C_TO_G_EXPLORE_END, async (bytes) => await HandleMessage<C_TO_G_EXPLORE_END>(bytes, HandleExploreEnd));
        _protocolRouter.RegisterHandler(Protocol.C_TO_G_USE_INGAME_ITEM, async (bytes) => await HandleMessage<C_TO_G_USE_INGAME_ITEM>(bytes, HandleUseInGameItem));
        _protocolRouter.RegisterHandler(Protocol.C_TO_G_PLAYER_STATE, async (bytes) => await HandleMessage<C_TO_G_PLAYER_STATE>(bytes, HandlePlayerState));

        // 탈출 절차 프로토콜
        _protocolRouter.RegisterHandler(Protocol.C_TO_G_EXIT_ADVANCE, async (bytes) => await HandleMessage<C_TO_G_EXIT_ADVANCE>(bytes, HandleExitAdvance));

        // 로비 복귀 프로토콜
        _protocolRouter.RegisterHandler(Protocol.C_TO_G_RETURN_TO_LOBBY, async (bytes) => await HandleMessage<C_TO_G_RETURN_TO_LOBBY>(bytes, HandleReturnToLobby));

        // 문 프로토콜
        _protocolRouter.RegisterHandler(Protocol.C_TO_G_DOOR_OPEN_REQUEST, async (bytes) => await HandleMessage<C_TO_G_DOOR_OPEN_REQUEST>(bytes, HandleDoorOpenRequest));
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

            if (protocolId != Protocol.C_TO_G_HEART_BEAT)
            {
                _logger.LogInformation("[GameClient] Protocol: {ProtocolId}, PlayerId: {L}", protocolId, playerId);
            }

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

            // 인게임 스탯 초기화
            ResetInGameStats();

            // 게임 타이머 시작 (해당 매칭에 대해 최초 1회만)
            StartGameTimerIfNeeded(msg.MatchingId);

            // 세션 등록
            _registerSessionCallback(PlayerId.Value, this);

            // 초기 위치 로드
            await using var playerLock = await PlayerInfo.Lock(_redLock, PlayerId.Value);
            var playerInfo = await PlayerInfo.Load(_cacheHelper, PlayerId.Value);

            if (playerInfo != null)
            {
                _lastValidatedPosition = playerInfo.ObjectInfo.Position;
                _lastValidCell = playerInfo.ObjectInfo.Cell;
                // 초기 Area 설정
                CurrentArea = GameMapData.GetCurrentArea(CurrentMapId, playerInfo.ObjectInfo.Cell);
                _logger.LogInformation("Player {PlayerId} initial Area: {Area}, Position: ({PosX:F2},{PosY:F2}), Cell: ({CellX},{CellY})",
                    PlayerId, CurrentArea, _lastValidatedPosition?.X, _lastValidatedPosition?.Y, _lastValidCell?.X, _lastValidCell?.Y);

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

            // 탈출 절차 정보 전송
            SendExitStepInfo();

            // 문 초기 상태 설정 및 열린 문 목록 전송
            _doorStateManager.InitializeMatching(CurrentMapSubId);
            SendDoorStateList();

            // 복도 종소리 스케줄 전송
            SendCorridorBellSchedule();

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

            // 2. Area 변경 시 퇴장 조건 체크 (치팅 방지)
            var currentCell = WorldPositionToCell(validatedPosition);
            var newArea = GameMapData.GetCurrentArea(CurrentMapId, currentCell);

            if (newArea != CurrentArea)
            {
                // 현재 Area에서 나갈 수 있는지 체크 (잠긴 문이 있는 경우)
                var blockingDoor = GameDoorData.GetBlockingDoor(CurrentArea);

                if (blockingDoor != null && blockingDoor.RequiredItemId > 0)
                {
                    // 문이 열렸는지 체크
                    var isDoorOpen = _doorStateManager.IsDoorOpen(CurrentMapSubId, blockingDoor.DoorId);

                    if (!isDoorOpen)
                    {
                        // 아이템 보유 여부 체크
                        var playerInventory = _inGameInventoryManager.GetPlayerInventory(CurrentMapSubId, PlayerId.Value);
                        var hasRequiredItem = (playerInventory?.GetItemCount(blockingDoor.RequiredItemId) ?? 0) > 0;

                        if (hasRequiredItem)
                        {
                            // 아이템 보유 시 문 열기 처리
                            _doorStateManager.OpenDoor(CurrentMapSubId, blockingDoor.DoorId);
                            _logger.LogInformation("Player {PlayerId} opened door {DoorId} in {Area} with item {ItemId}",
                                PlayerId, blockingDoor.DoorId, CurrentArea, blockingDoor.RequiredItemId);

                            // 같은 매칭의 모든 플레이어에게 문 열림 알림
                            using var doorPacket = PacketMaker.G_TO_C_DOOR_STATE_UPDATE(blockingDoor.DoorId, true, ErrorCode.SUCCESS, PlayerId.Value);
                            var matchingSessions = _getSessionsByInstance(CurrentMapId, CurrentMapSubId);
                            foreach (var session in matchingSessions)
                            {
                                session.Send(doorPacket);
                            }
                        }
                        else
                        {
                            // 아이템 미보유 - 퇴장 불가
                            var fallbackCell = _lastValidCell ?? currentCell;

                            // G_TO_C_AREA_EXIT_BLOCKED 패킷 전송
                            using var blockedPacket = PacketMaker.G_TO_C_AREA_EXIT_BLOCKED(CurrentArea, fallbackCell);
                            Send(blockedPacket);

                            _logger.LogWarning("Player {PlayerId} attempted to leave {Area} without item {ItemId} - teleported to Cell({X},{Y})",
                                PlayerId, CurrentArea, blockingDoor.RequiredItemId, fallbackCell.X, fallbackCell.Y);
                            return; // 이번 프레임 처리 종료
                        }
                    }
                }
            }

            // 3. 주기적 저장 (1초마다)
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

            // 4. Area 변경 처리 (퇴장 조건 통과한 경우만)
            var serverTimestamp = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();

            if (newArea != CurrentArea)
            {
                // 가장 가까운 문 기준으로 잠김 체크 (클라이언트는 이미 막고 있음, 서버는 보정 역할)
                // 1. 진입하려는 영역의 가장 가까운 문이 잠겨있으면 차단
                var entryBlockedDoor = _doorStateManager.GetBlockingDoorForArea(CurrentMapSubId, newArea, currentCell.X, currentCell.Y);
                if (entryBlockedDoor != null)
                {
                    _logger.LogWarning("Player {PlayerId} blocked entering area {NewArea} (locked door: {DoorId})",
                        PlayerId, newArea, entryBlockedDoor.DoorId);

                    // 진입 차단: fallback (밖쪽)으로 보정
                    var fallbackCell = new Cell(entryBlockedDoor.FallbackCellX, entryBlockedDoor.FallbackCellY);
                    SendAreaExitBlocked(newArea, fallbackCell);
                    return;
                }

                // 2. 현재 영역의 가장 가까운 문이 잠겨있으면 퇴장 차단
                var exitBlockedDoor = _doorStateManager.GetBlockingDoorForArea(CurrentMapSubId, CurrentArea, currentCell.X, currentCell.Y);
                if (exitBlockedDoor != null)
                {
                    _logger.LogWarning("Player {PlayerId} blocked exiting area {CurrentArea} (locked door: {DoorId})",
                        PlayerId, CurrentArea, exitBlockedDoor.DoorId);

                    // 퇴장 차단: position (안쪽)으로 보정
                    var positionCell = new Cell((int)exitBlockedDoor.PositionX, (int)exitBlockedDoor.PositionY);
                    SendAreaExitBlocked(newArea, positionCell);
                    return;
                }

                _logger.LogInformation("Player {PlayerId} Area change at Cell({CellX},{CellY}): {OldArea} → {NewArea}",
                    PlayerId, currentCell.X, currentCell.Y, CurrentArea, newArea);
                var oldArea = CurrentArea;
                CurrentArea = newArea; // 먼저 Area 업데이트 (다른 플레이어의 MOVE 수신 가능하도록)
                await HandleAreaChange(oldArea, newArea);
            }

            // 5. 복도 규칙 체크
            CheckCorridorRuleViolation(validatedPosition, msg.Velocity, newArea);

            // 6. 브로드캐스트 (같은 Area의 플레이어에게만 전송)
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

        // 3. Cell 기반 이동 가능 여부 검증 (맵 밖 이탈 방지)
        var clientCell = WorldPositionToCell(clientPos);
        if (!GameMapData.IsMoveablePosition(CurrentMapId, clientCell))
        {
            // 이동 불가능한 위치 → 마지막 유효 위치로 보정
            if (_lastValidatedPosition != null && _lastValidCell != null)
            {
                _logger.LogWarning("Player {PlayerId} 이동 불가 위치 감지: ClientPos=({CX:F2},{CY:F2}), Cell=({CellX},{CellY}), 보정 → ({VX:F2},{VY:F2})",
                    PlayerId, clientPos.X, clientPos.Y, clientCell.X, clientCell.Y,
                    _lastValidatedPosition.X, _lastValidatedPosition.Y);
                return _lastValidatedPosition;
            }

            // 마지막 유효 위치가 없으면 (첫 이동) 클라이언트 위치 그대로 사용 (초기 스폰 위치 신뢰)
            _logger.LogWarning("Player {PlayerId} 이동 불가 위치 감지 (첫 이동): ClientPos=({CX:F2},{CY:F2}), Cell=({CellX},{CellY})",
                PlayerId, clientPos.X, clientPos.Y, clientCell.X, clientCell.Y);
        }

        // 4. 검증 통과: 유효 위치 업데이트
        _lastValidCell = clientCell;

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

                // 5. Area 도착 시 탈출 조건 체크
                CheckAreaArrivalForExit(newArea);
            }
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "HandleAreaChange error for player {PlayerId}", PlayerId);
        }
    }

    /// <summary>
    /// Area 도착 시 탈출 조건 체크
    /// </summary>
    private void CheckAreaArrivalForExit(AreaType arrivedArea)
    {
        if (!PlayerId.HasValue) return;

        try
        {
            // target_area 조건 체크
            var (advanced, escaped) = _exitInstanceManager.OnAreaVisited(CurrentMapSubId, (int)arrivedArea, PlayerId.Value);

            if (advanced)
            {
                var state = _exitInstanceManager.GetOrCreateMatchingState(CurrentMapSubId);
                var newStepOrder = state.CurrentStepOrder;

                // 탈출 성공 시 게임 타이머 정리
                if (escaped)
                {
                    CleanupGameTimer(CurrentMapSubId);
                    _logger.LogInformation("Game timer cleaned up after escape success (area visit): MatchingId={MatchingId}", CurrentMapSubId);
                }

                // 진행 결과 응답
                using var resultPacket = PacketMaker.G_TO_C_EXIT_ADVANCE_RESULT(
                    success: true,
                    errorCode: ErrorCode.SUCCESS,
                    escaped: escaped,
                    newStepOrder: newStepOrder
                );
                Send(resultPacket);

                // 같은 인스턴스의 다른 플레이어들에게 브로드캐스트
                BroadcastExitStepUpdate(PlayerId.Value, newStepOrder, escaped);

                _logger.LogInformation("Player {PlayerId} area visit advanced exit step: Area={Area}, NewStep={NewStep}, Escaped={Escaped}",
                    PlayerId, arrivedArea, newStepOrder, escaped);
            }
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "CheckAreaArrivalForExit error for player {PlayerId}", PlayerId);
        }
    }

    public void SendInteractableList(AreaType areaType)
    {
        var objects = _interactableStateManager.GetAreaObjectStates(CurrentMapSubId, areaType);
        if (objects.Count == 0)
        {
            _logger.LogDebug("No interactable objects in area {AreaType}", areaType);
            return;
        }

        // 각 오브젝트의 액션 개수 로그
        foreach (var obj in objects)
        {
            _logger.LogDebug("InteractableObject Id={InteractId}: {ActionCount} actions",
                obj.InteractId, obj.Actions?.Count ?? 0);
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

        // 탐색 중이 아닌 상태에서 선택 요청
        if (CurrentState != PlayerState.Exploring)
        {
            _logger.LogWarning("Player {PlayerId} not exploring, cannot select: State={State}",
                PlayerId, CurrentState);
            SendExploreResult(false, msg.InteractId, msg.ActionId, 0, ErrorCode.EXPLORE_NOT_IN_PROGRESS);
            return Task.CompletedTask;
        }

        // 다른 오브젝트를 탐색 중
        if (CurrentExploringInteractId != msg.InteractId)
        {
            _logger.LogWarning("Player {PlayerId} exploring different object: ExploringId={ExploringId}, RequestedId={RequestedId}",
                PlayerId, CurrentExploringInteractId, msg.InteractId);
            SendExploreResult(false, msg.InteractId, msg.ActionId, 0, ErrorCode.INTERACTABLE_NOT_FOUND);
            return Task.CompletedTask;
        }

        _logger.LogInformation("Player {PlayerId} selected action: InteractId={InteractId}, ActionId={ActionId}",
            PlayerId, msg.InteractId, msg.ActionId);

        // 탐색 처리 (InteractableStateManager에서 상태 업데이트 - MatchingId별 독립 관리)
        var success = _interactableStateManager.TryExplore(CurrentMapSubId, msg.InteractId, msg.ActionId, PlayerId.Value, out var state);

        if (success)
        {
            _logger.LogInformation("Player {PlayerId} explored InteractId={InteractId}, ActionId={ActionId} successfully",
                PlayerId, msg.InteractId, msg.ActionId);

            // 스태미나 차감 (액션별 stamina_cost)
            var interactable = GameInteractableData.Get(msg.InteractId);
            var actionData = interactable?.Actions.FirstOrDefault(a => a.ActionId == msg.ActionId);
            if (actionData != null && actionData.StaminaCost > 0)
            {
                ModifyStats(staminaDelta: -actionData.StaminaCost);
                _logger.LogInformation("Player {PlayerId} stamina reduced by {Cost} for InteractId={InteractId}, ActionId={ActionId}",
                    PlayerId, actionData.StaminaCost, msg.InteractId, msg.ActionId);
            }

            // 상호작용 규칙 위반 체크 (금지된 액션 수행)
            var violationResult = _interactRuleManager.CheckExplore(CurrentMapSubId, msg.InteractId, msg.ActionId);
            if (violationResult.IsViolation)
            {
                _logger.LogInformation("Player {PlayerId} violated interact rule {RuleId}: {Message}",
                    PlayerId, violationResult.ViolatedRuleId, violationResult.Message);
                ModifyStats(corruptionDelta: violationResult.CorruptionDelta);
            }

            // 액션 결과 처리 (result_type, result_id, result_amount 기반)
            var (resultType, resultId, resultAmount) = _interactableStateManager.GetActionResult(msg.InteractId, msg.ActionId);
            var rewardItemId = 0;

            switch (resultType)
            {
                case ActionResultType.REWARD_POOL:
                    // 풀에서 다음 아이템 가져오기 (resultId = pool_id, 상호작용 대상별 독립 풀)
                    var itemFromPool = _itemPoolManager.GetNextItemFromPool(CurrentMapSubId, msg.InteractId, resultId);
                    if (itemFromPool.HasValue)
                    {
                        rewardItemId = itemFromPool.Value;
                        AddInGameItem(rewardItemId);
                        _logger.LogInformation("Player {PlayerId} received reward from pool {PoolId}: ItemId={ItemId}",
                            PlayerId, resultId, rewardItemId);

                        // 탈출 절차 목표 아이템인지 확인하고 진척도 갱신
                        CheckAndAdvanceExitStep(rewardItemId);
                    }
                    break;

                case ActionResultType.DEBUFF_CORRUPTION:
                    ModifyStats(corruptionDelta: resultAmount);
                    _logger.LogInformation("Player {PlayerId} received corruption debuff: +{Amount}", PlayerId, resultAmount);
                    break;

                case ActionResultType.DEBUFF_STAMINA:
                    ModifyStats(staminaDelta: -resultAmount);
                    _logger.LogInformation("Player {PlayerId} received stamina debuff: -{Amount}", PlayerId, resultAmount);
                    break;

                case ActionResultType.BUFF_CORRUPTION:
                    ModifyStats(corruptionDelta: -resultAmount);
                    _logger.LogInformation("Player {PlayerId} received corruption buff: -{Amount}", PlayerId, resultAmount);
                    break;

                case ActionResultType.BUFF_STAMINA:
                    ModifyStats(staminaDelta: resultAmount);
                    _logger.LogInformation("Player {PlayerId} received stamina buff: +{Amount}", PlayerId, resultAmount);
                    break;
            }

            // target_interactable_action 조건 체크
            CheckActionCompletedForExit(msg.InteractId, msg.ActionId);

            // 성공 응답 (최종 결정된 아이템 ID 전송)
            SendExploreResult(true, msg.InteractId, msg.ActionId, rewardItemId, ErrorCode.SUCCESS);

            // 같은 Area의 모든 플레이어에게 상태 업데이트 브로드캐스트
            // SINGLE 타입이면 모든 액션을 탐색 완료로 브로드캐스트 (마커 제거용)
            if (interactable?.InteractionType == InteractionType.SINGLE)
            {
                foreach (var action in interactable.Actions)
                {
                    BroadcastInteractableUpdate(msg.InteractId, action.ActionId, true, PlayerId.Value);
                }
            }
            else
            {
                BroadcastInteractableUpdate(msg.InteractId, msg.ActionId, true, PlayerId.Value);
            }
        }
        else
        {
            _logger.LogInformation("Player {PlayerId} tried to explore already explored action: InteractId={InteractId}, ActionId={ActionId}",
                PlayerId, msg.InteractId, msg.ActionId);

            // 이미 탐색됨 - 실패 응답
            SendExploreResult(false, msg.InteractId, msg.ActionId, 0, ErrorCode.ACTION_ALREADY_EXPLORED);
        }

        // SELECT 후에도 Exploring 상태 유지 (END 패킷으로 종료)
        return Task.CompletedTask;
    }

    private Task HandleExploreEnd(C_TO_G_EXPLORE_END msg)
    {
        if (!PlayerId.HasValue) return Task.CompletedTask;

        // 탐색 중이 아니면 무시
        if (CurrentState != PlayerState.Exploring)
        {
            _logger.LogWarning("Player {PlayerId} not exploring, ignoring explore end", PlayerId);
            return Task.CompletedTask;
        }

        _logger.LogInformation("Player {PlayerId} ended exploring InteractId={InteractId}", PlayerId, msg.InteractId);

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

    #region 플레이어 상태

    private Task HandlePlayerState(C_TO_G_PLAYER_STATE msg)
    {
        if (!PlayerId.HasValue) return Task.CompletedTask;

        _logger.LogInformation("Player {PlayerId} state change request: {State}", PlayerId, msg.State);

        // 같은 Area의 다른 플레이어들에게 상태 브로드캐스트
        var allSessions = _getSessionsByInstance(CurrentMapId, CurrentMapSubId);
        var sameAreaSessions = allSessions.Where(s => s.PlayerId != PlayerId && s.CurrentArea == CurrentArea).ToList();

        using var packet = PacketMaker.G_TO_C_PLAYER_STATE(PlayerId.Value, msg.State);
        foreach (var session in sameAreaSessions)
        {
            session.Send(packet);
        }

        _logger.LogDebug("Broadcasted PLAYER_STATE to {Count} players in Area {Area}", sameAreaSessions.Count, CurrentArea);

        return Task.CompletedTask;
    }

    #endregion

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

        // 아이템 정보 먼저 조회 (제거 전에 ItemId 확인 필요)
        var inventory = _inGameInventoryManager.GetPlayerInventory(CurrentMapSubId, PlayerId.Value);
        var itemInfo = inventory.GetItem(msg.ItemUid);
        if (itemInfo == null)
        {
            using var failPacket = PacketMaker.G_TO_C_USE_INGAME_ITEM_RESULT(false, msg.ItemUid, ErrorCode.FATAL);
            Send(failPacket);
            _logger.LogWarning("Player {PlayerId} item not found: ItemUid={ItemUid}", PlayerId, msg.ItemUid);
            return Task.CompletedTask;
        }

        var itemId = itemInfo.ItemId;
        var success = _inGameInventoryManager.TryRemoveItem(CurrentMapSubId, PlayerId.Value, msg.ItemUid, msg.Count, out var updatedItem);

        if (success && updatedItem != null)
        {
            // 아이템 사용 성공 - 인벤토리 업데이트 전송
            SendInGameInventoryUpdate(updatedItem);

            // 버프 효과 적용
            ApplyItemBuffs(itemId);

            // 행동 수칙 쪽지 아이템 처리 (202000003)
            var ruleId = 0;
            if (itemId == 202000003)
            {
                ruleId = _areaRuleManager.GetRuleForNote(CurrentMapSubId);
                _logger.LogInformation("Player {PlayerId} used manual item, got RuleId={RuleId}", PlayerId, ruleId);
            }

            // 사용 결과 전송
            using var resultPacket = PacketMaker.G_TO_C_USE_INGAME_ITEM_RESULT(true, msg.ItemUid, ErrorCode.SUCCESS, ruleId);
            Send(resultPacket);

            _logger.LogInformation("Player {PlayerId} used InGameItem: ItemUid={ItemUid}, ItemId={ItemId}, Count={Count}",
                PlayerId, msg.ItemUid, itemId, msg.Count);
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

    #region 플레이어 스탯

    /// <summary>
    /// 아이템 버프 효과 적용
    /// </summary>
    private void ApplyItemBuffs(int itemId)
    {
        var itemData = GameItemData.Get(itemId);
        if (itemData == null || !itemData.IsConsumable) return;

        var staminaDelta = 0;
        var corruptionDelta = 0;

        foreach (var (buffId, value) in itemData.ConsumableBuffList)
        {
            var buffData = GameBuffData.Get(buffId);

            switch (buffData.SubType)
            {
                case BuffSubType.CONDITION_ADD:
                    staminaDelta += value;
                    break;

                case BuffSubType.CORRUPTION_DOWN:
                    corruptionDelta -= value;
                    break;
            }
        }

        if (staminaDelta != 0 || corruptionDelta != 0)
        {
            ModifyStats(staminaDelta, corruptionDelta);
        }
    }

    /// <summary>
    /// 스탯 변경 (외부에서 호출 가능 - 환경 효과 등)
    /// </summary>
    public void ModifyStats(int staminaDelta = 0, int corruptionDelta = 0)
    {
        var oldStamina = Stamina;
        var oldCorruption = Corruption;

        if (staminaDelta != 0)
        {
            Stamina = Math.Clamp(Stamina + staminaDelta, 0, MaxStamina);
        }

        if (corruptionDelta != 0)
        {
            Corruption = Math.Clamp(Corruption + corruptionDelta, 0, MaxCorruption);
        }

        // 값이 변경되지 않았으면 패킷 전송 안함
        if (Stamina == oldStamina && Corruption == oldCorruption)
        {
            return;
        }

        _logger.LogInformation("Player {PlayerId} Stats: Stamina {OldS}→{NewS} ({DeltaS:+#;-#;0}), Corruption {OldC}→{NewC} ({DeltaC:+#;-#;0})",
            PlayerId, oldStamina, Stamina, staminaDelta, oldCorruption, Corruption, corruptionDelta);

        // 아이템 스펙 그대로 델타값 전송 (이펙트 표시용)
        SendPlayerStatsUpdate(staminaDelta, corruptionDelta);
    }

    /// <summary>
    /// 스탯 업데이트 패킷 전송
    /// </summary>
    private void SendPlayerStatsUpdate(int staminaDelta, int corruptionDelta)
    {
        using var packet = PacketMaker.G_TO_C_PLAYER_STATS_UPDATE(Stamina, staminaDelta, Corruption, corruptionDelta);
        Send(packet);
        _logger.LogDebug("Sent PLAYER_STATS_UPDATE to Player {PlayerId}: Stamina={Stamina} ({StaminaDelta:+#;-#;0}), Corruption={Corruption} ({CorruptionDelta:+#;-#;0})",
            PlayerId, Stamina, staminaDelta, Corruption, corruptionDelta);
    }

    /// <summary>
    /// Area 퇴장 불가 알림 전송 (위치 보정 포함)
    /// </summary>
    private void SendAreaExitBlocked(AreaType areaType, Cell correctedCell)
    {
        using var packet = PacketMaker.G_TO_C_AREA_EXIT_BLOCKED(areaType, correctedCell);
        Send(packet);
        _logger.LogDebug("Sent AREA_EXIT_BLOCKED to Player {PlayerId}: Area={Area}, CorrectedCell=({X},{Y})",
            PlayerId, areaType, correctedCell.X, correctedCell.Y);
    }

    /// <summary>
    /// 인게임 스탯 초기화 (새 게임 시작 시)
    /// </summary>
    private void ResetInGameStats()
    {
        Stamina = MaxStamina;
        Corruption = 0;
        CurrentState = PlayerState.Idle;
        CurrentExploringInteractId = null;
        _logger.LogInformation("Player {PlayerId} in-game stats reset: Stamina={Stamina}, Corruption={Corruption}",
            PlayerId, Stamina, Corruption);
    }

    /// <summary>
    /// 게임 타이머 시작 (매칭당 최초 1회만)
    /// </summary>
    private void StartGameTimerIfNeeded(long matchingId)
    {
        lock (_timerLock)
        {
            if (_gameTimers.ContainsKey(matchingId))
            {
                _logger.LogDebug("Game timer already exists for MatchingId={MatchingId}", matchingId);
                return;
            }

            _logger.LogInformation("게임 타이머 시작: MatchingId={MatchingId} ({Minutes}분)", matchingId, GameDurationMinutes);

            var timer = new Timer(_ =>
            {
                EndGameByTimeout(matchingId);
            }, null, TimeSpan.FromSeconds(GameDurationSeconds), Timeout.InfiniteTimeSpan);

            _gameTimers[matchingId] = timer;
        }
    }

    /// <summary>
    /// 시간 초과로 게임 종료
    /// </summary>
    private void EndGameByTimeout(long matchingId)
    {
        _logger.LogInformation("게임 시간 초과: MatchingId={MatchingId}, 타이머 시작 세션 PlayerId={PlayerId}, CurrentMapSubId={CurrentMapSubId}",
            matchingId, PlayerId, CurrentMapSubId);

        // 타이머 정리
        lock (_timerLock)
        {
            if (_gameTimers.TryGetValue(matchingId, out var timer))
            {
                timer.Dispose();
                _gameTimers.Remove(matchingId);
            }
        }

        // 해당 매칭의 모든 플레이어에게 게임 종료 패킷 전송
        var sessions = _getSessionsByInstance(CurrentMapId, matchingId);
        _logger.LogInformation("게임 종료 패킷 전송 대상: MatchingId={MatchingId}, 필터(MapId={MapId}, MapSubId={MapSubId}), 대상 세션 수={Count}",
            matchingId, CurrentMapId, matchingId, sessions.Count);

        using var packet = PacketMaker.G_TO_C_GAME_END(matchingId, isEscaped: false);

        foreach (var session in sessions)
        {
            _logger.LogInformation("게임 종료 패킷 전송: PlayerId={PlayerId}, 세션의 CurrentMapSubId={SessionMapSubId}",
                session.PlayerId, session.CurrentMapSubId);
            session.Send(packet);
        }
    }

    /// <summary>
    /// 매칭 종료 시 타이머 정리
    /// </summary>
    public static void CleanupGameTimer(long matchingId)
    {
        lock (_timerLock)
        {
            if (_gameTimers.TryGetValue(matchingId, out var timer))
            {
                timer.Dispose();
                _gameTimers.Remove(matchingId);
            }
        }
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

    #region 탈출 절차

    /// <summary>
    /// 탈출 절차 정보 전송 (게임 접속 시 자동 전송)
    /// </summary>
    private void SendExitStepInfo()
    {
        if (!PlayerId.HasValue) return;

        try
        {
            var state = _exitInstanceManager.GetOrCreateMatchingState(CurrentMapSubId);

            using var packet = PacketMaker.G_TO_C_EXIT_STEP_INFO(
                groupId: state.GroupId,
                currentStepOrder: state.CurrentStepOrder,
                totalStepCount: state.Steps.Count,
                isCompleted: state.IsCompleted,
                lastAdvancedBy: state.LastAdvancedBy
            );
            Send(packet);

            _logger.LogInformation("Sent exit step info to Player {PlayerId}: GroupId={GroupId}, CurrentStep={StepOrder}/{TotalSteps}, Completed={IsCompleted}, LastAdvancedBy={LastAdvancedBy}",
                PlayerId, state.GroupId, state.CurrentStepOrder, state.Steps.Count, state.IsCompleted, state.LastAdvancedBy);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "SendExitStepInfo error for player {PlayerId}", PlayerId);
        }
    }

    /// <summary>
    /// 탈출 절차 다음 단계 진행 요청 처리
    /// </summary>
    private Task HandleExitAdvance(C_TO_G_EXIT_ADVANCE msg)
    {
        if (!PlayerId.HasValue) return Task.CompletedTask;

        try
        {
            var state = _exitInstanceManager.GetOrCreateMatchingState(CurrentMapSubId);

            // 클라이언트가 생각하는 현재 단계와 서버 단계 검증
            if (msg.CurrentStepOrder != state.CurrentStepOrder)
            {
                _logger.LogWarning("Player {PlayerId} exit advance step mismatch: client={ClientStep}, server={ServerStep}",
                    PlayerId, msg.CurrentStepOrder, state.CurrentStepOrder);

                using var errorPacket = PacketMaker.G_TO_C_EXIT_ADVANCE_RESULT(
                    success: false,
                    errorCode: ErrorCode.FATAL,
                    escaped: false,
                    newStepOrder: state.CurrentStepOrder
                );
                Send(errorPacket);
                return Task.CompletedTask;
            }

            // 이미 완료된 경우
            if (state.IsCompleted)
            {
                _logger.LogWarning("Player {PlayerId} tried to advance already completed exit procedure", PlayerId);

                using var errorPacket = PacketMaker.G_TO_C_EXIT_ADVANCE_RESULT(
                    success: false,
                    errorCode: ErrorCode.FATAL,
                    escaped: true,
                    newStepOrder: state.CurrentStepOrder
                );
                Send(errorPacket);
                return Task.CompletedTask;
            }

            // 다음 단계로 진행 (수동 진행)
            var (success, escaped) = _exitInstanceManager.AdvanceStep(CurrentMapSubId, PlayerId.Value);

            if (success)
            {
                var newStepOrder = state.CurrentStepOrder;

                // 탈출 성공 시 게임 타이머 정리
                if (escaped)
                {
                    CleanupGameTimer(CurrentMapSubId);
                    _logger.LogInformation("Game timer cleaned up after escape success: MatchingId={MatchingId}", CurrentMapSubId);
                }

                // 요청자에게 결과 응답
                using var resultPacket = PacketMaker.G_TO_C_EXIT_ADVANCE_RESULT(
                    success: true,
                    errorCode: ErrorCode.SUCCESS,
                    escaped: escaped,
                    newStepOrder: newStepOrder
                );
                Send(resultPacket);

                // 같은 인스턴스의 다른 플레이어들에게 브로드캐스트
                BroadcastExitStepUpdate(PlayerId.Value, newStepOrder, escaped);

                // 현재 Area의 Interactable 목록 다시 전송 (MissionActionText 갱신)
                if (CurrentArea != AreaType.None)
                {
                    SendInteractableList(CurrentArea);
                }

                _logger.LogInformation("Player {PlayerId} advanced exit step: NewStep={NewStep}, Escaped={Escaped}",
                    PlayerId, newStepOrder, escaped);
            }
            else
            {
                _logger.LogWarning("Player {PlayerId} failed to advance exit step", PlayerId);

                using var errorPacket = PacketMaker.G_TO_C_EXIT_ADVANCE_RESULT(
                    success: false,
                    errorCode: ErrorCode.FATAL,
                    escaped: false,
                    newStepOrder: state.CurrentStepOrder
                );
                Send(errorPacket);
            }
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "HandleExitAdvance error for player {PlayerId}", PlayerId);
        }

        return Task.CompletedTask;
    }

    /// <summary>
    /// 탈출 절차 단계 변경을 같은 인스턴스의 다른 플레이어들에게 브로드캐스트
    /// </summary>
    private void BroadcastExitStepUpdate(long advancedByPlayerId, int newStepOrder, bool escaped)
    {
        var allSessions = _getSessionsByInstance(CurrentMapId, CurrentMapSubId);
        var otherSessions = allSessions.Where(s => s.PlayerId != PlayerId && s.PlayerId.HasValue).ToList();

        using var packet = PacketMaker.G_TO_C_EXIT_STEP_UPDATE(advancedByPlayerId, newStepOrder, escaped);
        foreach (var session in otherSessions)
        {
            session.Send(packet);

            // 다른 플레이어에게도 현재 Area의 Interactable 목록 전송 (MissionActionText 갱신)
            if (session.CurrentArea != AreaType.None)
            {
                session.SendInteractableList(session.CurrentArea);
            }
        }

        _logger.LogDebug("Broadcasted EXIT_STEP_UPDATE to {Count} players in instance {InstanceId}", otherSessions.Count, CurrentMapSubId);
    }

    /// <summary>
    /// 액션 완료 시 탈출 절차 확인 (target_interactable_action 조건 체크)
    /// </summary>
    private void CheckActionCompletedForExit(int interactId, int actionId)
    {
        if (!PlayerId.HasValue) return;

        try
        {
            var (advanced, escaped) = _exitInstanceManager.OnActionCompleted(CurrentMapSubId, interactId, actionId, PlayerId.Value);

            if (advanced)
            {
                var state = _exitInstanceManager.GetOrCreateMatchingState(CurrentMapSubId);
                var newStepOrder = state.CurrentStepOrder;

                // 탈출 성공 시 게임 타이머 정리
                if (escaped)
                {
                    CleanupGameTimer(CurrentMapSubId);
                    _logger.LogInformation("Game timer cleaned up after escape success (action completed): MatchingId={MatchingId}", CurrentMapSubId);
                }

                // 진행 결과 브로드캐스트
                BroadcastExitStepUpdateToAll(PlayerId.Value, newStepOrder, escaped);

                _logger.LogInformation("Player {PlayerId} action completion advanced exit step: InteractId={InteractId}, ActionId={ActionId}, NewStep={NewStep}, Escaped={Escaped}",
                    PlayerId, interactId, actionId, newStepOrder, escaped);
            }
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "CheckActionCompletedForExit error for player {PlayerId}", PlayerId);
        }
    }

    /// <summary>
    /// 아이템 습득 시 탈출 절차 확인 (target_item_id 조건 체크)
    /// </summary>
    private void CheckAndAdvanceExitStep(int acquiredItemId)
    {
        if (!PlayerId.HasValue) return;

        try
        {
            var (advanced, escaped) = _exitInstanceManager.OnItemAcquired(CurrentMapSubId, acquiredItemId, PlayerId.Value);

            if (advanced)
            {
                var state = _exitInstanceManager.GetOrCreateMatchingState(CurrentMapSubId);
                var newStepOrder = state.CurrentStepOrder;

                // 탈출 성공 시 게임 타이머 정리
                if (escaped)
                {
                    CleanupGameTimer(CurrentMapSubId);
                    _logger.LogInformation("Game timer cleaned up after escape success (item acquired): MatchingId={MatchingId}", CurrentMapSubId);
                }

                // 진행 결과 브로드캐스트
                BroadcastExitStepUpdateToAll(PlayerId.Value, newStepOrder, escaped);

                _logger.LogInformation("Player {PlayerId} item acquisition advanced exit step: ItemId={ItemId}, NewStep={NewStep}, Escaped={Escaped}",
                    PlayerId, acquiredItemId, newStepOrder, escaped);
            }
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "CheckAndAdvanceExitStep error for player {PlayerId}", PlayerId);
        }
    }

    /// <summary>
    /// 탈출 절차 단계 변경을 본인 포함 모든 플레이어에게 브로드캐스트
    /// </summary>
    private void BroadcastExitStepUpdateToAll(long advancedByPlayerId, int newStepOrder, bool escaped)
    {
        var allSessions = _getSessionsByInstance(CurrentMapId, CurrentMapSubId);

        using var packet = PacketMaker.G_TO_C_EXIT_STEP_UPDATE(advancedByPlayerId, newStepOrder, escaped);
        foreach (var session in allSessions.Where(s => s.PlayerId.HasValue))
        {
            session.Send(packet);
        }

        _logger.LogDebug("Broadcasted EXIT_STEP_UPDATE to ALL {Count} players in instance {InstanceId}", allSessions.Count, CurrentMapSubId);
    }

    #endregion

    #region 로비 복귀

    /// <summary>
    /// 로비 복귀 요청 처리 (게임 완료 후)
    /// </summary>
    private Task HandleReturnToLobby(C_TO_G_RETURN_TO_LOBBY msg)
    {
        if (!PlayerId.HasValue)
        {
            _logger.LogWarning("HandleReturnToLobby: PlayerId not set");
            using var errorPacket = PacketMaker.G_TO_C_RETURN_TO_LOBBY_RESULT(false, ErrorCode.FATAL);
            Send(errorPacket);
            return Task.CompletedTask;
        }

        try
        {
            // 게임 종료 후 로비 복귀 (탈출 성공/실패 모두 허용)
            _logger.LogInformation("Player {PlayerId} returning to lobby", PlayerId);

            // 성공 응답 전송
            using var resultPacket = PacketMaker.G_TO_C_RETURN_TO_LOBBY_RESULT(true, ErrorCode.SUCCESS);
            Send(resultPacket);

            // 세션 정리 (Leave 콜백 호출)
            _onLeaveCallback?.Invoke(this);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "HandleReturnToLobby error for player {PlayerId}", PlayerId);
            using var errorPacket = PacketMaker.G_TO_C_RETURN_TO_LOBBY_RESULT(false, ErrorCode.FATAL);
            Send(errorPacket);
        }

        return Task.CompletedTask;
    }

    #endregion

    #region 복도 규칙

    /// <summary>
    /// 복도 규칙 위반 체크 및 정신오염도 증가 처리
    /// </summary>
    private void CheckCorridorRuleViolation(Vector3f position, Vector3f velocity, AreaType currentArea)
    {
        if (!PlayerId.HasValue) return;

        try
        {
            var corridorRuleId = _areaRuleManager.GetFirstCorridorRuleId(CurrentMapSubId);
            // 복도 규칙 1번(종소리 중 이동 금지) 또는 6번(정지 금지)일 때만 체크
            if (corridorRuleId != 1 && corridorRuleId != 6) return;

            var result = _corridorRuleManager.CheckPlayerMove(
                CurrentMapSubId,
                PlayerId.Value,
                position,
                velocity,
                currentArea,
                corridorRuleId);

            if (result.IsViolation)
            {
                _logger.LogInformation("Player {PlayerId} violated corridor rule {RuleId}: {Message}, Corruption +{Delta}",
                    PlayerId, corridorRuleId, result.Message, result.CorruptionDelta);

                // 정신오염도 증가
                ModifyStats(corruptionDelta: result.CorruptionDelta);
            }
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "CheckCorridorRuleViolation error for player {PlayerId}", PlayerId);
        }
    }

    #endregion

    #region 문

    /// <summary>
    /// 문 열기 요청 처리
    /// </summary>
    private Task HandleDoorOpenRequest(C_TO_G_DOOR_OPEN_REQUEST msg)
    {
        if (!PlayerId.HasValue)
        {
            _logger.LogWarning("HandleDoorOpenRequest: PlayerId not set");
            return Task.CompletedTask;
        }

        try
        {
            var doorId = msg.DoorId;
            var doorInfo = GameDoorData.Get(doorId);

            // 문 정보 확인
            if (doorInfo == null)
            {
                _logger.LogWarning("Player {PlayerId} tried to open unknown door: DoorId={DoorId}", PlayerId, doorId);
                using var errorPacket = PacketMaker.G_TO_C_DOOR_STATE_UPDATE(doorId, false, ErrorCode.DOOR_NOT_FOUND);
                Send(errorPacket);
                return Task.CompletedTask;
            }

            // 이미 열려있는지 확인
            if (_doorStateManager.IsDoorOpen(CurrentMapSubId, doorId))
            {
                _logger.LogDebug("Player {PlayerId} tried to open already open door: DoorId={DoorId}", PlayerId, doorId);
                using var alreadyOpenPacket = PacketMaker.G_TO_C_DOOR_STATE_UPDATE(doorId, true, ErrorCode.DOOR_ALREADY_OPEN);
                Send(alreadyOpenPacket);
                return Task.CompletedTask;
            }

            // 열쇠 보유 확인 (required_item_id가 0이면 열쇠 불필요)
            if (doorInfo.RequiredItemId > 0)
            {
                var playerInventory = _inGameInventoryManager.GetPlayerInventory(CurrentMapSubId, PlayerId.Value);
                var hasKey = (playerInventory?.GetItemCount(doorInfo.RequiredItemId) ?? 0) > 0;

                if (!hasKey)
                {
                    _logger.LogWarning("Player {PlayerId} missing key for door: DoorId={DoorId}, RequiredItemId={ItemId}",
                        PlayerId, doorId, doorInfo.RequiredItemId);
                    using var noKeyPacket = PacketMaker.G_TO_C_DOOR_STATE_UPDATE(doorId, false, ErrorCode.DOOR_KEY_MISSING);
                    Send(noKeyPacket);
                    return Task.CompletedTask;
                }
            }

            // 문 열기
            _doorStateManager.OpenDoor(CurrentMapSubId, doorId);
            _logger.LogInformation("Player {PlayerId} opened door: DoorId={DoorId}", PlayerId, doorId);

            // 같은 매칭의 모든 플레이어에게 브로드캐스트
            using var updatePacket = PacketMaker.G_TO_C_DOOR_STATE_UPDATE(doorId, true, ErrorCode.SUCCESS, PlayerId.Value);
            var matchingSessions = _getSessionsByInstance(CurrentMapId, CurrentMapSubId);
            foreach (var session in matchingSessions)
            {
                session.Send(updatePacket);
            }
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "HandleDoorOpenRequest error for player {PlayerId}", PlayerId);
        }

        return Task.CompletedTask;
    }

    /// <summary>
    /// 열린 문 목록 전송 (입장 시)
    /// </summary>
    private void SendDoorStateList()
    {
        if (!PlayerId.HasValue) return;

        var openDoors = _doorStateManager.GetOpenDoors(CurrentMapSubId);
        using var packet = PacketMaker.G_TO_C_DOOR_STATE_LIST(openDoors);
        Send(packet);
        _logger.LogDebug("Sent DOOR_STATE_LIST to Player {PlayerId}: {Count} open doors", PlayerId, openDoors.Count);
    }

    #endregion

    #region 복도 규칙

    /// <summary>
    /// 복도 종소리 스케줄 전송 (입장 시)
    /// </summary>
    private void SendCorridorBellSchedule()
    {
        if (!PlayerId.HasValue) return;

        var bells = _corridorRuleManager.GetBellSchedule(CurrentMapSubId);
        using var packet = PacketMaker.G_TO_C_CORRIDOR_BELL(bells);
        Send(packet);
        _logger.LogDebug("Sent CORRIDOR_BELL schedule to Player {PlayerId}: {Count} bells", PlayerId, bells.Count);
    }

    #endregion
}
