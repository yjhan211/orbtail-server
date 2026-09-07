using System.Collections.Concurrent;
using game_server.network;
using game_server.services;
using MessagePack;
using Microsoft.Extensions.Logging;
using network.common;
using network.common.data;
using network.common.data.models;
using network.core;
using network.gameentry;
using network.helpers;
using network.infrastructure.redis;
using network.packets;

namespace game_server.sessions;

/// <summary>
///     GameServer 인계 뒤의 TCP 클라이언트 연결 하나. 프로토콜 검증과 연결별 상태를 소유하고, 매치 공유 상태는
///     GameServer 매니저에 남긴다. 권위 상태를 바꾸는 핸들러(채집·픽업·성장·조합)는 <see cref="RunWithMatchLock"/>로
///     매치 잠금(<see cref="MatchRuntime.Sync"/>) 안에서 동기 실행되며 응답도 그 안에서 보낸다 — 잠금 안 송신은
///     큐 적재뿐이라 I/O를 기다리지 않는다. 인증은 Redis 입장 커밋과 초기 스냅샷이 끝난 뒤에만 보이고, 성공 응답은
///     잠금 밖에서 큐에 넣는다. 매치 종료 결과·GAME_END는 같은 잠금 안에서 보내고 영속·lifecycle 발행은 잠금이
///     풀린 뒤 후처리로 돈다.
///     퇴장은 GameSessionLeaveHandler에, 입장 실패는 IMatchEntryFailureHandler에,
///     퇴장·완료 알림과 예약 해제는 IGameSessionLifecycle에 요청한다.
/// </summary>
public partial class GameClientSession : SessionBase
{
    private const int InitialCorruption = 0;


    private readonly PlayerCondition _condition = new();
    private readonly Func<long, List<GameClientSession>> _getSessionsByMatch;

    private readonly GameSessionLeaveHandler _sessionLeaveHandler;
    // 입장 전에는 null. 입장 후에는 Store를 재조회하지 않고 같은 매치 인스턴스를 사용한다.
    private MatchRuntime? _match;
    private MatchRuntime Match => Volatile.Read(ref _match) ?? throw new InvalidOperationException("Session has not entered a match.");
    /// <summary>
    ///     Queues an already-built successful entry response after authentication has committed. Production uses
    ///     <see cref="TcpConnection.TrySend"/>; tests can inject a sender to verify the match monitor boundary.
    /// </summary>
    private readonly Func<Packet, bool> _trySendConnectSuccessResponse;
    private readonly Func<long, GameClientSession, GameClientSession?> _registerSessionCallback;
    private readonly IGameSessionLifecycle _matchingLifecycle;
    private readonly IMatchEntryFailureHandler _entryFailureHandler;
    private readonly Func<bool> _isServerStopping;
    private readonly GameEventLogManager _gameEventLogManager;
    private readonly MatchEliminationService _matchEliminations;
    private readonly IPlayerGrowthHandler _growth;

    private readonly GameMatchEntryService _matchEntry;
    private readonly ItemCombinationService _itemCombinations;
    private readonly MovementValidationService _movementValidation;



    // #229 6단계 수면 회복 → 2026-08-17 재조정: 진입 시각(준비 1초)과 마지막 교전 시각
    // (가해·피해 뒤 3초 진입 잠금)을 세션이 들고, 회복 정산(1초 틱)은 아레나 틱이 돈다.
    internal DateTime SwarmSleepStartedAtUtc { get => _condition.SleepStartedAtUtc; set => _condition.SleepStartedAtUtc = value; }
    internal DateTime SwarmLastCombatAtUtc { get => _condition.LastCombatAtUtc; set => _condition.LastCombatAtUtc = value; }
    // 단일 절단 치명상 (#232): 성공 뒤 8초는 수면 진입·회복 틱이 막힌다.
    internal DateTime SwarmHealLockUntilUtc { get => _condition.HealLockUntilUtc; set => _condition.HealLockUntilUtc = value; }

