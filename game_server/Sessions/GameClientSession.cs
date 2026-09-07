using System.Collections.Concurrent;
using game_server.network;
using game_server.services;
using MessagePack;
using Microsoft.Extensions.Logging;
using network.common;
using network.common.data;
using network.common.data.models;
using network.core;
using network.gamehandoff;
using network.infrastructure.redis;
using network.packets;

namespace game_server.sessions;

/// <summary>
///     GameServer 인계 뒤의 TCP 클라이언트 연결 하나. 프로토콜 검증과 연결별 상태를 소유하고, 매치 공유 상태는
///     GameServer 매니저에 남긴다. 권위 상태를 바꾸는 핸들러(채집·픽업·성장·조합)는 <see cref="RunUnderMatch"/>로
///     매치 잠금(<see cref="MatchRuntime.Sync"/>) 안에서 동기 실행되며 응답도 그 안에서 보낸다 — 잠금 안 송신은
///     큐 적재뿐이라 I/O를 기다리지 않는다. 인증은 Redis 입장 커밋과 초기 스냅샷이 끝난 뒤에만 보이고, 성공 응답은
///     잠금 밖에서 큐에 넣는다. 매치 종료 결과·GAME_END는 같은 잠금 안에서 보내고 영속·lifecycle 발행은 잠금이
///     풀린 뒤 후처리로 돈다.
///     퇴장은 GameSessionLeaveHandler에, 입장 실패는 IMatchEntryFailureHandler에,
///     퇴장·완료 알림과 예약 해제는 IGameSessionLifecycle에 요청한다.
/// </summary>
public partial class GameClientSession : SessionBase
{
    private const int MaxStamina = 100;
    private static int MaxCorruption => Config.MAX_CORRUPTION;
    private const int InitialStamina = MaxStamina;
    private const int InitialCorruption = 0;
    private static readonly TimeSpan ExploreMoveGracePeriod = TimeSpan.FromMilliseconds(750);


    private readonly PlayerConditionState _condition = new();
    private readonly List<int> _activeBuffIds = new();
    private readonly Func<MapId, long, List<GameClientSession>> _getSessionsByInstance;

    private readonly Func<string?, Task<GameHandoffContext?>> _consumeGameHandoffTicket;
    private readonly GameSessionLeaveHandler _sessionLeaveHandler;
    /// <summary>매치별 잠금·수명 색인 (#331) — 핸들러 직렬화·터미널 게이트·종료 정리의 단일 원천.</summary>
    private readonly MatchRuntimeStore _matchRuntimes;
    private readonly GameServerDevOptions _devOptions;
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
    /// <summary>성장 카드 픽 — 매치 잠금 안에서 부르는 GameServer 인스턴스 위임.</summary>
    private readonly Action<GameClientSession, long, int, int> _handleSwarmGrowthPick;
    /// <summary>6칸 빌드 결정 — 매치 잠금 안에서 부르는 GameServer 인스턴스 위임.</summary>
    private readonly Action<GameClientSession, long, int, long, long> _handleSwarmOrbDecision;

    private readonly GameMatchEntryService _matchEntry;
    private readonly ItemCombinationService _itemCombinations;
    private readonly MovementValidationService _movementValidation;



    // #229 6단계 수면 회복 → 2026-08-17 재조정: 진입 시각(준비 1초)과 마지막 교전 시각
    // (가해·피해 뒤 3초 진입 잠금)을 세션이 들고, 회복 정산(1초 틱)은 아레나 틱이 돈다.
    internal DateTime SwarmSleepStartedAtUtc { get => _condition.SleepStartedAtUtc; set => _condition.SleepStartedAtUtc = value; }
    internal DateTime SwarmLastCombatAtUtc { get => _condition.LastCombatAtUtc; set => _condition.LastCombatAtUtc = value; }
    // 단일 절단 치명상 (#232): 성공 뒤 8초는 수면 진입·회복 틱이 막힌다.
    internal DateTime SwarmHealLockUntilUtc { get => _condition.HealLockUntilUtc; set => _condition.HealLockUntilUtc = value; }
    internal bool IsSleeping => _condition.IsSleeping;

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

    private DateTime _exploreMoveGraceUntil = DateTime.MinValue;

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

    /// <summary>잼 승점 지갑 (#222 M3) — 매치 단위, 소환석과 분리된 재화.</summary>
    public int JamCount { get; private set; }

    private Timer? _periodicBuffTimer;

    /// <summary>절단 실험 더미 조종 훅 (#226 실험장, 개발용) — (matchingId, dirX, dirY).</summary>
    internal static Action<long, float, float>? SwarmDummyMoveCallback { get; set; }

    /// <summary>
    ///     하트 픽업 시 앞줄 오브 HP 회복 훅 (#222 M4) — 원작 하트는 스쿼드 유닛도 회복한다.
    ///     GameServer가 스웜 매치 초기화 시 배선한다 (사람·봇 픽업 공통).
    /// </summary>
    internal static Action<long, long>? SwarmHeartPickupCallback { get; set; }

