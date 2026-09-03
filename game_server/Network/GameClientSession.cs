using System.Collections.Concurrent;
using game_server.services;
using MessagePack;
using Microsoft.Extensions.Logging;
using network.common;
using network.common.data;
using network.common.data.models;
using network.contracts.authentication;
using network.core;
using network.interfaces;
using network.packets;

namespace game_server.network;

/// <summary>
///     GameServer 인계 뒤의 TCP 클라이언트 연결 하나. 프로토콜 검증과 연결별 상태를 소유하고, 매치 공유 상태는
///     GameServer 매니저에 남긴다. 권위 상태를 바꾸는 핸들러(채집·픽업·성장·조합)는 <see cref="RunUnderMatch"/>로
///     매치 잠금(<see cref="MatchRuntime.Sync"/>) 안에서 동기 실행되며 응답도 그 안에서 보낸다 — 잠금 안 송신은
///     큐 적재뿐이라 I/O를 기다리지 않는다. 인증은 Redis 입장 커밋과 초기 스냅샷이 끝난 뒤에만 보이고, 성공 응답은
///     잠금 밖에서 큐에 넣는다. 매치 종료 결과·GAME_END는 같은 잠금 안에서 보내고 영속·lifecycle 발행은 잠금이
///     풀린 뒤 후처리로 돈다.
/// </summary>
public partial class GameClientSession : SessionBase
{
    private const int MaxStamina = 100;
    private static int MaxCorruption => Config.MAX_CORRUPTION;
    private const int InitialStamina = MaxStamina;
    private const int InitialCorruption = 0;
    private static readonly TimeSpan ExploreMoveGracePeriod = TimeSpan.FromMilliseconds(750);

    // 하트비트 타임아웃 (초)
    private const int HeartbeatTimeoutSeconds = 30;
    private static readonly ConcurrentDictionary<long, SemaphoreSlim> MatchInitializationLocks = new();
    private readonly List<PeriodicBuffEntry> _activePeriodicBuffs = new();
    private readonly List<int> _activeBuffIds = new();
    private readonly DoorStateManager _doorStateManager;
    private readonly Func<MapId, long, List<GameClientSession>> _getSessionsByInstance;
    private readonly InGameInventoryManager _inGameInventoryManager;
    private readonly InteractableStateManager _interactableStateManager;
    private readonly AreaItemStockManager _areaItemStockManager;
    private readonly GroundItemManager _groundItemManager;
    private readonly Func<string?, Task<GameHandoffContext?>> _consumeGameHandoffTicket;
    private readonly SummonStoneManager _summonStoneManager;
    private readonly Action<GameClientSession> _onLeaveCallback;
    /// <summary>매치별 잠금·수명 색인 (#331) — 핸들러 직렬화·터미널 게이트·종료 정리의 단일 원천.</summary>
    private readonly MatchRuntimeStore _matchRuntimes;
    /// <summary>
    ///     Queues an already-built successful admission response after authentication has committed. Production uses
    ///     <see cref="UserToken.TrySend"/>; tests can inject a sender to verify the match monitor boundary.
    /// </summary>
    private readonly Func<Packet, bool> _trySendConnectSuccessResponse;
    private readonly Func<long, GameClientSession, Action?> _registerSessionCallback;
    private readonly Action<long, long> _publishPlayerLeft;
    private readonly Func<long, long, Action?> _prepareGameCompletion;
    private readonly Action<long, long> _releaseMatchingClaim;
    private readonly Action<GameClientSession> _recordAdmissionFailure;
    private readonly Func<bool> _isServerStopping;
    private readonly MatchRosterManager _matchRosterManager;
    private readonly AreaClosureManager _areaClosureManager;
    private readonly BotPlayerManager _botPlayerManager;
    private readonly GameEventLogManager _gameEventLogManager;
    private readonly MatchSummaryFileStore _matchSummaryFileStore;
    private readonly EncounterRevealManager _encounterRevealManager;
    /// <summary>성장 카드 픽 — 매치 잠금 안에서 부르는 GameServer 인스턴스 위임.</summary>
    private readonly Action<GameClientSession, long, int, int> _handleSwarmGrowthPick;
    /// <summary>6칸 빌드 결정 — 매치 잠금 안에서 부르는 GameServer 인스턴스 위임.</summary>
    private readonly Action<GameClientSession, long, int, long, long> _handleSwarmOrbDecision;
    /// <summary>매치 소유 조합 난수 stream — 매치 잠금 안에서만 조회한다.</summary>
    private readonly Func<long, Random> _getItemCombineRandom;
    private readonly GameAdmissionStateCommitter _admissionStateCommitter;