    private int _entryCompleted;
    private int _entryFailureReported;
    private int _entryDisconnectIssued;
    private int _matchingLifecycleHandledExternally;
    private int _matchingLifecycleTerminalReported;
    private int _matchingReservationReleaseReported;
    private long[] _matchHumanPlayerIds = [];

    /// <summary>START/FINISH 대기와 문 게이지 상태. 연결별로 소유하고 매치 잠금 안에서 사용한다.</summary>
    private readonly PlayerInteractionState _interactions = new();

    private int _pendingOrbDraftCost = 0;


    private Vector3f? _lastValidatedPosition;
    private Vector3f _lastValidatedVelocity = new(0f, 0f, 0f);
    private Cell? _lastValidCell;

    private float _lastValidatedRotation;
    // 오브 궤도 위상 (#232): 검증 이동 거리로 적산한 권위값 — 시드는 PlayerId. null = 아직 시드 전.
    private float? _orbOrbitPhaseDegrees;
    // Stopwatch ticks: client timestamps are telemetry only and never extend movement authority.
    private long _lastMoveReceiptTimestamp;
    private readonly MovementPacketQueue _movementPacketQueue;
    private long _lastMoveAcknowledgementTimestamp;
    private bool _hasProcessedMoveInputSequence;
    private uint _lastProcessedMoveInputSequence;

    // #219 M2 3택 드래프트: 개봉이 연 드래프트 권리와 개봉 시점 확정 비용
    private bool _hasPendingOrbDraft;

    /// <summary>열쇠 (#222 M4): 무료 소환 충전 수 — 획득/소비는 GroundItem·OrbSummon partial.</summary>
    public int FreeSummonCharges { get; internal set; }


    private Timer? _periodicBuffTimer;

    /// <summary>절단 실험 더미 조종 훅 (#226 실험장, 개발용) — (matchingId, dirX, dirY).</summary>
    internal static Action<long, float, float>? SwarmDummyMoveCallback { get; set; }

    /// <summary>
    ///     하트 픽업 시 앞줄 오브 HP 회복 훅 (#222 M4) — 원작 하트는 스쿼드 유닛도 회복한다.
    ///     GameServer가 스웜 매치 초기화 시 배선한다 (사람·봇 픽업 공통).
    /// </summary>
    internal static Action<long, long>? SwarmHeartPickupCallback { get; set; }


    private MatchDoorState? Doors => Volatile.Read(ref _match)?.Doors;

    internal GameClientSession(
        TcpConnection connection,
        ILogger logger,
        IRedisOperations redisOperations,
        GameSessionLeaveHandler sessionLeaveHandler,
        Func<long, GameClientSession, GameClientSession?> registerSessionCallback,
        Func<long, List<GameClientSession>> getSessionsByMatch,

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
        _getSessionsByMatch = getSessionsByMatch;

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

        // ReSharper disable once VirtualMemberCallInConstructor
        InitializeProtocolHandlers();
        Logger.LogInformation("GameClientSession created");
    }

    /// <summary>
    ///     권위 상태를 바꾸는 핸들러 core를 매치 잠금 안에서 동기로 돌린다 — 50ms 전투 틱과 같은 잠금이라
    ///     이 안의 Send 순서가 곧 상태 변경 순서다. 런타임이 없거나 터미널이면 core 대신 거부 응답만 보낸다.
    ///     core가 던지면 잠금을 풀고 SessionBase가 G_TO_C_ERROR를 보낸다(앞서 보낸 패킷은 그대로).
    /// </summary>
    private Task RunWithMatchLock(Func<Task> handleRequest, Action rejectRequest)
    {
        ArgumentNullException.ThrowIfNull(handleRequest);
        ArgumentNullException.ThrowIfNull(rejectRequest);

        MatchRuntime? runtime = Volatile.Read(ref _match);
        if (runtime == null)
        {
            rejectRequest();
            return Task.CompletedTask;
        }

        using MatchScope scope = runtime.Enter();
        if (runtime.IsTerminal)
        {
            rejectRequest();
            return Task.CompletedTask;
        }

        Task handlingTask = handleRequest() ??
            throw new InvalidOperationException("Match request handler returned a null task.");
        if (!handlingTask.IsCompleted)
        {
            throw new InvalidOperationException(
                "Match request handler must complete synchronously under the match lock.");
        }

        handlingTask.GetAwaiter().GetResult();
        return Task.CompletedTask;
    }