    public IReadOnlyCollection<int> ActiveBuffIds => _activeBuffIds;

    private MatchDoorState? Doors => _matchRuntimes.Get(MatchingId)?.Doors;

    internal GameClientSession(
        TcpConnection connection,
        ILogger logger,
        IRedisOperations redisOperations,
        Func<string?, Task<GameHandoffContext?>> consumeGameHandoffTicket,
        GameSessionLeaveHandler sessionLeaveHandler,
        Func<long, GameClientSession, GameClientSession?> registerSessionCallback,
        Func<MapId, long, List<GameClientSession>> getSessionsByInstance,

        GameEventLogManager gameEventLogManager,
        MatchEliminationService matchEliminations,
        MatchRuntimeStore matchRuntimes,
        Action<GameClientSession, long, int, int> handleSwarmGrowthPick,
        Action<GameClientSession, long, int, long, long> handleSwarmOrbDecision,

        IGameSessionLifecycle matchingLifecycle,
        Func<bool> isServerStopping,
        IMatchEntryFailureHandler entryFailureHandler,
        GameServerDevOptions devOptions,
        GameMatchEntryService matchEntry,
        ItemCombinationService itemCombinations,
        MovementValidationService movementValidation,
        Func<Packet, bool>? trySendConnectSuccessResponse = null,
        TimeProvider? movementTimeProvider = null)
        : base(connection, logger, redisOperations)
    {
        _sessionLeaveHandler = sessionLeaveHandler;
        _consumeGameHandoffTicket = consumeGameHandoffTicket;
        _registerSessionCallback = registerSessionCallback;
        _getSessionsByInstance = getSessionsByInstance;

        _gameEventLogManager = gameEventLogManager;
        _matchEliminations = matchEliminations;
        _matchRuntimes = matchRuntimes;
        _devOptions = devOptions;
        _movementPacketQueue = new MovementPacketQueue(() => Connection.IsAcceptingMessages, movementTimeProvider);
        _handleSwarmGrowthPick = handleSwarmGrowthPick;
        _handleSwarmOrbDecision = handleSwarmOrbDecision;

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

    /// <summary>터미널 매치의 늦은 패킷은 잠금 없이 거른다 — 매치 밖 세션(로비 전)은 그대로 통과.</summary>
    protected override bool IsMessageLifecycleActive()
    {
        long matchingId = MatchingId;
        return matchingId <= 0 || _matchRuntimes.Get(matchingId) is { IsTerminal: false };
    }

    /// <summary>
    ///     권위 상태를 바꾸는 핸들러 core를 매치 잠금 안에서 동기로 돌린다 — 50ms 전투 틱과 같은 잠금이라
    ///     이 안의 Send 순서가 곧 상태 변경 순서다. 런타임이 없거나 터미널이면 core 대신 거부 응답만 보낸다.
    ///     core가 던지면 잠금을 풀고 SessionBase가 G_TO_C_ERROR를 보낸다(앞서 보낸 패킷은 그대로).
    /// </summary>
    private Task RunUnderMatch(Func<Task> core, Action rejectIfTerminal)
    {
        ArgumentNullException.ThrowIfNull(core);
        ArgumentNullException.ThrowIfNull(rejectIfTerminal);

        MatchRuntime? runtime = _matchRuntimes.Get(MatchingId);
        if (runtime == null)
        {
            rejectIfTerminal();
            return Task.CompletedTask;
        }

        using MatchScope scope = _matchRuntimes.Enter(runtime);
        if (runtime.IsTerminal)
        {
            rejectIfTerminal();
            return Task.CompletedTask;
        }

        Task prepared = core() ??
            throw new InvalidOperationException("Match handler core returned a null task.");
        if (!prepared.IsCompleted)
        {
            throw new InvalidOperationException(
                "Match handler core must complete synchronously under the match lock.");
        }

        prepared.GetAwaiter().GetResult();
        return Task.CompletedTask;
    }

    private bool IsRoundActionLocked(out string reason)
    {
        reason = string.Empty;

        if (IsEliminated)
        {
            reason = "Eliminated players cannot act";
            return true;
        }

        if (Volatile.Read(ref _isGameEnded))
        {
            reason = "Game has already ended";
            return true;
        }

        if (MatchingId > 0 && !MatchStartGate.IsGameplayActive(MatchingId))
        {
            reason = "Waiting for match start";
            return true;
        }

        return false; // 라운드 시스템 퇴역(#246)
    }

    public new long? PlayerId { get; private set; }
    public MapId CurrentMapId { get; private set; }
    public long MatchingId { get; private set; }
    public AreaType CurrentArea { get; private set; } = AreaType.None;
    // 이동 잠금 판정용 — IDLE/EXPLORE_1 두 값만 저장한다 (SLEEP 등은 _condition.IsSleeping이 별도 추적).
    private PlayerState CurrentState { get; set; } = PlayerState.IDLE;

    /// <summary>마지막 검증된 월드 좌표 — 근접 전투·드랍 위치 등 거리 판정용.</summary>
    public Vector3f? LastValidatedPosition => _lastValidatedPosition;

    /// <summary>
    ///     오브 궤도 위상 (#232): 이동할 때 돌고 멈추면 선다 — 검증 이동 거리를 적산한다.
    ///     서버 전투가 오브별 자리(SwarmOrbOrbit)를 계산하는 근거이자, G_TO_C_MOVE로 클라에 보내는 보정값.
    /// </summary>
    public float OrbOrbitPhaseDegrees =>
        _orbOrbitPhaseDegrees ?? SwarmOrbOrbit.InitialPhaseDegrees(PlayerId ?? 0L);

    /// <summary>검증된 이동만큼 궤도를 돌린다 (텔레포트급 점프는 SwarmOrbOrbit이 무시한다).</summary>
    private void AdvanceOrbOrbit(Vector3f from, Vector3f to)
    {
        float dx = to.X - from.X;
        float dy = to.Y - from.Y;
        _orbOrbitPhaseDegrees = SwarmOrbOrbit.AdvancePhase(
            OrbOrbitPhaseDegrees, MathF.Sqrt(dx * dx + dy * dy));
    }

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
    internal int CurrentCorruption => Corruption;
    public bool IsEliminated => PlayerMatchStatus == PlayerMatchStatus.ELIMINATED || PlayerMatchStatus == PlayerMatchStatus.SPECTATING;

    // 인게임 스탯 (게임 종료 시 초기화)
    private int Stamina { get => _condition.Stamina; set => _condition.Stamina = value; }
    private int Corruption { get => _condition.Corruption; set => _condition.Corruption = value; }

    protected override void InitializeProtocolHandlers()
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

    /// <summary>
    ///     본인이 SOCIAL 액션 요청 → 같은 area 모든 클라(본인 포함)에 G_TO_C_SOCIAL_ACTION broadcast.
    /// </summary>
    private Task HandleSocialAction(C_TO_G_SOCIAL_ACTION msg)
    {
        if (!PlayerId.HasValue) return Task.CompletedTask;
        if (IsRoundActionLocked(out _))
            return Task.CompletedTask;

        var allSessions = _getSessionsByInstance(CurrentMapId, MatchingId);
        var sameAreaSessions = GetSessionsInArea(allSessions, CurrentArea, excludeSelf: false);

        var broadcast = new G_TO_C_SOCIAL_ACTION
        {
            PlayerId = PlayerId.Value,
            SocialActionType = msg.SocialActionType
        };

        var bodyBytes = MessagePackSerializer.Serialize(broadcast);
        foreach (var session in sameAreaSessions)
        {
            using var packet = Packet.Create((int)Protocol.G_TO_C_SOCIAL_ACTION, session.PlayerId ?? 0);
            packet.SetBody(bodyBytes);
            session.TrySend(packet);
        }

        return Task.CompletedTask;
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
    ///     Hands a registered, pre-authentication session to the owning server's deferred
    ///     entry-abort path. A false result means invoking that hook failed; callers may
    ///     close the socket without sending a competing local terminal response.
    /// </summary>
    private bool ReportEntryFailureOnce()
    {
        bool entryFailureClaimed = false;
        bool matchingLifecycleTerminalClaimed = false;
        if (Volatile.Read(ref _entryCompleted) != 0 ||
            !PlayerId.HasValue ||
            MatchingId <= 0)
            return true;
        if (Interlocked.CompareExchange(ref _entryFailureReported, 1, 0) != 0)
            return true;

        entryFailureClaimed = true;
        if (!TryBeginMatchingLifecycleTerminal())
            return true;

        matchingLifecycleTerminalClaimed = true;

        try
        {
            _entryFailureHandler.Handle(this);
            return true;
        }
        catch (Exception ex)
        {
            // Only release reservations this invocation acquired. A competing terminal path
            // can already own either marker when this method returns early above.
            if (matchingLifecycleTerminalClaimed)
                Volatile.Write(ref _matchingLifecycleTerminalReported, 0);
            if (entryFailureClaimed)
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
        _ = RunUnderMatch(() =>
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
    ///     `GAME_RESULT`와 `GAME_END` 시도 뒤 마지막 best-effort mark 단계에서 호출한다.
    ///     completed terminal을 선점해 이후 소켓 종료가 다른 종료 원인으로 덮어쓰지 못하게 한다.
    ///     이 public wrapper는 준비된 lifecycle Action을 즉시 dispatch한다.
    /// </summary>
    public void MarkGameEnded()
    {
        Action? dispatch = MarkGameEndedAndPrepareLifecyclePublication();
        dispatch?.Invoke();
    }

    /// <summary>
    ///     세션을 터미널로 표시하고 lifecycle Action을 준비만 한다(발행 안 함). 매치 종료 경로가
    ///     잠금 안에서 부르고, 발행은 잠금이 풀린 뒤 후처리에서 한다.
    /// </summary>
    internal Action? MarkGameEndedAndPrepareLifecyclePublication()
    {
        Volatile.Write(ref _isGameEnded, true);
        if (!PlayerId.HasValue || MatchingId <= 0 || !TryBeginMatchingLifecycleTerminal())
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
