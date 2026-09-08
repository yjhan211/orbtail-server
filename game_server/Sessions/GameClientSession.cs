using game_server.services;
using MessagePack;
using Microsoft.Extensions.Logging;
using network.common;
using network.common.data;
using network.common.data.models;
using network.core;
using network.infrastructure.redis;
using network.packets;

namespace game_server.sessions;

/// <summary>
///     GameServer에 접속한 플레이어 한 명의 TCP 세션.
///     입장 티켓으로 플레이어와 매치를 확인하고, 해당 MatchRuntime에 연결한다.
///     수신 패킷을 처리하거나 담당 서비스에 전달하고, 결과를 클라이언트에 전송한다.
///     연결 종료 시에는 입장 실패·퇴장·서버 종료 상황에 맞게 정리를 요청한다.
///
///     플레이어 개인 상태는 세션이, 참가 세션 목록과 매치 전체 상태는 MatchRuntime이 관리한다.
///     기능별 요청 처리와 상태 전송 코드는 GameClientSession.* partial 파일에 나누어 둔다.
/// </summary>
public partial class GameClientSession : SessionBase
{
    private readonly GameMatchEntryService _matchEntry;
    private readonly GameSessionLeaveHandler _sessionLeaveHandler;

    private MatchRuntime? _match;
    internal MatchRuntime Match => Volatile.Read(ref _match) ?? throw new InvalidOperationException("Session has not entered a match.");

    private readonly Func<Packet, bool> _trySendConnectSuccessResponse;
    private readonly Func<long, GameClientSession, GameClientSession?> _registerSessionCallback;
    private readonly IGameSessionLifecycle _matchingLifecycle;
    private readonly IMatchEntryFailureHandler _entryFailureHandler;
    private readonly Func<bool> _isServerStopping;
    private readonly GameEventLogManager _gameEventLogManager;
    internal PlayerHealthChangeService HealthChanges { get; }
    private readonly PlayerCondition _condition = new();
    private readonly IPlayerGrowthHandler _growth;
    private readonly PlayerMovementService _playerMovement;
    private readonly OrbInventoryService _orbInventory;

    private int _entryCompleted;
    private int _entryFailureReported;
    private int _entryDisconnectIssued;
    private int _matchingLifecycleHandledExternally;
    private int _matchingLifecycleTerminalReported;
    private int _matchingReservationReleaseReported;

    private readonly PlayerInteractionState _interactions = new();


    internal static Action<long, long>? SwarmHeartPickupCallback { get; set; }

    internal GameClientSession(
        TcpConnection connection,
        ILogger logger,
        IRedisOperations redisOperations,
        GameSessionLeaveHandler sessionLeaveHandler,
        Func<long, GameClientSession, GameClientSession?> registerSessionCallback,
        GameEventLogManager gameEventLogManager,
        MatchEliminationService matchEliminations,
        IPlayerGrowthHandler growth,
        IGameSessionLifecycle matchingLifecycle,
        Func<bool> isServerStopping,
        IMatchEntryFailureHandler entryFailureHandler,
        GameMatchEntryService matchEntry,
        MovementValidationService movementValidation,
        OrbInventoryService orbInventory,
        Func<Packet, bool>? trySendConnectSuccessResponse = null)
        : base(connection, logger, redisOperations)
    {
        _sessionLeaveHandler = sessionLeaveHandler;
        _registerSessionCallback = registerSessionCallback;

        _gameEventLogManager = gameEventLogManager;
        HealthChanges = new PlayerHealthChangeService(this, gameEventLogManager, matchEliminations, logger);
        _growth = growth;

        _matchEntry = matchEntry;
        _playerMovement = new PlayerMovementService(this, movementValidation, gameEventLogManager, logger);
        _orbInventory = orbInventory;
        _trySendConnectSuccessResponse = trySendConnectSuccessResponse ?? Connection.TrySend;
        _matchingLifecycle = matchingLifecycle;
        _isServerStopping = isServerStopping;
        _entryFailureHandler = entryFailureHandler;

        InitializeProtocolHandlers();
        Logger.LogInformation("GameClientSession created");
    }