    public new long? PlayerId { get; private set; }
    public MapId CurrentMapId { get; private set; }
    public long MatchingId { get; private set; }
    public AreaType CurrentArea { get; private set; } = AreaType.None;
    // 접속 스냅샷에 포함할 행동 상태. 수면 여부는 _condition.IsSleeping에서 별도로 확인한다.
    private PlayerState CurrentState { get; set; } = PlayerState.IDLE;

    /// <summary>마지막 검증된 월드 좌표 — 근접 전투·드랍 위치 등 거리 판정용.</summary>
    public Vector3f? LastValidatedPosition => _lastValidatedPosition;

    public PlayerMatchStatus PlayerMatchStatus { get; private set; } = PlayerMatchStatus.ACTIVE;

    // terminal lifecycle 원인을 고르는 상태 플래그
    /// <summary>게임 결과가 확정된 뒤에는 completed terminal을 유지한다.</summary>
    private bool _isGameEnded;
    /// <summary>서버 주도 종료는 released terminal로 reservation만 해제한다.</summary>
    private bool _isServerInitiatedDisconnect;

    /// <summary>
    ///     탈락/관전 상태에서 행동 가능한지 체크
    /// </summary>
    internal bool IsGameEnded => Volatile.Read(ref _isGameEnded);
    internal int CurrentHealth => Health;
    public bool IsEliminated => PlayerMatchStatus == PlayerMatchStatus.ELIMINATED || PlayerMatchStatus == PlayerMatchStatus.SPECTATING;

    // 인게임 스탯 (게임 종료 시 초기화)
    private int Health { get => _condition.Health; set => _condition.Health = value; }

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
        // 오브/배틀아이템 조합 — 이름은 부품 결합이지만 현행 오브 머지가 쓰는 프로토콜 (#238에서 게이트 밖으로 복구)
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

        // 문 프로토콜
        ProtocolRouter.RegisterHandler(Protocol.C_TO_G_DOOR_OPEN_REQUEST,
            async bytes => await HandleMessage<C_TO_G_DOOR_OPEN_REQUEST>(bytes, HandleDoorOpenRequest));

        // RNG 채집 프로토콜 (v0.2.1, #79)
        // RNG 채집 2단계 프로토콜 (#134)
        ProtocolRouter.RegisterHandler(Protocol.C_TO_G_RNG_COLLECT_START,
            async bytes => await HandleMessage<C_TO_G_RNG_COLLECT_START>(bytes, HandleRngCollectStart));
        ProtocolRouter.RegisterHandler(Protocol.C_TO_G_RNG_COLLECT_FINISH,
            async bytes => await HandleMessage<C_TO_G_RNG_COLLECT_FINISH>(bytes, HandleRngCollectFinish));

        // 구역 이동 프로토콜 (GDD v0.0.8: 문/계단 마커 방식)

