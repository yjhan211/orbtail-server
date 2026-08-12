using System.Collections.Concurrent;
using game_server.services;
using MessagePack;
using Microsoft.Extensions.Logging;
using network.common;
using network.common.data.models;
using network.core;
using network.helpers;
using network.interfaces;
using network.packets;

namespace game_server.network;

public enum PlayerState
{
    Idle, // 일반 상태 (이동 가능)
    Exploring // 탐색 중 (이동 불가)
}

public partial class GameClientSession : SessionBase
{
    private const int MaxStamina = 100;
    private const int MaxCorruption = Config.SURVIVOR_MAX_CORRUPTION;
    private const int InitialStamina = MaxStamina;
    private const int InitialCorruption = 0;
    private static readonly TimeSpan ExploreMoveGracePeriod = TimeSpan.FromMilliseconds(750);

    // 하트비트 타임아웃 (초)
    private const int HeartbeatTimeoutSeconds = 30;
    private static readonly TimeSpan InteractCooldown = TimeSpan.FromSeconds(5);
    private static readonly ConcurrentDictionary<long, Timer> GameTimers = new();
    private static readonly object _roundSessionStartLock = new();
    internal static readonly ConcurrentDictionary<long, RoundRuntimeState> GameRoundStates = new();
    private static Proto0PresenceTracker? _presenceTracker;
    private static SurvivorPhaseManager? _survivorPhaseManager;
    private readonly List<PeriodicBuffEntry> _activePeriodicBuffs = new();
    private readonly List<int> _activeBuffIds = new();
    private readonly AreaRuleManager _areaRuleManager;

    // 플레이어가 발견한 행동 수칙 (ruleId → 최초 발견자 PlayerId)
    private readonly Dictionary<int, long> _discoveredRules = new();
    private readonly DoorStateManager _doorStateManager;
    private readonly Func<MapId, long, List<GameClientSession>> _getSessionsByInstance;
    private readonly InGameInventoryManager _inGameInventoryManager;
    private readonly InteractableStateManager _interactableStateManager;
    private readonly ItemPoolManager _itemPoolManager;
    private readonly AreaItemStockManager _areaItemStockManager;
    private readonly GroundItemManager _groundItemManager;
    private readonly EmotionAfterimageMonsterManager _emotionAfterimageMonsterManager;
    private readonly SummonStoneManager _summonStoneManager;
    private readonly Action<GameClientSession> _onLeaveCallback;
    private readonly Action<long, GameClientSession> _registerSessionCallback;
    private readonly Action<long> _recordLeavePenalty;
    private readonly Action<long> _recordGameCompletion;
    private readonly SabotageManager _sabotageManager;
    private readonly ManittoChainManager _manittoChainManager;
    private readonly MissionManager _missionManager;
    private readonly ChecklistManager _checklistManager;
    private readonly AreaClosureManager _areaClosureManager;
    private readonly TraceManager _traceManager;
    private readonly InteractionChoiceService _interactionChoiceService;
    private readonly BotPlayerManager _botPlayerManager;
    private readonly GameEventLogManager _gameEventLogManager;
    private readonly MatchSummaryFileStore _matchSummaryFileStore;
    private readonly EncounterRevealManager _encounterRevealManager;

    // 이미 공유한 수칙 추적 (ruleId, targetPlayerId) — 동일 대상에 중복 공유 방지
    private readonly HashSet<(int RuleId, long TargetPlayerId)> _sharedRules = new();
    // 활성 대화 상대 PlayerId (수락 후 대화 중)
    private long? _activeConversationPlayerId;

    // 마니또 상호작용 선택지 상태
    private List<InteractionQuestion>? _pendingQuestions;     // 질문자의 선택지
    private List<InteractionQuestionContext>? _pendingQuestionContexts;
    private List<InteractionAnswer>? _pendingAnswers;         // 답변자의 선택지
    private List<InteractionAnswerContext>? _pendingAnswerContexts;
    private InteractionQuestionType _lastAskedQuestion;       // 마지막 질문 유형
    private AreaType _previousArea = AreaType.None;           // 이전 구역 (동선추궁용)
    private CancellationTokenSource? _interactTimeoutCts;
    private CancellationTokenSource? _botInteractTimeoutCts;
    private bool _isSleeping;
    private DateTime _lastHeartbeatTime = DateTime.UtcNow;
    private DateTime _lastInteractRejectTime = DateTime.MinValue;
    private DateTime _lastMoveTime = DateTime.UtcNow;