    private bool _isSleeping;

    // #229 6단계 수면 회복 → 2026-08-17 재조정: 진입 시각(준비 1초)과 마지막 교전 시각
    // (가해·피해 뒤 3초 진입 잠금)을 세션이 들고, 회복 정산(1초 틱)은 아레나 틱이 돈다.
    internal DateTime SwarmSleepStartedAtUtc { get; set; } = DateTime.MinValue;
    internal DateTime SwarmLastCombatAtUtc { get; set; } = DateTime.MinValue;
    // 단일 절단 치명상 (#232): 성공 뒤 8초는 수면 진입·회복 틱이 막힌다.
    internal DateTime SwarmHealLockUntilUtc { get; set; } = DateTime.MinValue;
    internal bool IsSleeping => _isSleeping;
    private int _swarmSleepGrantedTicks;
    private DateTime _lastHeartbeatTime = DateTime.UtcNow;
    private int _admissionCompleted;
    private int _admissionFailureReported;
    private int _admissionDisconnectIssued;
    private int _matchingLifecycleHandledExternally;
    private int _matchingLifecycleTerminalReported;
    private int _matchingClaimReleaseReported;
    private long[] _handoffHumanPlayerIds = [];

    // #229: 진행 중인 문 잠금해제 게이지. 맞으면 서버가 지워 뒤늦은 FINISH까지 무효로 만든다.
    private int? _pendingDoorUnlockInteractId;

    /// <summary>START 처리됐으나 FINISH 대기 중인 InteractId — RngCollect 파셜이 사용.
    /// FINISH 도착 시 이 set에 있어야 결과 산출 진행.</summary>
    private readonly HashSet<int> _pendingFinish = new();

    // 이 매치에서 연 문 수 — 첫 문은 피격으로 게이지가 끊기지 않는다 (2026-08-16).
    private int _swarmDoorUnlockCount;
    private int _pendingOrbDraftCost = 0;

    private DateTime _exploreMoveGraceUntil = DateTime.MinValue;

    private Vector3f? _lastValidatedPosition;
    private Cell? _lastValidCell;

    private float _lastValidatedRotation;
    // 오브 궤도 위상 (#232): 검증 이동 거리로 적산한 권위값 — 시드는 PlayerId. null = 아직 시드 전.
    private float? _orbOrbitPhaseDegrees;
    // Stopwatch ticks: client timestamps are telemetry only and never extend movement authority.
    private long _lastMoveReceiptTimestamp;
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

    private void SetActiveBuffIds(IEnumerable<int>? activeBuffIds)
    {
        _activeBuffIds.Clear();
        if (activeBuffIds == null) return;

        foreach (int buffId in activeBuffIds)
        {
            if (buffId > 0 && !_activeBuffIds.Contains(buffId))
                _activeBuffIds.Add(buffId);
        }
    }