    public new long? PlayerId { get; private set; }
    public MapId CurrentMapId { get; private set; }
    public long MatchingId { get; private set; }
    public AreaType CurrentArea => _playerMovement.CurrentArea;
    public Vector3f? LastValidatedPosition => _playerMovement.LastValidatedPosition;

    public PlayerMatchStatus PlayerMatchStatus { get; private set; } = PlayerMatchStatus.ACTIVE;
    private bool _isGameEnded;
    private bool _disconnectedByServer;

    internal bool IsGameEnded => Volatile.Read(ref _isGameEnded);
    internal int CurrentHealth => Health;
    internal bool IsConnectionReleased => Connection.IsReleased;
    internal bool IsAcceptingMessages => Connection.IsAcceptingMessages;
    internal PlayerCondition Condition => _condition;
    public bool IsEliminated => PlayerMatchStatus is PlayerMatchStatus.ELIMINATED or PlayerMatchStatus.SPECTATING;

    private int Health => _condition.Health;

    private void InitializeProtocolHandlers()
    {
        ProtocolRouter.RegisterHandler(Protocol.C_TO_G_HEART_BEAT, async _ => await HandleHeartbeat());
        ProtocolRouter.RegisterHandler(Protocol.C_TO_G_CONNECT,
            async bytes => await HandleMessage<C_TO_G_CONNECT>(bytes, HandleConnect));
        ProtocolRouter.RegisterHandler(Protocol.C_TO_G_MATCH_START_READY,
            async _ => await HandleMatchStartReady());
        ProtocolRouter.RegisterHandler(Protocol.C_TO_G_MOVE,
            async bytes => await HandleMessage<C_TO_G_MOVE>(bytes, HandleMove));
        ProtocolRouter.RegisterHandler(Protocol.C_TO_G_SUMMON_ORB,
            async bytes => await HandleMessage<C_TO_G_SUMMON_ORB>(bytes, HandleSummonOrb));
        ProtocolRouter.RegisterHandler(Protocol.C_TO_G_UPGRADE_ORB,
            async bytes => await HandleMessage<C_TO_G_UPGRADE_ORB>(bytes, HandleUpgradeOrb));

        ProtocolRouter.RegisterHandler(Protocol.C_TO_G_PLAYER_STATE,
            async bytes => await HandleMessage<C_TO_G_PLAYER_STATE>(bytes, HandlePlayerState));
        ProtocolRouter.RegisterHandler(Protocol.C_TO_G_DOOR_OPEN_START,
            async bytes => await HandleMessage<C_TO_G_DOOR_OPEN_START>(bytes, HandleDoorOpenStart));
        ProtocolRouter.RegisterHandler(Protocol.C_TO_G_DOOR_OPEN_FINISH,
            async bytes => await HandleMessage<C_TO_G_DOOR_OPEN_FINISH>(bytes, HandleDoorOpenFinish));
        ProtocolRouter.RegisterHandler(Protocol.C_TO_G_SOCIAL_ACTION,
            async bytes => await HandleMessage<C_TO_G_SOCIAL_ACTION>(bytes, HandleSocialAction));
    }

    protected override bool ShouldSkipLogging(Protocol protocolId)
    {
        return protocolId == Protocol.C_TO_G_HEART_BEAT || protocolId == Protocol.C_TO_G_MOVE;
    }

    private Task HandleHeartbeat()
    {
        using var packet = PacketMaker.G_TO_C_HEART_BEAT(DateTime.UtcNow);
        TrySend(packet);
        return Task.CompletedTask;
    }

    private bool IsGameplayActionBlocked(out ErrorCode errorCode)
    {
        errorCode = ErrorCode.SUCCESS;

        if (IsEliminated)
        {
            errorCode = ErrorCode.PLAYER_DEAD;
            return true;
        }

        if (IsGameEnded)
        {
            errorCode = ErrorCode.GAME_ALREADY_ENDED;
            return true;
        }

        if (MatchingId > 0 && !MatchStartGate.IsGameplayActive(MatchingId))
        {
            errorCode = ErrorCode.GAME_NOT_STARTED;
            return true;
        }

        return false;
    }