    private DateTime _exploreMoveGraceUntil = DateTime.MinValue;

    private Vector3f? _lastValidatedPosition;
    private Cell? _lastValidCell;

    private float _lastValidatedRotation;
    // Stopwatch ticks: client timestamps are telemetry only and never extend movement authority.
    private long _lastMoveReceiptTimestamp;
    private long _lastMoveAcknowledgementTimestamp;
    private bool _hasProcessedMoveInputSequence;
    private uint _lastProcessedMoveInputSequence;

    // 플레이어 상호작용 요청 상태
    private long? _pendingInteractPlayerId;
    private long? _pendingBotRequesterPlayerId;
    private int _pendingRoomEntryEventId;
    private readonly object _roomEntryEventChoiceLock = new();

    // #219 M2 3택 드래프트: 개봉이 연 드래프트 권리와 개봉 시점 확정 비용
    private bool _hasPendingOrbDraft;
    private int _pendingOrbDraftCost;

    /// <summary>열쇠 (#222 M4): 무료 소환 충전 수 — 획득/소비는 GroundItem·OrbSummon partial.</summary>
    public int FreeSummonCharges { get; internal set; }

    /// <summary>잼 승점 지갑 (#222 M3) — 매치 단위, 소환석과 분리된 재화.</summary>
    public int JamCount { get; private set; }

    private Timer? _periodicBuffTimer;

    /// <summary>
    ///     #217 스웜: 채집(개봉) 시작을 스웜 매니저에 알리는 훅. GameServer가 스웜 매치
    ///     초기화 시 배선한다 — 세션이 매니저를 직접 참조하지 않기 위한 최소 연결.
    /// </summary>
    internal static Action<long, long>? SwarmExploreNoiseCallback { get; set; }

    /// <summary>절단 실험 더미 조종 훅 (#226 실험장, 개발용) — (matchingId, dirX, dirY).</summary>
    internal static Action<long, float, float>? SwarmDummyMoveCallback { get; set; }

    /// <summary>성장 카드 선택 훅 (#226 단계 C) — (session, matchingId, offerId, cardIndex).</summary>
    internal static Action<GameClientSession, long, int, int>? SwarmGrowthPickCallback { get; set; }

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