    internal GameClientSession(
        UserToken token,
        IRedLockFactory redLock,
        ILogger logger,
        ICacheHelper cacheHelper,
        Func<string?, Task<GameHandoffContext?>> consumeGameHandoffTicket,
        Action<GameClientSession> onLeaveCallback,
        Func<long, GameClientSession, Action?> registerSessionCallback,
        Func<MapId, long, List<GameClientSession>> getSessionsByInstance,
        InteractableStateManager interactableStateManager,
        InGameInventoryManager inGameInventoryManager,
        AreaItemStockManager areaItemStockManager,
        GroundItemManager groundItemManager,
        SummonStoneManager summonStoneManager,
        DoorStateManager doorStateManager,
        MatchRosterManager matchRosterManager,
        AreaClosureManager areaClosureManager,
        BotPlayerManager botPlayerManager,
        GameEventLogManager gameEventLogManager,
        MatchSummaryFileStore matchSummaryFileStore,
        EncounterRevealManager encounterRevealManager,
        MatchRuntimeStore matchRuntimes,
        Action<GameClientSession, long, int, int> handleSwarmGrowthPick,
        Action<GameClientSession, long, int, long, long> handleSwarmOrbDecision,
        Func<long, Random> getItemCombineRandom,
        Action<long, long> publishPlayerLeft,
        Func<long, long, Action?> prepareGameCompletion,
        Action<long, long> releaseMatchingClaim,
        Func<bool> isServerStopping,
        Action<GameClientSession> recordAdmissionFailure,
        Func<Packet, bool>? trySendConnectSuccessResponse = null)
        : base(token, logger, cacheHelper, redLock)
    {
        _onLeaveCallback = onLeaveCallback;
        _consumeGameHandoffTicket = consumeGameHandoffTicket;
        _registerSessionCallback = registerSessionCallback;
        _getSessionsByInstance = getSessionsByInstance;
        _interactableStateManager = interactableStateManager;
        _inGameInventoryManager = inGameInventoryManager;
        _areaItemStockManager = areaItemStockManager;
        _groundItemManager = groundItemManager;
        _summonStoneManager = summonStoneManager;
        _doorStateManager = doorStateManager;
        _matchRosterManager = matchRosterManager;
        _areaClosureManager = areaClosureManager;
        _botPlayerManager = botPlayerManager;
        _gameEventLogManager = gameEventLogManager;
        _matchSummaryFileStore = matchSummaryFileStore;
        _encounterRevealManager = encounterRevealManager;
        _matchRuntimes = matchRuntimes;
        _handleSwarmGrowthPick = handleSwarmGrowthPick;
        _handleSwarmOrbDecision = handleSwarmOrbDecision;
        _getItemCombineRandom = getItemCombineRandom;
        _admissionStateCommitter = new GameAdmissionStateCommitter(cacheHelper, logger);
        _trySendConnectSuccessResponse = trySendConnectSuccessResponse ?? Token.TrySend;
        _publishPlayerLeft = publishPlayerLeft;
        _prepareGameCompletion = prepareGameCompletion;
        _releaseMatchingClaim = releaseMatchingClaim;
        _isServerStopping = isServerStopping;
        _recordAdmissionFailure = recordAdmissionFailure;

        // ReSharper disable once VirtualMemberCallInConstructor
        InitializeProtocolHandlers();
        Logger.LogInformation("GameClientSession created");
    }