    private static void InitializeWithMatchLock(MatchRuntime runtime, Action initialize)
    {
        using var scope = runtime.Enter();
        if (runtime.IsTerminal)
        {
            throw new OperationCanceledException("Match became terminal during game entry.");
        }

        initialize();
    }

    private Task RunWithMatchLock(Func<Task> handleRequest, Action rejectRequest)
    {
        ArgumentNullException.ThrowIfNull(handleRequest);
        ArgumentNullException.ThrowIfNull(rejectRequest);

        var runtime = Volatile.Read(ref _match);
        if (runtime == null)
        {
            rejectRequest();
            return Task.CompletedTask;
        }

        using var scope = runtime.Enter();
        if (runtime.IsTerminal)
        {
            rejectRequest();
            return Task.CompletedTask;
        }

        var handlingTask = handleRequest() ?? throw new InvalidOperationException("Match request handler returned a null task.");
        if (!handlingTask.IsCompleted)
        {
            throw new InvalidOperationException("Match request handler must complete synchronously under the match lock.");
        }

        handlingTask.GetAwaiter().GetResult();
        return Task.CompletedTask;
    }

    public void MarkDisconnectedByServer()
    {
        Volatile.Write(ref _disconnectedByServer, true);
    }

    private void EnsureConnectionActive()
    {
        if (Connection.IsReleased)
        {
            throw new OperationCanceledException("Connection closed during game entry.");
        }
    }

