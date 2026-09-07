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
///     Game Server에 접속한 클라이언트의 TCP 세션.
///     입장 티켓으로 플레이어와 매치를 확인하고, 수신한 요청을 담당 서비스에 전달한다.
///     플레이어의 세션 상태를 관리하며, 처리 결과를 클라이언트에 보내고 연결 종료 시 매치 이탈을 처리한다.
///     매치 전체의 상태와 게임 규칙은 매치 런타임과 각 서비스가 담당한다.
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
    private readonly MatchEliminationService _matchEliminations;
    private readonly PlayerCondition _condition = new();
    private readonly IPlayerGrowthHandler _growth;
    private readonly ItemCombinationService _itemCombinations;
    private readonly MovementValidationService _movementValidation;

    private int _entryCompleted;
    private int _entryFailureReported;
    private int _entryDisconnectIssued;
    private int _matchingLifecycleHandledExternally;
    private int _matchingLifecycleTerminalReported;
    private int _matchingReservationReleaseReported;

    private readonly PlayerInteractionState _interactions = new();
    private const int PendingOrbDraftCost = 0;
    private Vector3f _lastValidatedVelocity = new(0f, 0f, 0f);
    private Cell? _lastValidCell;

    private float _lastValidatedRotation;
    private float? _orbOrbitPhaseDegrees;
    private long _lastMoveReceiptTimestamp;
    private readonly MovementPacketQueue _movementPacketQueue;
    private long _lastMoveAcknowledgementTimestamp;
    private bool _hasProcessedMoveInputSequence;
    private uint _lastProcessedMoveInputSequence;
    private bool _hasPendingOrbDraft;
    public int FreeSummonCharges { get; internal set; }

    private Timer? _periodicBuffTimer;
    internal static Action<long, float, float>? SwarmDummyMoveCallback { get; set; }
    internal static Action<long, long>? SwarmHeartPickupCallback { get; set; }

    private MatchDoorState? Doors => Volatile.Read(ref _match)?.Doors;

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
        ItemCombinationService itemCombinations,
        MovementValidationService movementValidation,
        Func<Packet, bool>? trySendConnectSuccessResponse = null,
        TimeProvider? movementTimeProvider = null)
        : base(connection, logger, redisOperations)
    {
        _sessionLeaveHandler = sessionLeaveHandler;
        _registerSessionCallback = registerSessionCallback;

        _gameEventLogManager = gameEventLogManager;
        _matchEliminations = matchEliminations;
        _movementPacketQueue = new MovementPacketQueue(() => Connection.IsAcceptingMessages, movementTimeProvider);
        _growth = growth;

        _matchEntry = matchEntry;
        _itemCombinations = itemCombinations;
        _movementValidation = movementValidation;
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
    public AreaType CurrentArea { get; private set; } = AreaType.None;
    private PlayerState CurrentState { get; set; } = PlayerState.IDLE;
    public Vector3f? LastValidatedPosition { get; private set; }

    public PlayerMatchStatus PlayerMatchStatus { get; private set; } = PlayerMatchStatus.ACTIVE;
    private bool _isGameEnded;
    private bool _disconnectedByServer;

    internal bool IsGameEnded => Volatile.Read(ref _isGameEnded);
    internal int CurrentHealth => Health;
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
        ProtocolRouter.RegisterHandler(Protocol.C_TO_G_DESTROY_ORB,
            async bytes => await HandleMessage<C_TO_G_DESTROY_ORB>(bytes, HandleDestroyOrb));
        ProtocolRouter.RegisterHandler(Protocol.C_TO_G_DEV_DUMMY_MOVE,
            async bytes => await HandleMessage<C_TO_G_DEV_DUMMY_MOVE>(bytes, HandleDevDummyMove));
        ProtocolRouter.RegisterHandler(Protocol.C_TO_G_SWARM_GROWTH_PICK,
            async bytes => await HandleMessage<C_TO_G_SWARM_GROWTH_PICK>(bytes, HandleSwarmGrowthPick));
        ProtocolRouter.RegisterHandler(Protocol.C_TO_G_SWARM_ORB_DECISION,
            async bytes => await HandleMessage<C_TO_G_SWARM_ORB_DECISION>(bytes, HandleSwarmOrbDecision));
        ProtocolRouter.RegisterHandler(Protocol.C_TO_G_COMBINE_ITEMS,
            async bytes => await HandleMessage<C_TO_G_COMBINE_ITEMS>(bytes, HandleCombineItems));
        ProtocolRouter.RegisterHandler(Protocol.C_TO_G_USE_INGAME_ITEM,
            async bytes => await HandleMessage<C_TO_G_USE_INGAME_ITEM>(bytes, HandleUseInGameItem));
        ProtocolRouter.RegisterHandler(Protocol.C_TO_G_GROUND_ITEM_PICKUP,
            async bytes => await HandleMessage<C_TO_G_GROUND_ITEM_PICKUP>(bytes, HandleGroundItemPickup));
        ProtocolRouter.RegisterHandler(Protocol.C_TO_G_DROP_GROUND_ITEM,
            async bytes => await HandleMessage<C_TO_G_DROP_GROUND_ITEM>(bytes, HandleDropGroundItem));
        ProtocolRouter.RegisterHandler(Protocol.C_TO_G_PLAYER_STATE,
            async bytes => await HandleMessage<C_TO_G_PLAYER_STATE>(bytes, HandlePlayerState));
        ProtocolRouter.RegisterHandler(Protocol.C_TO_G_DOOR_OPEN_REQUEST,
            async bytes => await HandleMessage<C_TO_G_DOOR_OPEN_REQUEST>(bytes, HandleDoorOpenRequest));
        ProtocolRouter.RegisterHandler(Protocol.C_TO_G_RNG_COLLECT_START,
            async bytes => await HandleMessage<C_TO_G_RNG_COLLECT_START>(bytes, HandleRngCollectStart));
        ProtocolRouter.RegisterHandler(Protocol.C_TO_G_RNG_COLLECT_FINISH,
            async bytes => await HandleMessage<C_TO_G_RNG_COLLECT_FINISH>(bytes, HandleRngCollectFinish));
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
            LastValidatedPosition = CellToWorldPosition(matchingSpawnCell);
            _lastValidCell = Cell.Clone(matchingSpawnCell);
            _lastValidatedRotation = 0f;
            CurrentArea = GameMapData.GetCurrentArea(CurrentMapId, matchingSpawnCell);

            Logger.LogInformation(
                "Player {PlayerId} initial Area: {Area}, Position: ({PosX:F2},{PosY:F2}), Cell: ({CellX},{CellY})",
                PlayerId, CurrentArea, LastValidatedPosition?.X, LastValidatedPosition?.Y, _lastValidCell?.X,
                _lastValidCell?.Y);

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

            SendInteractableList(CurrentArea);
            SendInteractCooldownSnapshot();
            SendGroundItemSnapshot(CurrentArea);
            SendInGameInventoryList();
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
            inventory.GetEquippedBattleItem()?.ItemId ?? 0, CurrentArea.ToString(), "connection_sync", isBot: false);
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

    /// <summary>외부에서 매치 종료 처리를 맡았으므로 연결 종료 시 중복 처리하지 않도록 표시한다.</summary>
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
            StopAllPeriodicBuffs();
            return Task.CompletedTask;
        }, StopAllPeriodicBuffs);
        _sessionLeaveHandler.Handle(this);
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