    /// <summary>터미널 매치의 늦은 패킷은 잠금 없이 거른다 — 매치 밖 세션(로비 전)은 그대로 통과.</summary>
    protected override bool IsMessageLifecycleActive()
    {
        long matchingId = CurrentMapSubId;
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

        MatchRuntime? runtime = _matchRuntimes.Get(CurrentMapSubId);
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

    internal static void CleanupAbandonedMatchingRuntime(long matchingId)
    {
        MatchStartGate.RemoveMatching(matchingId);
        RngCollectCooldownStore.ClearMatching(matchingId);
        if (MatchInitializationLocks.TryRemove(matchingId, out var initializationLock))
            initializationLock.Dispose();
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

        if (CurrentMapSubId > 0 && !MatchStartGate.IsGameplayActive(CurrentMapSubId))
        {
            reason = "Waiting for match start";
            return true;
        }

        return false; // 라운드 시스템 퇴역(#246)
    }

    public new long? PlayerId { get; private set; }
    public MapId CurrentMapId { get; private set; }
    public long CurrentMapSubId { get; private set; }
    public AreaType CurrentArea { get; private set; } = AreaType.None;
    // 이동 잠금 판정용 — IDLE/EXPLORE_1 두 값만 저장한다 (SLEEP 등은 _isSleeping이 별도 추적).
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

    // 미니맵 타깃 마커 대상 (TargetLocation 송신이 사용)
    public long TargetPlayerId { get; private set; }
    public PlayerMatchStatus PlayerMatchStatus { get; private set; } = PlayerMatchStatus.ACTIVE;

    // 이탈 페널티 면제 플래그
    /// <summary>게임 결과 화면 이후 퇴장: 페널티 면제</summary>
    private bool _isGameEnded;
    /// <summary>서버 셧다운/크래시로 인한 종료: 페널티 면제</summary>
    private bool _isServerInitiatedDisconnect;

    /// <summary>
    ///     탈락/관전 상태에서 행동 가능한지 체크
    /// </summary>
    internal bool IsGameEnded => Volatile.Read(ref _isGameEnded);
    internal int CurrentCorruption => Corruption;
    public bool IsEliminated => PlayerMatchStatus == PlayerMatchStatus.ELIMINATED || PlayerMatchStatus == PlayerMatchStatus.SPECTATING;

    // 인게임 스탯 (게임 종료 시 초기화)
    private int Stamina { get; set; } = InitialStamina;
    private int Corruption { get; set; } = InitialCorruption;

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

        var allSessions = _getSessionsByInstance(CurrentMapId, CurrentMapSubId);
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
            session.Send(packet);
        }