        // 소셜 액션 프로토콜
        ProtocolRouter.RegisterHandler(Protocol.C_TO_G_SOCIAL_ACTION,
            async bytes => await HandleMessage<C_TO_G_SOCIAL_ACTION>(bytes, HandleSocialAction));
    }

    private async Task HandleConnect(C_TO_G_CONNECT msg)
    {
        if (PlayerId.HasValue || MatchingId > 0)
        {
            Logger.LogWarning(
                "Repeated game authentication attempt: PlayerId={PlayerId}, MatchingId={MatchingId}",
                PlayerId,
                MatchingId);
            SendConnectResult(false, ErrorCode.AUTH_FAILED, "이미 인증된 세션입니다", disconnectAfterSend: true);
            return;
        }

        bool registered = false;
        try
        {
            GameEntryContext? entryContext = await _matchEntry.ConsumeTicketAsync(msg.GameEntryTicket);
            if (entryContext == null)
            {
                EnsureConnectionActive();
                Logger.LogWarning(
                    "GameServer connection rejected: invalid, expired, replayed, or another node's handoff ticket");
                SendConnectResult(false, ErrorCode.AUTH_FAILED, "게임 접속 인증에 실패했습니다",
                    disconnectAfterSend: true);
                return;
            }

            long matchingId = entryContext.MatchingId;
            long playerId = entryContext.PlayerId;
            // 신원은 소비 응답 직후, 소켓 상태 확인 전에 확정한다. GETDEL 응답이 유실되면 여기에 정확한 신원이
            // 없고, 되돌리기는 user_server의 입장 마감이 맡는다.
            PlayerId = playerId;
            CurrentMapId = Config.SWARM_MATCH_MAP;
            MatchingId = matchingId;
            EnsureConnectionActive();

            Logger.LogInformation(
                "Client connection authorized: PlayerId={PlayerId}, MatchingId={MatchingId}",
                playerId,
                matchingId);

            // 세션 등록은 매치 잠금 안에서 — 사람 없음 정리가 등록과 발행 사이로 끼어들 수 없다.
            MatchRuntime runtime = _matchEntry.GetOrCreateMatch(matchingId);
            Volatile.Write(ref _match, runtime);
            GameClientSession? previousSession = null;
            using (runtime.Enter())
            {
                if (runtime.IsTerminal)
                {
                    Logger.LogWarning(
                        "GameServer connection rejected because the match is terminal: PlayerId={PlayerId}, MatchingId={MatchingId}",
                        playerId,
                        matchingId);
                    MarkServerInitiatedDisconnect();
                    SendConnectResult(false, ErrorCode.GAME_ALREADY_ENDED, "이미 종료된 게임입니다",
                        disconnectAfterSend: true);
                    return;
                }

                if (!Connection.TryRunIfActive(
                        () => previousSession = _registerSessionCallback(playerId, this)))
                    throw new OperationCanceledException("Connection closed before session registration.");
                registered = true;
            }
            // 이전 연결 종료는 새 연결의 상태 잠금과 매치 잠금을 벗어난 뒤 실행한다.
            if (previousSession != null)
            {
                previousSession.MarkServerInitiatedDisconnect();
                previousSession.ForceDisconnect();
            }

            // 매치 구성(사람·봇·스폰)은 manifest에서 매치당 한 번 확정한다. ticket은 신원만 증명하므로
            // 이 사람이 정말 이 매치의 참가자인지도 여기서 가른다.
            MatchComposition composition = await _matchEntry.LoadCompositionAsync(matchingId, CurrentMapId, runtime);
            EnsureConnectionActive();
            if (!composition.HumanPlayerIds.Contains(playerId))
                throw new InvalidOperationException($"Player {playerId} is not part of match {matchingId}.");
            Volatile.Write(ref _matchHumanPlayerIds, composition.HumanPlayerIds.ToArray());

            // 명단을 먼저 전송해야 이후 등장 패킷의 이름과 외형을 클라이언트가 해석할 수 있다.
            using (var rosterPacket = PacketMaker.G_TO_C_MATCH_ROSTER(matchingId, composition.PlayerRoster.ToList()))
                if (!TrySend(rosterPacket))
                    throw new OperationCanceledException("Could not send initial match roster.");

            RunUnderLiveMatch(runtime, () =>
            {
                foreach (var bot in Match.Bots.GetBots(matchingId))
                    if (!bot.IsEliminated)
                    {
                        _gameEventLogManager.SetPlayerArea(matchingId, bot.PlayerId, bot.CurrentArea.ToString());
                    }

                // Initialize match-scoped area state once; manager implementations are idempotent.
                int matchSeed = MatchSpawnData.GetDeterministicSeed(matchingId);
                _gameEventLogManager.BeginMatch(matchingId, matchSeed);
                foreach (var bot in Match.Bots.GetBots(matchingId))
                {
                    _gameEventLogManager.LogSpawnAssignment(
                        matchingId,
                        bot.PlayerId,
                        matchSeed,
                        MatchSpawnData.GetAnchorIndex(bot.Cell),
                        bot.Cell.X,
                        bot.Cell.Y,
                        bot.CurrentArea.ToString(),
                        isBot: true);
                }

                MatchStartGate.RegisterHumanPlayer(
                    matchingId, PlayerId.Value, composition.HumanPlayerIds.Count, composition.Mode);
            });

            // 초기 위치는 매치 구성의 스폰으로 정한다. PlayerInfo는 프로필 조회에만 사용한다.
            Cell matchingSpawnCell;
            {
                var playerInfo = await PlayerInfo.Load(RedisOperations, PlayerId.Value);
                if (playerInfo == null)
                    throw new InvalidOperationException($"PlayerInfo not found for authenticated player {playerId}.");

                Match.Roster.UpdatePlayerProfile(playerId, playerInfo.Name, playerInfo.WearItemIdList);

                matchingSpawnCell = Cell.Clone(composition.SpawnCells[playerId]);
                _lastValidatedPosition = CellToWorldPosition(matchingSpawnCell);
                _lastValidCell = Cell.Clone(matchingSpawnCell);
                _lastValidatedRotation = 0f;
                CurrentArea = GameMapData.GetCurrentArea(CurrentMapId, matchingSpawnCell);
                Logger.LogInformation(
                    "Player {PlayerId} initial Area: {Area}, Position: ({PosX:F2},{PosY:F2}), Cell: ({CellX},{CellY})",
                    PlayerId, CurrentArea, _lastValidatedPosition?.X, _lastValidatedPosition?.Y, _lastValidCell?.X,
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

                if (CurrentArea != AreaType.None)
                {
                    SendInteractableList(CurrentArea);
                    SendInteractCooldownSnapshot();
                    SendGroundItemSnapshot(CurrentArea);
                }
            }
            EnsureConnectionActive();

            SendInGameInventoryList();
            SendSummonStoneState();
            var connectionBoard = Match.Inventory.GetPlayerInventory(PlayerId.Value);
            _gameEventLogManager.LogOrbBoardTransition(
                MatchingId, PlayerId.Value, connectionBoard.GetAllItems(),
                connectionBoard.GetEquippedBattleItem()?.ItemId ?? 0, CurrentArea.ToString(), "connection_sync", isBot: false);

            // Send the initial door and mission snapshots.
            RunUnderLiveMatch(
                runtime,
                () => Doors?.Initialize(Array.Empty<AreaType>()));
            SendDoorStateList();

            // 스웜 모드(M4)는 시간 웨이브 폐쇄를 쓰므로 폐쇄 스냅샷을 복원해야 한다.
            SendAreaClosureStateSnapshot();

            // Send other players and broadcast this player's authoritative snapshot.
            await BroadcastPlayerJoin();
            EnsureConnectionActive();

            await _matchEntry.CommitAsync(
                matchingId,
                PlayerId.Value,
                composition.HumanPlayerIds);
            EnsureConnectionActive();

            // The standalone submission client is only ready after the full initial snapshot
            // has been sent. Starting the countdown earlier lets bots consume finite room stock
            // while the human client is still loading the match.
            if (composition.HumanPlayerIds.Count == 1)
            {
                MatchStartGate.MarkHumanReady(matchingId, PlayerId.Value);
            }

            // Authentication succeeds only after every fallible initialization and initial
            // snapshot step has completed. Build the complete success response before that
            // commit so serialization failure still follows the entry-abort path.
            SendMatchStartCountdown(matchingId);
            using Packet successResponse = CreateConnectResultPacket(
                true,
                ErrorCode.SUCCESS,
                "Connected to GameServer",
                matchingId,
                matchingSpawnCell);
            RunUnderLiveMatch(runtime, () =>
            {
                if (!Connection.TryMarkAuthenticated(() => Volatile.Write(ref _entryCompleted, 1)))
                    throw new OperationCanceledException("Connection closed before authentication commit.");
            });
            if (!TryPublishCommittedConnectResult(successResponse))
                return;
            Logger.LogInformation("Client connected successfully: PlayerId={PlayerId}", PlayerId);
        }
        catch (Exception ex)
        {
            Logger.LogError(ex, "Failed to handle connect");

            // Authentication and the local entry flag commit together. Once visible, an
            // ACK publication failure is connection-local: never roll entry back or abort
            // the whole match, and never send a contradictory negative CONNECT_RESULT.
            if (Volatile.Read(ref _entryCompleted) != 0)
            {
                CloseAfterCommittedEntryResponseFailure();
                return;
            }

            // 매치 잠금 안에서 이미 등록된 세션이다. 입장 중단 경로가 등록된 수신자 전원의 터미널
            // 에러/끊기를 한 번에 소유하므로 여기서 경쟁하는 CONNECT_RESULT를 보내지 않는다.
            if (registered)
            {
                if (!ReportEntryFailureOnce())
                {
                    MarkServerInitiatedDisconnect();
                    Connection.Disconnect();
                }
                return;
            }

            // 등록 전 실패(거부된 인계 티켓 등)는 어떤 수신자 스냅샷도 이 소켓을 소유하지 않으므로
            // 직접 응답한다.
            MarkServerInitiatedDisconnect();
            SendConnectResult(false, ErrorCode.FATAL, "게임 서버 연결 처리 중 오류가 발생했습니다",
                disconnectAfterSend: true);
        }
    }

    /// <summary>
    ///     입장 초기화 블록을 매치 잠금 안에서 돌린다 — await 사이에 매치가 끝났으면 상태를 되살리는 대신
    ///     입장을 중단한다.
    /// </summary>
    private void RunUnderLiveMatch(MatchRuntime runtime, Action initialize)
    {
        using MatchScope scope = runtime.Enter();
        if (runtime.IsTerminal)
            throw new OperationCanceledException("Match became terminal during game entry.");

        initialize();
    }

    private void EnsureConnectionActive()
    {
        if (Connection.IsReleased)
            throw new OperationCanceledException("Connection closed during game entry.");
    }

    private bool SendConnectResult(bool success, ErrorCode errorCode, string message,
        bool disconnectAfterSend = false)
    {
        using Packet packet = CreateConnectResultPacket(success, errorCode, message);
        return disconnectAfterSend ? Connection.TrySendAndDisconnect(packet) : Connection.TrySend(packet);
    }

    /// <summary>
    ///     Creates a complete CONNECT_RESULT frame. Successful entry calls this before the authentication commit,
    ///     keeping allocation, serialization, and body construction on the pre-commit failure side of the boundary.
    /// </summary>
    private Packet CreateConnectResultPacket(bool success, ErrorCode errorCode, string message,
        long matchingId = 0, Cell? spawnCell = null)
    {
        var response = new G_TO_C_CONNECT_RESULT
        {
            Success = success,
            ErrorCode = errorCode,
            Message = message,
            MatchingId = matchingId,
            SpawnCell = spawnCell ?? new Cell(0, 0)
        };
        byte[] body = MessagePackSerializer.Serialize(response);
        Packet packet = Packet.Create((int)Protocol.G_TO_C_CONNECT_RESULT, PlayerId ?? 0);
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

    /// <summary>
    ///     인증 커밋 뒤 매치 잠금 밖에서 미리 만든 성공 응답을 큐에 넣는다. false나 예외는 fail-forward —
    ///     커밋된 입장은 유지하고 이 소켓만 닫는다.
    /// </summary>
    private bool TryPublishCommittedConnectResult(Packet packet)
    {
        try
        {
            if (_trySendConnectSuccessResponse(packet))
                return true;

            Logger.LogWarning(
                "Committed game entry response was not queued; closing connection: PlayerId={PlayerId}, MatchingId={MatchingId}",
                PlayerId,
                MatchingId);
        }
        catch (Exception ex)
        {
            Logger.LogError(
                ex,
                "Committed game entry response enqueue failed; closing connection: PlayerId={PlayerId}, MatchingId={MatchingId}",
                PlayerId,
                MatchingId);
        }

        CloseAfterCommittedEntryResponseFailure();
        return false;
    }

    /// <summary>
    ///     Closes a connection whose entry already committed without reporting an entry failure or attempting
    ///     a contradictory negative CONNECT_RESULT.
    /// </summary>
    private void CloseAfterCommittedEntryResponseFailure()
    {
        MarkServerInitiatedDisconnect();
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
            return;

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
        if (!PlayerId.HasValue || MatchingId <= 0) return Task.CompletedTask;
        return RunWithMatchLock(() =>
        {
            MatchStartGate.MarkHumanReady(MatchingId, PlayerId.Value);
            SendMatchStartCountdown(MatchingId);
            return Task.CompletedTask;
        }, () => { });
    }

    private Task HandleHeartbeat()
    {
        // Send the heartbeat response.
        using var packet = PacketMaker.G_TO_C_HEART_BEAT(DateTime.UtcNow);
        TrySend(packet);

        return Task.CompletedTask;
    }

    /// <summary>
    ///     세션의 TCP 연결을 즉시 종료한다.
    /// </summary>
    public void ForceDisconnect()
    {
        Logger.LogWarning("Force disconnecting PlayerId={PlayerId}", PlayerId);
        Connection.Disconnect();
    }

    protected override bool ShouldSkipLogging(Protocol protocolId)
    {
        return protocolId == Protocol.C_TO_G_HEART_BEAT || protocolId == Protocol.C_TO_G_MOVE;
    }

    internal IReadOnlyList<long> MatchHumanPlayerIds => Volatile.Read(ref _matchHumanPlayerIds);

    private bool TryBeginMatchingLifecycleTerminal()
    {
        return Interlocked.CompareExchange(ref _matchingLifecycleTerminalReported, 1, 0) == 0;
    }

    /// <summary>
    /// 입장 실패 처리를 한 호출만 맡는다. 다른 종료 처리가 선점했다면 중복 처리하지 않는다.
    /// </summary>
    /// <returns>처리가 불필요하거나 다른 호출이 맡았으면 true. 실패 처리 중 예외가 나서 호출자가 직접 연결을 끊어야 하면 false.</returns>
    private bool ReportEntryFailureOnce()
    {
        if (Volatile.Read(ref _entryCompleted) != 0 ||
            !PlayerId.HasValue ||
            MatchingId <= 0)
            return true;
        if (Interlocked.CompareExchange(ref _entryFailureReported, 1, 0) != 0)
            return true;

        if (!TryBeginMatchingLifecycleTerminal())
        {
            // 실제 처리를 맡지 않았으므로 입장 실패 플래그는 돌려놓는다.
            // 다른 종료 처리가 실패해 선점을 풀면 다음 호출에서 다시 시도할 수 있다.
            Volatile.Write(ref _entryFailureReported, 0);
            return true;
        }

        try
        {
            _entryFailureHandler.Handle(this);
            return true;
        }
        catch (Exception ex)
        {
            // 여기까지 왔다면 두 플래그 모두 이 호출이 선점했다.
            Volatile.Write(ref _matchingLifecycleTerminalReported, 0);
            Volatile.Write(ref _entryFailureReported, 0);
            Logger.LogError(
                ex,
                "Failed to report game entry failure: PlayerId={PlayerId}, MatchingId={MatchingId}",
                PlayerId,
                MatchingId);
            return false;
        }
    }

    private void ReleaseMatchingReservationOnce()
    {
        if (!PlayerId.HasValue ||
            MatchingId <= 0 ||
            Interlocked.CompareExchange(ref _matchingReservationReleaseReported, 1, 0) != 0)
            return;
        if (!TryBeginMatchingLifecycleTerminal())
            return;

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
            return;

        MarkServerInitiatedDisconnect();
        try
        {
            using var packet = PacketMaker.G_TO_C_ERROR(ErrorCode.FATAL, "게임 입장 초기화에 실패했습니다");
            Connection.TrySendAndDisconnect(packet);
        }
        catch (Exception ex)
        {
            Logger.LogError(ex, "입장 실패 응답 전송 실패: PlayerId={PlayerId}", PlayerId);
            Connection.Disconnect();
        }
    }

    internal bool TryMarkMatchingLifecycleHandledExternally()
    {
        if (!TryBeginMatchingLifecycleTerminal())
            return false;

        Volatile.Write(ref _matchingLifecycleHandledExternally, 1);
        return true;
    }

    private void RecordLeaveOnce()
    {
        if (!PlayerId.HasValue || MatchingId <= 0 || !TryBeginMatchingLifecycleTerminal())
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
        _ = RunWithMatchLock(() =>
        {
            StopAllPeriodicBuffs();
            return Task.CompletedTask;
        }, StopAllPeriodicBuffs);
        Logger.LogInformation("GameClient removed: PlayerId={PlayerId}", PlayerId);
        _sessionLeaveHandler.Handle(this);
    }

    public override void OnDisconnect()
    {
        // 연결이 끝난 시점의 상태에 맞는 terminal lifecycle 원인을 한 번만 고른다.
        // 입장 실패·서버 주도 종료·탈락 뒤 퇴장·진행 중 이탈을 서로 다른 subject로 구분한다.
        if (Volatile.Read(ref _matchingLifecycleHandledExternally) != 0)
        {
            // An infrastructure-level match abort publishes one deterministic terminal event
            // for the whole roster. Do not race it with a per-socket Released event.
        }
        else if (Volatile.Read(ref _entryCompleted) == 0)
        {
            ReportEntryFailureOnce();
        }
        else if (Volatile.Read(ref _isServerInitiatedDisconnect) || _isServerStopping())
        {
            // Planned shutdown still must not leave the exact matching reservation
            // behind until its long TTL expires.
            ReleaseMatchingReservationOnce();
        }
        else if (PlayerId.HasValue && MatchingId > 0 && !Volatile.Read(ref _isGameEnded))
        {
            if (IsEliminated)
            {
                // Eliminated spectators already paid the gameplay consequence. They still need
                // a released terminal event if they leave before the final result so the
                // UserServer can clear the exact matching reservation and local assignment.
                ReleaseMatchingReservationOnce();
            }
            else
            {
                RecordLeaveOnce();
            }
        }

        Logger.LogInformation("GameClient disconnected: PlayerId={PlayerId}", PlayerId);
    }

    /// <summary>
    ///     서버가 소켓 종료를 시작했음을 표시해 released terminal로 정리한다.
    /// </summary>
    public void MarkServerInitiatedDisconnect()
    {
        Volatile.Write(ref _isServerInitiatedDisconnect, true);
    }

    /// <summary>
    ///     특정 영역의 세션 목록 반환
    /// </summary>
    /// <param name="allSessions">전체 세션 목록</param>
    /// <param name="area">필터링할 영역</param>
    /// <param name="excludeSelf">자신을 제외할지 여부</param>
    private List<GameClientSession> GetSessionsInArea(
        List<GameClientSession> allSessions, AreaType area, bool excludeSelf = true)
    {
        return allSessions
            .Where(s => !s.IsEliminated &&
                        s.CurrentArea == area &&
                        (!excludeSelf || s.PlayerId != PlayerId))
            .ToList();
    }


}
