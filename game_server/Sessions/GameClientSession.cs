using game_server.matches;
using game_server.matches.combat;
using game_server.matches.logging;
using game_server.players;
using MessagePack;
using Microsoft.Extensions.Logging;
using network.common;
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
///     플레이어 상태는 MatchRuntime의 참가자를 참조하고, 세션은 요청 처리와 전송을 담당한다.
///     기능별 요청 처리와 상태 전송 코드는 GameClientSession.* partial 파일에 나누어 둔다.
/// </summary>
public partial class GameClientSession : SessionBase
{
    private readonly Func<long, GameClientSession, GameClientSession?> _registerSessionCallback;
    private readonly Func<GameClientSession, bool> _removeSessionCallback;
    private readonly Func<bool> _isServerStopping;
    private readonly Func<Packet, bool> _trySendConnectSuccessResponse;

    private readonly GameMatchEntryService _matchEntry;
    private readonly IMatchEntryFailureHandler _entryFailureHandler;
    private readonly IMatchSessionCleanup _matchSessionCleanup;
    private readonly MatchCleanupService _matchCleanup;

    private readonly PlayerMovementService _movement;
    private readonly PlayerInteractionService _interactions;
    private readonly PlayerOrbGrowthService _orbGrowth;
    private readonly GameEventLogManager _gameEventLogManager;

    private MatchRuntime? _match;
    internal Player Player = null!;
    private readonly Dictionary<long, OrbVisualState> _lastSentOrbVisualStates = new();

    private int _entryCompleted;
    private int _entryFailureReported;
    private int _entryDisconnectIssued;
    private int _matchingLifecycleHandledExternally;
    private int _matchingLifecycleTerminalReported;
    private int _matchingReservationReleaseReported;
    private bool _isGameEnded;
    private bool _disconnectedByServer;

    internal GameClientSession(
        TcpConnection connection,
        ILogger logger,
        IRedisOperations redisOperations,
        Func<GameClientSession, bool> removeSessionCallback,
        MatchCleanupService matchCleanup,
        Func<long, GameClientSession, GameClientSession?> registerSessionCallback,
        GameEventLogManager gameEventLogManager,
        PlayerOrbGrowthService orbGrowth,
        PlayerMovementService movement,
        PlayerInteractionService interactions,
        IMatchSessionCleanup matchSessionCleanup,
        Func<bool> isServerStopping,
        IMatchEntryFailureHandler entryFailureHandler,
        GameMatchEntryService matchEntry,
        Func<Packet, bool>? trySendConnectSuccessResponse = null)
        : base(connection, logger, redisOperations)
    {
        _removeSessionCallback = removeSessionCallback;
        _matchCleanup = matchCleanup;
        _registerSessionCallback = registerSessionCallback;

        _gameEventLogManager = gameEventLogManager;
        _orbGrowth = orbGrowth;
        _movement = movement;
        _interactions = interactions;

        _matchEntry = matchEntry;
        _trySendConnectSuccessResponse = trySendConnectSuccessResponse ?? Connection.TrySend;
        _matchSessionCleanup = matchSessionCleanup;
        _isServerStopping = isServerStopping;
        _entryFailureHandler = entryFailureHandler;

        InitializeProtocolHandlers();
        Logger.LogInformation("GameClientSession created");
    }

    public new long? PlayerId { get; private set; }
    public long MatchingId { get; private set; }

    internal MatchRuntime Match => Volatile.Read(ref _match) ?? throw new InvalidOperationException("Session has not entered a match.");

    internal bool IsGameEnded => Volatile.Read(ref _isGameEnded);
    internal bool IsConnectionReleased => Connection.IsReleased;
    internal bool IsAcceptingMessages => Connection.IsAcceptingMessages;


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
        return protocolId is Protocol.C_TO_G_HEART_BEAT or Protocol.C_TO_G_MOVE;
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