        return Task.CompletedTask;
    }

    protected override bool ShouldSkipLogging(Protocol protocolId)
    {
        return protocolId == Protocol.C_TO_G_HEART_BEAT || protocolId == Protocol.C_TO_G_MOVE;
    }

    internal IReadOnlyList<long> HandoffHumanPlayerIds => Volatile.Read(ref _handoffHumanPlayerIds);

    private bool TryBeginMatchingLifecycleTerminal()
    {
        return Interlocked.CompareExchange(ref _matchingLifecycleTerminalReported, 1, 0) == 0;
    }

    /// <summary>
    ///     Hands a registered, pre-authentication session to the owning server's deferred
    ///     admission-abort path. A false result means invoking that hook failed; callers may
    ///     close the socket without sending a competing local terminal response.
    /// </summary>
    private bool ReportAdmissionFailureOnce()
    {
        bool admissionFailureClaimed = false;
        bool matchingLifecycleTerminalClaimed = false;
        if (Volatile.Read(ref _admissionCompleted) != 0 ||
            !PlayerId.HasValue ||
            CurrentMapSubId <= 0)
            return true;
        if (Interlocked.CompareExchange(ref _admissionFailureReported, 1, 0) != 0)
            return true;

        admissionFailureClaimed = true;
        if (!TryBeginMatchingLifecycleTerminal())
            return true;

        matchingLifecycleTerminalClaimed = true;

        try
        {
            _recordAdmissionFailure(this);
            return true;
        }
        catch (Exception ex)
        {
            // Only release reservations this invocation acquired. A competing terminal path
            // can already own either marker when this method returns early above.
            if (matchingLifecycleTerminalClaimed)
                Volatile.Write(ref _matchingLifecycleTerminalReported, 0);
            if (admissionFailureClaimed)
                Volatile.Write(ref _admissionFailureReported, 0);
            Logger.LogError(
                ex,
                "Failed to report game admission failure: PlayerId={PlayerId}, MatchingId={MatchingId}",
                PlayerId,
                CurrentMapSubId);
            return false;
        }
    }

    private void ReleaseMatchingClaimOnce()
    {
        if (!PlayerId.HasValue ||
            CurrentMapSubId <= 0 ||
            Interlocked.CompareExchange(ref _matchingClaimReleaseReported, 1, 0) != 0)
            return;
        if (!TryBeginMatchingLifecycleTerminal())
            return;

        try
        {
            _releaseMatchingClaim(PlayerId.Value, CurrentMapSubId);
        }
        catch
        {
            Volatile.Write(ref _matchingLifecycleTerminalReported, 0);
            Volatile.Write(ref _matchingClaimReleaseReported, 0);
            throw;
        }
    }

    internal virtual void DisconnectForAdmissionFailure()
    {
        if (Interlocked.Exchange(ref _admissionDisconnectIssued, 1) != 0)
            return;

        MarkServerInitiatedDisconnect();
        try
        {
            using var packet = PacketMaker.G_TO_C_ERROR(ErrorCode.FATAL, "게임 입장 초기화에 실패했습니다");
            Token.TrySendAndDisconnect(packet);
        }
        catch (Exception ex)
        {
            Logger.LogError(ex, "입장 실패 응답 전송 실패: PlayerId={PlayerId}", PlayerId);
            Token.Disconnect();
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
        if (!PlayerId.HasValue || CurrentMapSubId <= 0 || !TryBeginMatchingLifecycleTerminal())
            return;

        try
        {
            _publishPlayerLeft(PlayerId.Value, CurrentMapSubId);
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
            Send(packet);
        }
        catch (Exception ex)
        {
            Logger.LogError(ex, "에러 응답 전송 실패: PlayerId={PlayerId}", PlayerId);
        }
    }

    public override void OnRemoved()
    {
        StopAllPeriodicBuffs();
        Logger.LogInformation("GameClient removed: PlayerId={PlayerId}", PlayerId);
        _onLeaveCallback(this);
    }

    public override void OnDisconnect()
    {
        // 게임 진행 중 의도적 이탈 시 페널티 기록
        // 면제: SPECTATING/ELIMINATED, 게임 결과 화면 이후, 서버 주도 종료
        if (Volatile.Read(ref _matchingLifecycleHandledExternally) != 0)
        {
            // An infrastructure-level match abort publishes one deterministic terminal event
            // for the whole roster. Do not race it with a per-socket Released event.
        }
        else if (Volatile.Read(ref _admissionCompleted) == 0)
        {
            ReportAdmissionFailureOnce();
        }
        else if (Volatile.Read(ref _isServerInitiatedDisconnect) || _isServerStopping())
        {
            // Planned shutdown is penalty-free, but it must not leave the exact matching claim
            // behind until its long TTL expires.
            ReleaseMatchingClaimOnce();
        }
        else if (PlayerId.HasValue && CurrentMapSubId > 0 && !Volatile.Read(ref _isGameEnded))
        {
            if (IsEliminated)
            {
                // Eliminated spectators already paid the gameplay consequence. They still need
                // a penalty-free terminal event if they leave before the final result so the
                // UserServer can clear the exact matching claim and local assignment.
                ReleaseMatchingClaimOnce();
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
    ///     이후 퇴장은 페널티를 면제하고 정상 완료 보상으로 이탈 횟수 1을 감소시킨다.
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
        if (!PlayerId.HasValue || CurrentMapSubId <= 0 || !TryBeginMatchingLifecycleTerminal())
            return null;

        try
        {
            return _prepareGameCompletion(PlayerId.Value, CurrentMapSubId);
        }
        catch
        {
            Volatile.Write(ref _matchingLifecycleTerminalReported, 0);
            throw;
        }
    }

    /// <summary>
    ///     서버 셧다운/크래시 시 호출. 비자발적 이탈로 간주하여 페널티 면제.
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

    private class PeriodicBuffEntry
    {
        public float ElapsedSeconds;
        public int DurationSeconds;
        public int IntervalSeconds;
        public int RemainingSeconds;
        public BuffSubType SubType;
        public int Value;
    }
}