    private async Task HandleConnect(C_TO_G_CONNECT msg)
    {
        if (PlayerId.HasValue || MatchingId > 0)
        {
            Logger.LogWarning("Repeated game authentication attempt: PlayerId={PlayerId}, MatchingId={MatchingId}", PlayerId, MatchingId);
            SendConnectFailure(ErrorCode.ALREADY_AUTHENTICATED);
            return;
        }

        bool registered = false;
        try
        {
            var entryContext = await _matchEntry.ConsumeTicketAsync(msg.GameEntryTicket);
            if (entryContext == null)
            {
                EnsureConnectionActive();
                Logger.LogWarning("GameServer connection rejected: invalid, expired, replayed, or another node's handoff ticket");
                SendConnectFailure(ErrorCode.GAME_ENTRY_TICKET_INVALID);
                return;
            }

            long matchingId = entryContext.MatchingId;
            long playerId = entryContext.PlayerId;
            PlayerId = playerId;
            CurrentMapId = Config.SWARM_MATCH_MAP;
            MatchingId = matchingId;

            EnsureConnectionActive();
            Logger.LogInformation("Client connection authorized: PlayerId={PlayerId}, MatchingId={MatchingId}", playerId, matchingId);

            var runtime = _matchEntry.GetOrCreateMatch(matchingId);
            Volatile.Write(ref _match, runtime);
            GameClientSession? previousSession = null;
            using (runtime.Enter())
            {
                if (runtime.IsTerminal)
                {
                    Logger.LogWarning("GameServer connection rejected because the match is terminal: PlayerId={PlayerId}, MatchingId={MatchingId}", playerId, matchingId);
                    MarkDisconnectedByServer();
                    SendConnectFailure(ErrorCode.GAME_ALREADY_ENDED);
                    return;
                }

                if (!Connection.TryRunIfActive(() => previousSession = _registerSessionCallback(playerId, this)))
                {
                    throw new OperationCanceledException("Connection closed before session registration.");
                }
                registered = true;
            }

            if (previousSession != null)
            {
                previousSession.MarkDisconnectedByServer();
                previousSession.ForceDisconnect();
            }

            var composition = await _matchEntry.LoadCompositionAsync(matchingId, CurrentMapId, runtime);
            EnsureConnectionActive();
            if (!composition.HumanPlayerIds.Contains(playerId))
            {
                throw new InvalidOperationException($"Player {playerId} is not part of match {matchingId}.");
            }

            using (var rosterPacket = PacketMaker.G_TO_C_MATCH_ROSTER(matchingId, composition.PlayerRoster.ToList()))
            {
                if (!TrySend(rosterPacket))
                {
                    throw new OperationCanceledException("Could not send initial match roster.");
                }
            }

            InitializeWithMatchLock(runtime, () =>
            {
                MatchStartGate.RegisterHumanPlayer(matchingId, PlayerId.Value, composition.HumanPlayerIds.Count, composition.Mode);
            });

            var matchingSpawnCell = Cell.Clone(composition.SpawnCells[playerId]);
            _playerMovement.InitializeSpawn(matchingSpawnCell);

            Logger.LogInformation(
                "Player {PlayerId} initial Area: {Area}, Position: ({PosX:F2},{PosY:F2}), Cell: ({CellX},{CellY})",
                PlayerId, CurrentArea, LastValidatedPosition?.X, LastValidatedPosition?.Y, _playerMovement.LastValidatedCell?.X,
                _playerMovement.LastValidatedCell?.Y);

            _gameEventLogManager.LogSpawnAssignment(
                MatchingId,
                PlayerId.Value,
                MatchSpawnData.GetDeterministicSeed(MatchingId),
                MatchSpawnData.GetAnchorIndex(matchingSpawnCell),
                matchingSpawnCell.X,
                matchingSpawnCell.Y,
                CurrentArea.ToString(),
                isBot: false);

            _gameEventLogManager.SetPlayerArea(MatchingId, PlayerId.Value, CurrentArea.ToString());

            _playerMovement.SendInteractableList(CurrentArea);

            GroundItemNotificationService.SendSnapshot(this, CurrentArea);
            SendOrbList();
            SendSummonStoneState();
            SendDoorStateList();
            SendPressureFieldState();
            LogInitialInventory();

            await BroadcastPlayerJoin();
            EnsureConnectionActive();

            await _matchEntry.CommitAsync(matchingId, PlayerId.Value, composition.HumanPlayerIds);
            EnsureConnectionActive();

            if (composition.HumanPlayerIds.Count == 1)
            {
                MatchStartGate.MarkHumanReady(matchingId, PlayerId.Value);
            }

            SendMatchStartCountdown(matchingId);
            using var successResponse = CreateConnectResultPacket(true, ErrorCode.SUCCESS, matchingId, matchingSpawnCell);
            InitializeWithMatchLock(runtime, () =>
            {
                if (!Connection.TryMarkAuthenticated(() => Volatile.Write(ref _entryCompleted, 1)))
                {
                    throw new OperationCanceledException("Connection closed before authentication commit.");
                }
            });

            if (!_trySendConnectSuccessResponse(successResponse))
            {
                throw new IOException("Failed to queue the game entry response.");
            }
            Logger.LogInformation("Client connected successfully: PlayerId={PlayerId}", PlayerId);
        }
        catch (Exception ex)
        {
            Logger.LogError(ex, "Failed to handle connect");

            if (Volatile.Read(ref _entryCompleted) != 0)
            {
                CloseAfterCommittedEntryResponseFailure();
                return;
            }

            if (registered)
            {
                HandleEntryFailureOnce();
                return;
            }
            MarkDisconnectedByServer();
            SendConnectFailure(ErrorCode.GAME_ENTRY_FAILED);
        }
    }

    private void LogInitialInventory()
    {
        var inventory = Match.Inventory.GetPlayerInventory(PlayerId!.Value);
        _gameEventLogManager.LogOrbBoardTransition(MatchingId, PlayerId.Value, inventory.GetAllItems(),
            inventory.GetOrderedOrbs().FirstOrDefault()?.ItemId ?? 0, CurrentArea.ToString(), "connection_sync", isBot: false);
    }