    private bool AddActiveBuffId(int buffId)
    {
        if (buffId <= 0 || _activeBuffIds.Contains(buffId)) return false;

        _activeBuffIds.Add(buffId);
        return true;
    }

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
        ItemPoolManager itemPoolManager,
        AreaItemStockManager areaItemStockManager,
        GroundItemManager groundItemManager,
        EmotionAfterimageMonsterManager emotionAfterimageMonsterManager,
        SummonStoneManager summonStoneManager,
        DoorStateManager doorStateManager,
        SabotageManager sabotageManager,
        ManittoChainManager manittoChainManager,
        MissionManager missionManager,
        ChecklistManager checklistManager,
        AreaClosureManager areaClosureManager,
        TraceManager traceManager,
        InteractionChoiceService interactionChoiceService,
        BotPlayerManager botPlayerManager,
        GameEventLogManager gameEventLogManager,
        MatchSummaryFileStore matchSummaryFileStore,
        EncounterRevealManager encounterRevealManager,
        Action<long> recordLeavePenalty,
        Action<long> recordGameCompletion)
        : base(token, logger, cacheHelper, redLock)
    {
        _onLeaveCallback = onLeaveCallback;
        _registerSessionCallback = registerSessionCallback;
        _getSessionsByInstance = getSessionsByInstance;
        _interactableStateManager = interactableStateManager;
        _inGameInventoryManager = inGameInventoryManager;
        _areaRuleManager = areaRuleManager;
        _itemPoolManager = itemPoolManager;
        _areaItemStockManager = areaItemStockManager;
        _groundItemManager = groundItemManager;
        _emotionAfterimageMonsterManager = emotionAfterimageMonsterManager;
        _summonStoneManager = summonStoneManager;
        _doorStateManager = doorStateManager;
        _sabotageManager = sabotageManager;
        _manittoChainManager = manittoChainManager;
        _missionManager = missionManager;
        _checklistManager = checklistManager;
        _areaClosureManager = areaClosureManager;
        _traceManager = traceManager;
        _interactionChoiceService = interactionChoiceService;
        _botPlayerManager = botPlayerManager;
        _gameEventLogManager = gameEventLogManager;
        _matchSummaryFileStore = matchSummaryFileStore;
        _encounterRevealManager = encounterRevealManager;
        _recordLeavePenalty = recordLeavePenalty;
        _recordGameCompletion = recordGameCompletion;

        // ReSharper disable once VirtualMemberCallInConstructor
        InitializeProtocolHandlers();
        Logger.LogInformation("GameClientSession created");
    }

    internal sealed class RoundRuntimeState
    {
        public object SyncRoot { get; } = new();
        public int RoundNumber { get; set; } = 1;
        public RoundPhase Phase { get; set; } = RoundPhase.Action;
        public DateTime PhaseEndsAtUtc { get; set; }
        public int PhaseDurationSeconds { get; set; } = Config.ROUND_ACTION_SECONDS;
        public bool SettlementEliminationApplied { get; set; }
        public bool IsSessionEnded { get; set; }
        public Dictionary<long, long> SettlementNominations { get; } = new();
        public bool BotNominationsInjected { get; set; }
        public List<SettlementContributionEntry> SettlementContributionEntries { get; } = new();
        public long SettlementContributionTopPlayerId { get; set; }
        public long SettlementContributionLowestPlayerId { get; set; }
        public int SettlementContributionTopValue { get; set; }
        public int SettlementContributionLowestValue { get; set; }
        public long SettlementContributionDecisiveTargetPlayerId { get; set; }
        public bool SettlementContributionNominationSuccess { get; set; }
        public long SettlementContributionEliminatedPlayerId { get; set; }
    }

    internal static void SetPresenceTracker(Proto0PresenceTracker presenceTracker)
    {
        _presenceTracker = presenceTracker;
    }

    internal static void CleanupAbandonedMatchingRuntime(long matchingId)
    {
        MatchStartGate.RemoveMatching(matchingId);
        GameRoundStates.TryRemove(matchingId, out _);
        _presenceTracker?.Remove(matchingId);
        RngCollectCooldownStore.ClearMatching(matchingId);
        if (GameTimers.TryRemove(matchingId, out var timer))
            timer.Dispose();
    }

    internal static (int RoundNumber, int TotalRounds, string Phase, int RemainingSeconds, int PhaseDurationSeconds,
        bool IsSessionEnded)? GetRoundSnapshot(long matchingId)
    {
        if (!Config.ROUND_SYSTEM_ENABLED)
            return null;

        if (!GameRoundStates.TryGetValue(matchingId, out var state))
            return null;

        lock (state.SyncRoot)
        {
            return (
                state.RoundNumber,
                Config.ROUND_TOTAL_COUNT,
                state.Phase.ToString(),
                GetRoundRemainingSeconds(state),
                state.PhaseDurationSeconds,
                state.IsSessionEnded);
        }
    }

    internal static void SetSurvivorPhaseManager(SurvivorPhaseManager manager)
    {
        _survivorPhaseManager = manager;
    }

    private bool IsSurvivorBoardActionLocked()
    {
        if (Config.SPOT_ARENA_P0_ENABLED)
            return false;

        return CurrentMapSubId > 0 &&
               _survivorPhaseManager is { } manager &&
               manager.HasMatching(CurrentMapSubId) &&
               !manager.AreOrbBoardActionsAllowed(CurrentMapSubId, CurrentArea);
    }
    internal static bool IsRoundActionPhase(long matchingId)
    {
        if (!MatchStartGate.IsGameplayActive(matchingId))
            return false;

        if (!Config.ROUND_SYSTEM_ENABLED)
            return true;

        if (!GameRoundStates.TryGetValue(matchingId, out var state))
            return true;

        lock (state.SyncRoot)
        {
            return !state.IsSessionEnded && state.Phase == RoundPhase.Action;
        }
    }

    internal static bool TryStartHeadlessActionRound(long matchingId)
    {
        if (!Config.ROUND_SYSTEM_ENABLED)
            return false;

        var state = new RoundRuntimeState
        {
            RoundNumber = 1,
            Phase = RoundPhase.Action,
            PhaseDurationSeconds = Config.ROUND_ACTION_SECONDS,
            PhaseEndsAtUtc = DateTime.UtcNow.AddSeconds(Config.ROUND_ACTION_SECONDS),
            SettlementEliminationApplied = false,
            IsSessionEnded = false
        };

        return GameRoundStates.TryAdd(matchingId, state);
    }

    private bool IsRoundActionLocked(out RoundPhase phase)
    {
        return IsRoundActionLocked(out phase, out _);
    }

    private bool IsRoundActionLocked(out RoundPhase phase, out string reason)
    {
        phase = RoundPhase.Action;
        reason = string.Empty;

        if (Config.SPOT_ARENA_P0_ENABLED && _spotArenaRespawning)
        {
            phase = RoundPhase.SpotArena;
            reason = "Waiting for spot respawn";
            return true;
        }

        if (IsEliminated)
        {
            reason = "Eliminated players cannot act";
            return true;
        }

        if (_isGameEnded)
        {
            reason = "Game has already ended";
            return true;
        }

        if (CurrentMapSubId > 0 && !MatchStartGate.IsGameplayActive(CurrentMapSubId))
        {
            reason = "Waiting for match start";
            return true;
        }

        if (!Config.ROUND_SYSTEM_ENABLED)
            return false;

        if (CurrentMapSubId <= 0 || !GameRoundStates.TryGetValue(CurrentMapSubId, out var state))
            return false;

        lock (state.SyncRoot)
        {
            phase = state.Phase;
            if (state.IsSessionEnded)
            {
                reason = "Round session has ended";
                return true;
            }

            if (state.Phase != RoundPhase.Action)
            {
                reason = "Round settlement in progress";
                return true;
            }

            return false;
        }
    }

    public new long? PlayerId { get; private set; }
    public MapId CurrentMapId { get; private set; }
    public long CurrentMapSubId { get; private set; }
    public AreaType CurrentArea { get; private set; } = AreaType.None;
    private PlayerState CurrentState { get; set; } = PlayerState.Idle;

    /// <summary>마지막 검증된 월드 좌표 — 근접 전투와 체크리스트 거리 판정용.</summary>
    public Vector3f? LastValidatedPosition => _lastValidatedPosition;

    // 마니또 체인 정보
    public long TargetPlayerId { get; private set; }
    public long PresenceBookmarkPlayerId { get; private set; }
    private JobTitle MyJobTitle { get; set; }
    private JobTitle TargetJobTitle { get; set; }
    public ManittoStatus ManittoStatus { get; private set; } = ManittoStatus.ACTIVE;

    // 이탈 페널티 면제 플래그
    /// <summary>게임 결과 화면 이후 퇴장: 페널티 면제</summary>
    private bool _isGameEnded;
    /// <summary>서버 셧다운/크래시로 인한 종료: 페널티 면제</summary>
    private bool _isServerInitiatedDisconnect;
    private bool _spotArenaRespawning;

    /// <summary>
    ///     탈락/관전 상태에서 행동 가능한지 체크
    /// </summary>
    internal bool IsGameEnded => _isGameEnded;
    internal int CurrentCorruption => Corruption;
    public bool IsEliminated => ManittoStatus == ManittoStatus.ELIMINATED || ManittoStatus == ManittoStatus.SPECTATING;
    private int? CurrentExploringInteractId { get; set; }

    // 인게임 스탯 (게임 종료 시 초기화)
    private int Stamina { get; set; } = InitialStamina;
    private int Corruption { get; set; } = InitialCorruption;

    // 게임 타이머 설정 (Config에서 참조)
    private static int GameDurationMinutes => Config.GAME_DURATION_MINUTES;
    private static int GameDurationSeconds => Config.GAME_DURATION_SECONDS;

    protected override void InitializeProtocolHandlers()
    {
        ProtocolRouter.RegisterHandler(Protocol.C_TO_G_HEART_BEAT, async _ => await HandleHeartbeat());
        ProtocolRouter.RegisterHandler(Protocol.C_TO_G_CONNECT,
            async bytes => await HandleMessage<C_TO_G_CONNECT>(bytes, HandleConnect));
        ProtocolRouter.RegisterHandler(Protocol.C_TO_G_MATCH_START_READY,
            async _ => await HandleMatchStartReady());
        ProtocolRouter.RegisterHandler(Protocol.C_TO_G_MOVE,
            async bytes => await HandleMessage<C_TO_G_MOVE>(bytes, HandleMove));
        ProtocolRouter.RegisterHandler(Protocol.C_TO_G_ATTACK,
            async bytes => await HandleMessage<C_TO_G_ATTACK>(bytes, HandleAttack));
        ProtocolRouter.RegisterHandler(Protocol.C_TO_G_SUMMON_ORB,
            async bytes => await HandleMessage<C_TO_G_SUMMON_ORB>(bytes, HandleSummonOrb));
        ProtocolRouter.RegisterHandler(Protocol.C_TO_G_DESTROY_ORB,
            async bytes => await HandleMessage<C_TO_G_DESTROY_ORB>(bytes, HandleDestroyOrb));
        ProtocolRouter.RegisterHandler(Protocol.C_TO_G_DEV_DUMMY_MOVE,
            async bytes => await HandleMessage<C_TO_G_DEV_DUMMY_MOVE>(bytes, HandleDevDummyMove));
        ProtocolRouter.RegisterHandler(Protocol.C_TO_G_SWARM_GROWTH_PICK,
            async bytes => await HandleMessage<C_TO_G_SWARM_GROWTH_PICK>(bytes, HandleSwarmGrowthPick));
        ProtocolRouter.RegisterHandler(Protocol.C_TO_G_INTERACT,
            async bytes => await HandleMessage<C_TO_G_INTERACT>(bytes, HandleInteract));
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

        // 플레이어 상호작용 프로토콜
        ProtocolRouter.RegisterHandler(Protocol.C_TO_G_PLAYER_INTERACT_REQUEST,
            async bytes => await HandleMessage<C_TO_G_PLAYER_INTERACT_REQUEST>(bytes, HandlePlayerInteractRequest));
        ProtocolRouter.RegisterHandler(Protocol.C_TO_G_PLAYER_INTERACT_RESPONSE,
            async bytes => await HandleMessage<C_TO_G_PLAYER_INTERACT_RESPONSE>(bytes, HandlePlayerInteractResponse));
        ProtocolRouter.RegisterHandler(Protocol.C_TO_G_PLAYER_INTERACT_END,
            async bytes => await HandleMessage<C_TO_G_PLAYER_INTERACT_END>(bytes, HandlePlayerInteractEnd));
        ProtocolRouter.RegisterHandler(Protocol.C_TO_G_PLAYER_INTERACT_USE_ITEM,
            async bytes => await HandleMessage<C_TO_G_PLAYER_INTERACT_USE_ITEM>(bytes, HandlePlayerInteractUseItem));
        ProtocolRouter.RegisterHandler(Protocol.C_TO_G_PLAYER_INTERACT_SHARE_RULE,
            async bytes =>
                await HandleMessage<C_TO_G_PLAYER_INTERACT_SHARE_RULE>(bytes, HandlePlayerInteractShareRule));

        // 마니또 프로토콜
        ProtocolRouter.RegisterHandler(Protocol.C_TO_G_DETECT_MANITTO,
            async bytes => await HandleMessage<C_TO_G_DETECT_MANITTO>(bytes, HandleDetectManitto));
        ProtocolRouter.RegisterHandler(Protocol.C_TO_G_SETTLEMENT_NOMINATE,
            async bytes => await HandleMessage<C_TO_G_SETTLEMENT_NOMINATE>(bytes, HandleSettlementNominate));
        ProtocolRouter.RegisterHandler(Protocol.C_TO_G_BOOKMARK_PRESENCE,
            async bytes => await HandleMessage<C_TO_G_BOOKMARK_PRESENCE>(bytes, HandleBookmarkPresence));
        ProtocolRouter.RegisterHandler(Protocol.C_TO_G_PLACE_TRACE,
            async bytes => await HandleMessage<C_TO_G_PLACE_TRACE>(bytes, HandlePlaceTrace));
        ProtocolRouter.RegisterHandler(Protocol.C_TO_G_PLACE_GIFT,
            async bytes => await HandleMessage<C_TO_G_PLACE_GIFT>(bytes, HandlePlaceGift));
        ProtocolRouter.RegisterHandler(Protocol.C_TO_G_RECALL_GIFT,
            async bytes => await HandleMessage<C_TO_G_RECALL_GIFT>(bytes, HandleRecallGift));
        ProtocolRouter.RegisterHandler(Protocol.C_TO_G_SABOTAGE_MISSION,
            async bytes => await HandleMessage<C_TO_G_SABOTAGE_MISSION>(bytes, HandleSabotageMission));
        ProtocolRouter.RegisterHandler(Protocol.C_TO_G_COMBINE_PARTS,
            async bytes => await HandleMessage<C_TO_G_COMBINE_PARTS>(bytes, HandleCombineParts));
        ProtocolRouter.RegisterHandler(Protocol.C_TO_G_MISSION_NODE_EXECUTE,
            async bytes => await HandleMessage<C_TO_G_MISSION_NODE_EXECUTE>(bytes, HandleMissionNodeExecute));
        ProtocolRouter.RegisterHandler(Protocol.C_TO_G_CHECKLIST_ACTIVITY_START,
            async bytes => await HandleMessage<C_TO_G_CHECKLIST_ACTIVITY_START>(bytes, HandleChecklistActivityStart));
        ProtocolRouter.RegisterHandler(Protocol.C_TO_G_CHECKLIST_ACTIVITY_FINISH,
            async bytes => await HandleMessage<C_TO_G_CHECKLIST_ACTIVITY_FINISH>(bytes, HandleChecklistActivityFinish));

        // RNG 채집 프로토콜 (v0.2.1, #79)
        // RNG 채집 2단계 프로토콜 (#134)
        ProtocolRouter.RegisterHandler(Protocol.C_TO_G_RNG_COLLECT_START,
            async bytes => await HandleMessage<C_TO_G_RNG_COLLECT_START>(bytes, HandleRngCollectStart));
        ProtocolRouter.RegisterHandler(Protocol.C_TO_G_RNG_COLLECT_FINISH,
            async bytes => await HandleMessage<C_TO_G_RNG_COLLECT_FINISH>(bytes, HandleRngCollectFinish));

        // 상호작용 선택지 프로토콜
        ProtocolRouter.RegisterHandler(Protocol.C_TO_G_ROOM_ENCOUNTER_AVOID,
            async bytes => await HandleMessage<C_TO_G_ROOM_ENCOUNTER_AVOID>(bytes, HandleRoomEncounterAvoid));

        ProtocolRouter.RegisterHandler(Protocol.C_TO_G_INTERACTION_ASK,
            async bytes => await HandleMessage<C_TO_G_INTERACTION_ASK>(bytes, HandleInteractionAsk));
        ProtocolRouter.RegisterHandler(Protocol.C_TO_G_INTERACTION_ANSWER,
            async bytes => await HandleMessage<C_TO_G_INTERACTION_ANSWER>(bytes, HandleInteractionAnswer));

        // 구역 이동 프로토콜 (GDD v0.0.8: 문/계단 마커 방식)
        ProtocolRouter.RegisterHandler(Protocol.C_TO_G_AREA_MOVE,
            async bytes => await HandleMessage<C_TO_G_AREA_MOVE>(bytes, HandleAreaMove));
        ProtocolRouter.RegisterHandler(Protocol.C_TO_G_ROOM_ENTRY_EVENT_CHOICE,
            async bytes => await HandleMessage<C_TO_G_ROOM_ENTRY_EVENT_CHOICE>(bytes, HandleRoomEntryEventChoice));

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
        _interactTimeoutCts?.Cancel();
        _interactTimeoutCts?.Dispose();
        _interactTimeoutCts = null;
        _botInteractTimeoutCts?.Cancel();
        _botInteractTimeoutCts?.Dispose();
        _botInteractTimeoutCts = null;
        Logger.LogInformation("GameClient removed: PlayerId={PlayerId}", PlayerId);
        _onLeaveCallback(this);
    }

    public override void OnDisconnect()
    {
        StopAllPeriodicBuffs();
        _botInteractTimeoutCts?.Cancel();
        _botInteractTimeoutCts?.Dispose();
        _botInteractTimeoutCts = null;
        _interactTimeoutCts?.Cancel();
        _interactTimeoutCts?.Dispose();
        _interactTimeoutCts = null;

        // 게임 진행 중 의도적 이탈 시 페널티 기록
        // 면제: SPECTATING/ELIMINATED, 게임 결과 화면 이후, 서버 주도 종료
        if (PlayerId.HasValue && !IsEliminated && CurrentMapSubId > 0
            && !_isGameEnded && !_isServerInitiatedDisconnect)
        {
            _recordLeavePenalty(PlayerId.Value);
        }

        Logger.LogInformation("GameClient disconnected: PlayerId={PlayerId}", PlayerId);
        _onLeaveCallback(this);
    }

    /// <summary>
    ///     게임 결과 패킷 전송 후 호출. 이후 퇴장은 페널티 면제.
    ///     동시에 정상 완료 보상으로 이탈 횟수 1 감소.
    /// </summary>
    public void MarkGameEnded()
    {
        _isGameEnded = true;
        if (PlayerId.HasValue)
            _recordGameCompletion(PlayerId.Value);
    }

    /// <summary>
    ///     서버 셧다운/크래시 시 호출. 비자발적 이탈로 간주하여 페널티 면제.
    /// </summary>
    public void MarkServerInitiatedDisconnect()
    {
        _isServerInitiatedDisconnect = true;
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