        if (Player == null || Player.IsEliminated)
        {
            errorCode = ErrorCode.PLAYER_DEAD;
            return true;
        }

        if (IsGameEnded)
        {
            errorCode = ErrorCode.GAME_ALREADY_ENDED;
            return true;
        }

        if (MatchingId > 0 && Volatile.Read(ref _match)?.IsGameplayActive() != true)
        {
            errorCode = ErrorCode.GAME_NOT_STARTED;
            return true;
        }

        return false;
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
            MatchingId = matchingId;

            EnsureConnectionActive();
            Logger.LogInformation("Client connection authorized: PlayerId={PlayerId}, MatchingId={MatchingId}", playerId, matchingId);

            var runtime = _matchEntry.GetOrCreateMatch(matchingId);
            Volatile.Write(ref _match, runtime);
            await _matchEntry.PrepareMatchAsync(matchingId, runtime);
            EnsureConnectionActive();
            GameClientSession? previousSession = null;
            using (runtime.Enter())
            {
                if (runtime.IsEnded)
                {
                    Logger.LogWarning("GameServer connection rejected because the match is terminal: PlayerId={PlayerId}, MatchingId={MatchingId}", playerId, matchingId);
                    MarkDisconnectedByServer();
                    SendConnectFailure(ErrorCode.GAME_ALREADY_ENDED);
                    return;
                }

                Player = runtime.GetParticipant(playerId) ?? throw new InvalidOperationException("Match participant was not initialized.");
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

            Cell matchingSpawnCell;
            var humanPlayerIds = new List<long>();

            using (runtime.Enter())
            {
                if (runtime.IsEnded)
                {
                    throw new OperationCanceledException("Match became terminal during game entry initialization.");
                }

                if (!runtime.IsSetupComplete)
                {
                    throw new InvalidOperationException("Match setup is not complete.");
                }
                var playerProfiles = runtime.GetPlayerProfiles();
                foreach (var participant in playerProfiles)
                {
                    if (participant.PlayerId > 0)
                        humanPlayerIds.Add(participant.PlayerId);
                }

                if (!humanPlayerIds.Contains(playerId))
                {
                    throw new InvalidOperationException($"Player {playerId} is not part of match {matchingId}.");
                }
                matchingSpawnCell = Cell.Clone(runtime.SpawnCells[playerId]);
                EnsureConnectionActive();

                using (var rosterPacket = PacketMaker.G_TO_C_MATCH_ROSTER(matchingId, playerProfiles))
                {
                    if (!TrySend(rosterPacket))
                    {
                        throw new OperationCanceledException("Could not send initial match roster.");
                    }
                }

                runtime.BeginEntry(PlayerId.Value);

                Player.InitializeSpawn(matchingSpawnCell);

                Logger.LogInformation(
                    "Player {PlayerId} initial Area: {Area}, Position: ({PosX:F2},{PosY:F2}), Cell: ({CellX},{CellY})",
                    PlayerId, Player.CurrentArea, Player.Position?.X, Player.Position?.Y, Player.Cell?.X,
                    Player.Cell?.Y);



                SendInteractableList();

                SendGroundItemSnapshot(Player.CurrentArea);
                SendOrbList();
                SendOrbUpgradeInfo(_orbGrowth.GetOrbUpgradeInfo(runtime, Player));
                SendSummonStoneState();
                SendDoorStateList();
                SendPressureFieldState();
                LogInitialInventory();

                SyncPlayersOnEntry();
                EnsureConnectionActive();
            }

            await _matchEntry.RecordEntryAsync(matchingId, PlayerId.Value, humanPlayerIds);
            EnsureConnectionActive();

            if (humanPlayerIds.Count == 1)
            {
                runtime.MarkPlayerReady(PlayerId.Value);
            }

            SendMatchStartCountdown(matchingId);
            using var successResponse = CreateConnectResultPacket(true, ErrorCode.SUCCESS, matchingId, matchingSpawnCell);
            using (runtime.Enter())
            {
                if (runtime.IsEnded)
                {
                    throw new OperationCanceledException("Match became terminal during game entry.");
                }
                if (!Connection.TryMarkAuthenticated(() => Volatile.Write(ref _entryCompleted, 1)))
                {
                    throw new OperationCanceledException("Connection closed before authentication commit.");
                }
            }

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

    private void SyncPlayersOnEntry()
    {
        if (!PlayerId.HasValue || Player.IsEliminated)
        {
            return;
        }

        var match = Volatile.Read(ref _match);
        if (match == null)
        {
            return;
        }

        using (match.Enter())
        {
            if (match.IsEnded || !PlayerId.HasValue || Player.IsEliminated)
            {
                return;
            }

            var sessions = new List<GameClientSession>();
            foreach (var session in match.GetSessions())
            {
                if (!session.PlayerId.HasValue || session.PlayerId == PlayerId)
                {
                    continue;
                }
                if (session.Player.IsEliminated || session.Player.CurrentArea != Player.CurrentArea)
                {
                    continue;
                }
                sessions.Add(session);
            }

            if (sessions.Count > 0)
            {
                using var others = PacketMaker.G_TO_C_OBJECT_INFO(sessions.Select(s => _movement.CreateGameObjectInfo(s.Match, s.Player, s.Player.State)).ToList());
                TrySend(others);
            }

            using (var mine = PacketMaker.G_TO_C_OBJECT_INFO([_movement.CreateGameObjectInfo(Match, Player, Player.State)]))
            {
                foreach (var session in sessions)
                {
                    session.TrySend(mine);
                }
            }

            var bots = match.Bots.GetBots().Where(bot => !bot.Player.IsEliminated && bot.Player.CurrentArea == Player.CurrentArea).ToList();
            var objects = bots.Select(bot => match.Bots.SynthesizeGameObjectInfo(bot.PlayerId)).OfType<GameObjectInfo>().ToList();
            if (objects.Count <= 0)
            {
                return;
            }

            using var packet = PacketMaker.G_TO_C_OBJECT_INFO(objects);
            TrySend(packet);
        }

    }

    private void SendPressureFieldState()
    {
        if (MatchingId <= 0)
        {
            return;
        }
        if (Match.Closures.GameStartTime is not { } gameStartTime)
        {
            return;
        }

        using var packet = Packet.Create((int)Protocol.G_TO_C_SWARM_FIELD_STATE);
        packet.SetBody(MessagePackSerializer.Serialize(new G_TO_C_SWARM_FIELD_STATE
        {
            StartedAtUnixMs = new DateTimeOffset(gameStartTime).ToUnixTimeMilliseconds()
        }));
        TrySend(packet);
    }

    private void LogInitialInventory()
    {
        var inventory = Player.Orbs;
        _gameEventLogManager.LogOrbBoardTransition(MatchingId, PlayerId.Value, inventory.GetAllItems(),
            inventory.GetOrderedOrbs().FirstOrDefault()?.ItemId ?? 0, Player.CurrentArea.ToString(), "connection_sync", isBot: false);
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
            Logger.LogError(ex, "Failed to close connection after committed entry response failure: PlayerId={PlayerId}, MatchingId={MatchingId}", PlayerId, MatchingId);
        }
    }