    private void SendConnectFailure(ErrorCode errorCode)
    {
        using var packet = CreateConnectResultPacket(false, errorCode);
        Connection.TrySendAndDisconnect(packet);
    }

    private Packet CreateConnectResultPacket(bool success, ErrorCode errorCode, long matchingId = 0, Cell? spawnCell = null)
    {
        var response = new G_TO_C_CONNECT_RESULT
        {
            Success = success,
            ErrorCode = errorCode,
            MatchingId = matchingId,
            SpawnCell = spawnCell ?? new Cell(0, 0)
        };
        byte[] body = MessagePackSerializer.Serialize(response);
        var packet = Packet.Create((int)Protocol.G_TO_C_CONNECT_RESULT, PlayerId ?? 0);
        try
        {
            packet.SetBody(body);
            return packet;
        }
        catch
        {
            packet.Dispose();
            throw;
        }
    }

    private void CloseAfterCommittedEntryResponseFailure()
    {
        MarkDisconnectedByServer();
        try
        {
            Connection.Disconnect();
        }
        catch (Exception ex)
        {
            Logger.LogError(
                ex,
                "Failed to close connection after committed entry response failure: PlayerId={PlayerId}, MatchingId={MatchingId}",
                PlayerId,
                MatchingId);
        }
    }

    private void SendMatchStartCountdown(long matchingId)
    {
        var snapshot = MatchStartGate.GetSnapshot(matchingId);
        if (!snapshot.IsKnown)
        {
            return;
        }

        using var packet = Packet.Create((int)Protocol.G_TO_C_MATCH_START_COUNTDOWN, PlayerId ?? 0);
        packet.SetBody(MessagePackSerializer.Serialize(new G_TO_C_MATCH_START_COUNTDOWN
        {
            MatchingId = matchingId,
            RemainingSeconds = snapshot.RemainingSeconds,
            ServerUnixMs = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds()
        }));
        TrySend(packet);
    }

    private Task HandleMatchStartReady()
    {
        if (!PlayerId.HasValue || MatchingId <= 0)
        {
            return Task.CompletedTask;
        }
        return RunWithMatchLock(() =>
        {
            MatchStartGate.MarkHumanReady(MatchingId, PlayerId.Value);
            SendMatchStartCountdown(MatchingId);
            return Task.CompletedTask;
        }, () => { });
    }

    private void ForceDisconnect()
    {
        Logger.LogWarning("Force disconnecting PlayerId={PlayerId}", PlayerId);
        Connection.Disconnect();
    }

    private bool TryBeginMatchEndHandling()
    {
        return Interlocked.CompareExchange(ref _matchingLifecycleTerminalReported, 1, 0) == 0;
    }

    private void HandleEntryFailureOnce()
    {
        if (Volatile.Read(ref _entryCompleted) != 0 || !PlayerId.HasValue || MatchingId <= 0)
        {
            return;
        }

        if (Interlocked.CompareExchange(ref _entryFailureReported, 1, 0) != 0)
        {
            return;
        }

        if (!TryBeginMatchEndHandling())
        {
            Volatile.Write(ref _entryFailureReported, 0);
            return;
        }

        try
        {
            _entryFailureHandler.Handle(this);
        }
        catch (Exception ex)
        {
            Volatile.Write(ref _matchingLifecycleTerminalReported, 0);
            Volatile.Write(ref _entryFailureReported, 0);
            Logger.LogError(
                ex,
                "Failed to report game entry failure: PlayerId={PlayerId}, MatchingId={MatchingId}",
                PlayerId,
                MatchingId);
            MarkDisconnectedByServer();
            try
            {
                Connection.Disconnect();
            }
            catch (Exception disconnectException)
            {
                Logger.LogError(disconnectException,
                    "Failed to close connection after entry failure: PlayerId={PlayerId}, MatchingId={MatchingId}",
                    PlayerId, MatchingId);
            }
        }
    }