    private void SendMatchStartCountdown(long matchingId)
    {
        if (Match.IsEnded || !Match.EntryDeadlineUtc.HasValue)
        {
            return;
        }

        using var packet = Packet.Create((int)Protocol.G_TO_C_MATCH_START_COUNTDOWN, PlayerId ?? 0);
        packet.SetBody(MessagePackSerializer.Serialize(new G_TO_C_MATCH_START_COUNTDOWN
        {
            MatchingId = matchingId,
            StartsAtUnixMs = Match.StartsAtUtc is { } startsAt ? new DateTimeOffset(startsAt).ToUnixTimeMilliseconds() : 0,
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
        var match = Volatile.Read(ref _match);
        if (match == null)
        {
            return Task.CompletedTask;
        }

        using (match.Enter())
        {
            if (match.IsEnded)
            {
                return Task.CompletedTask;
            }
            bool wasScheduled = match.StartsAtUtc.HasValue;
            match.MarkPlayerReady(PlayerId.Value);
            if (!wasScheduled && match.StartsAtUtc.HasValue)
            {
                foreach (var participant in match.GetSessions())
                {
                    participant.SendMatchStartCountdown(MatchingId);
                }
            }
            else
            {
                SendMatchStartCountdown(MatchingId);
            }
        }
        return Task.CompletedTask;
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
            Logger.LogError(ex, "Failed to report game entry failure: PlayerId={PlayerId}, MatchingId={MatchingId}", PlayerId, MatchingId);
            MarkDisconnectedByServer();
            try
            {
                Connection.Disconnect();
            }
            catch (Exception disconnectException)
            {
                Logger.LogError(disconnectException, "Failed to close connection after entry failure: PlayerId={PlayerId}, MatchingId={MatchingId}", PlayerId, MatchingId);
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
            _matchSessionCleanup.ReleaseMatchingReservation(PlayerId.Value, MatchingId);
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
        {
            return;
        }

        try
        {
            _matchSessionCleanup.PublishPlayerLeft(PlayerId.Value, MatchingId);
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

        var match = Volatile.Read(ref _match);
        if (match == null)
        {
            Player?.ClearPeriodicBuffs();
        }
        else
        {
            using (match.Enter())
            {
                if (ReferenceEquals(Player?.Session, this))
                {
                    Player?.ClearPeriodicBuffs();
                }
            }
        }
        if (!PlayerId.HasValue)
        {
            return;
        }

        bool removed = _removeSessionCallback(this);
        if (!removed)
        {
            Logger.LogDebug("Ignored removal from a superseded game session: PlayerId={PlayerId}, MatchingId={MatchingId}", PlayerId.Value, MatchingId);
            if (MatchingId > 0)
            {
                _matchCleanup.CleanupIfNoHumanSessionsRemain(MatchingId);
            }
            return;
        }

        Logger.LogInformation("Game client session removed: PlayerId={SessionPlayerId}", PlayerId.Value);

        if (Player != null && MatchingId > 0 && Player.CurrentArea != AreaType.None)
        {
            using var leavePacket = PacketMaker.G_TO_C_AREA_PLAYER_LEAVE(PlayerId.Value);
            var sameAreaSessions = new List<GameClientSession>();
            foreach (var other in Match.GetSessions())
            {
                if (ReferenceEquals(other, this))
                {
                    continue;
                }
                if (other.Player.CurrentArea != Player.CurrentArea)
                {
                    continue;
                }
                sameAreaSessions.Add(other);
            }

            foreach (var other in sameAreaSessions)
            {
                other.TrySend(leavePacket);
            }

            Logger.LogInformation(
                "Broadcasted disconnected player leave: PlayerId={PlayerId}, MatchingId={MatchingId}, Area={Area}, Receivers={ReceiverCount}",
                PlayerId.Value,
                MatchingId,
                Player.CurrentArea,
                sameAreaSessions.Count);
        }

        if (MatchingId > 0)
        {
            _matchCleanup.CleanupIfNoHumanSessionsRemain(MatchingId);
        }
    }



    internal Action? MarkGameEndedAndPrepareLifecyclePublication()
    {
        Volatile.Write(ref _isGameEnded, true);
        if (!PlayerId.HasValue || MatchingId <= 0 || !TryBeginMatchEndHandling())
        {
            return null;
        }

        try
        {
            return _matchSessionCleanup.PrepareGameCompletion(PlayerId.Value, MatchingId);
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
            if (Player == null || Player.IsEliminated)
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