    private void ReleaseMatchingReservationOnce()
    {
        if (!PlayerId.HasValue || MatchingId <= 0 || Interlocked.CompareExchange(ref _matchingReservationReleaseReported, 1, 0) != 0)
        {
            return;
        }

        if (!TryBeginMatchEndHandling())
        {
            return;
        }

        try
        {
            _matchingLifecycle.ReleaseMatchingReservation(PlayerId.Value, MatchingId);
        }
        catch
        {
            Volatile.Write(ref _matchingLifecycleTerminalReported, 0);
            Volatile.Write(ref _matchingReservationReleaseReported, 0);
            throw;
        }
    }

    internal virtual void DisconnectForEntryFailure()
    {
        if (Interlocked.Exchange(ref _entryDisconnectIssued, 1) != 0)
        {
            return;
        }

        MarkDisconnectedByServer();
        try
        {
            using var packet = PacketMaker.G_TO_C_ERROR(ErrorCode.GAME_ENTRY_FAILED);
            Connection.TrySendAndDisconnect(packet);
        }
        catch (Exception ex)
        {
            Logger.LogError(ex, "입장 실패 응답 전송 실패: PlayerId={PlayerId}", PlayerId);
            Connection.Disconnect();
        }
    }

    internal void MarkMatchEndHandledExternally()
    {
        if (!TryBeginMatchEndHandling())
        {
            return;
        }

        Volatile.Write(ref _matchingLifecycleHandledExternally, 1);
    }

    private void PublishPlayerLeftOnce()
    {
        if (!PlayerId.HasValue || MatchingId <= 0 || !TryBeginMatchEndHandling())
            return;

        try
        {
            _matchingLifecycle.PublishPlayerLeft(PlayerId.Value, MatchingId);
        }
        catch
        {
            Volatile.Write(ref _matchingLifecycleTerminalReported, 0);
            throw;
        }
    }

    protected override void SendErrorResponse(ErrorCode errorCode, string message)
    {
        try
        {
            using var packet = PacketMaker.G_TO_C_ERROR(errorCode, message);
            TrySend(packet);
        }
        catch (Exception ex)
        {
            Logger.LogError(ex, "에러 응답 전송 실패: PlayerId={PlayerId}", PlayerId);
        }
    }

    public override void OnRemoved()
    {
        Logger.LogInformation("GameClient removed: PlayerId={PlayerId}", PlayerId);

        _ = RunWithMatchLock(() =>
        {
            _condition.ClearPeriodicBuffs();
            return Task.CompletedTask;
        }, _condition.ClearPeriodicBuffs);
        _sessionLeaveHandler.Handle(this);
    }

    internal void ApplyMatchStatus(PlayerMatchStatus status)
    {
        PlayerMatchStatus = status == PlayerMatchStatus.ELIMINATED
            ? PlayerMatchStatus.SPECTATING
            : status;
    }

    internal Action? MarkGameEndedAndPrepareLifecyclePublication()
    {
        Volatile.Write(ref _isGameEnded, true);
        if (!PlayerId.HasValue || MatchingId <= 0 || !TryBeginMatchEndHandling())
            return null;

        try
        {
            return _matchingLifecycle.PrepareGameCompletion(PlayerId.Value, MatchingId);
        }
        catch
        {
            Volatile.Write(ref _matchingLifecycleTerminalReported, 0);
            throw;
        }
    }

    public override void OnDisconnect()
    {
        if (Volatile.Read(ref _matchingLifecycleHandledExternally) != 0)
        {
        }
        else if (Volatile.Read(ref _entryCompleted) == 0)
        {
            HandleEntryFailureOnce();
        }
        else if (Volatile.Read(ref _disconnectedByServer) || _isServerStopping())
        {
            ReleaseMatchingReservationOnce();
        }
        else if (PlayerId.HasValue && MatchingId > 0 && !IsGameEnded)
        {
            if (IsEliminated)
            {
                ReleaseMatchingReservationOnce();
            }
            else
            {
                PublishPlayerLeftOnce();
            }
        }
        Logger.LogInformation("GameSession disconnected: PlayerId={PlayerId}", PlayerId);
    }
}
