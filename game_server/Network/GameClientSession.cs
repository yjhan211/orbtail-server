using System.Collections.Concurrent;
using game_server.services;
using Microsoft.Extensions.Logging;
using network.common;
using network.common.data.models;
using network.core;
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
    private const int MaxCorruption = 100;

    // 하트비트 타임아웃 (초)
    private const int HeartbeatTimeoutSeconds = 30;
    private static readonly TimeSpan InteractCooldown = TimeSpan.FromSeconds(5);
    private static readonly ConcurrentDictionary<long, Timer> GameTimers = new();
    private readonly List<PeriodicBuffEntry> _activePeriodicBuffs = new();
    private readonly AreaRuleManager _areaRuleManager;
    private readonly CorridorRuleManager _corridorRuleManager;

    // 플레이어가 발견한 행동 수칙 (ruleId → 최초 발견자 PlayerId)
    private readonly Dictionary<int, long> _discoveredRules = new();
    private readonly DoorStateManager _doorStateManager;
    private readonly ExitInstanceManager _exitInstanceManager;
    private readonly Func<MapId, long, List<GameClientSession>> _getSessionsByInstance;
    private readonly InGameInventoryManager _inGameInventoryManager;
    private readonly InteractableStateManager _interactableStateManager;
    private readonly InteractRuleManager _interactRuleManager;
    private readonly ItemPoolManager _itemPoolManager;
    private readonly Action<GameClientSession> _onLeaveCallback;
    private readonly Action<long, GameClientSession> _registerSessionCallback;
    private readonly SabotageManager _sabotageManager;
    private readonly ManittoChainManager _manittoChainManager;
    private readonly MissionManager _missionManager;
    private readonly AreaClosureManager _areaClosureManager;

    // 이미 공유한 수칙 추적 (ruleId, targetPlayerId) — 동일 대상에 중복 공유 방지
    private readonly HashSet<(int RuleId, long TargetPlayerId)> _sharedRules = new();

    // 활성 대화 상대 PlayerId (수락 후 대화 중)
    private long? _activeConversationPlayerId;
    private CancellationTokenSource? _interactTimeoutCts;
    private bool _isSleeping;
    private DateTime _lastHeartbeatTime = DateTime.UtcNow;
    private DateTime _lastInteractRejectTime = DateTime.MinValue;
    private DateTime _lastMoveTime = DateTime.UtcNow;
    private DateTime _lastSaveTime = DateTime.UtcNow;

    private Vector3f? _lastValidatedPosition;
    private Cell? _lastValidCell;

    // 플레이어 상호작용 요청 상태
    private long? _pendingInteractPlayerId;

    private Timer? _periodicBuffTimer;

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
        DoorStateManager doorStateManager,
        SabotageManager sabotageManager,
        ManittoChainManager manittoChainManager,
        MissionManager missionManager,
        AreaClosureManager areaClosureManager)
        : base(token, logger, cacheHelper, redLock)
    {
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
        _sabotageManager = sabotageManager;
        _manittoChainManager = manittoChainManager;
        _missionManager = missionManager;
        _areaClosureManager = areaClosureManager;

        // ReSharper disable once VirtualMemberCallInConstructor
        InitializeProtocolHandlers();
        Logger.LogInformation("GameClientSession created");
    }

    public new long? PlayerId { get; private set; }
    public MapId CurrentMapId { get; private set; }
    public long CurrentMapSubId { get; private set; }
    public AreaType CurrentArea { get; private set; } = AreaType.None;
    private PlayerState CurrentState { get; set; } = PlayerState.Idle;

    // 마니또 체인 정보
    public long TargetPlayerId { get; private set; }
    public JobTitle MyJobTitle { get; private set; }
    public JobTitle TargetJobTitle { get; private set; }
    private int? CurrentExploringInteractId { get; set; }

    // 인게임 스탯 (게임 종료 시 초기화)
    private int Stamina { get; set; } = 100;
    public int Corruption { get; private set; }

    // 게임 타이머 설정 (Config에서 참조)
    private static int GameDurationMinutes => Config.GAME_DURATION_MINUTES;
    private static int GameDurationSeconds => Config.GAME_DURATION_SECONDS;

    protected override void InitializeProtocolHandlers()
    {
        ProtocolRouter.RegisterHandler(Protocol.C_TO_G_HEART_BEAT, async _ => await HandleHeartbeat());
        ProtocolRouter.RegisterHandler(Protocol.C_TO_G_CONNECT,
            async bytes => await HandleMessage<C_TO_G_CONNECT>(bytes, HandleConnect));
        ProtocolRouter.RegisterHandler(Protocol.C_TO_G_MOVE,
            async bytes => await HandleMessage<C_TO_G_MOVE>(bytes, HandleMove));
        ProtocolRouter.RegisterHandler(Protocol.C_TO_G_ATTACK,
            async bytes => await HandleMessage<C_TO_G_ATTACK>(bytes, HandleAttack));
        ProtocolRouter.RegisterHandler(Protocol.C_TO_G_INTERACT,
            async bytes => await HandleMessage<C_TO_G_INTERACT>(bytes, HandleInteract));
        ProtocolRouter.RegisterHandler(Protocol.C_TO_G_EXPLORE_START,
            async bytes => await HandleMessage<C_TO_G_EXPLORE_START>(bytes, HandleExploreStart));
        ProtocolRouter.RegisterHandler(Protocol.C_TO_G_EXPLORE_SELECT,
            async bytes => await HandleMessage<C_TO_G_EXPLORE_SELECT>(bytes, HandleExploreSelect));
        ProtocolRouter.RegisterHandler(Protocol.C_TO_G_EXPLORE_END,
            async bytes => await HandleMessage<C_TO_G_EXPLORE_END>(bytes, HandleExploreEnd));
        ProtocolRouter.RegisterHandler(Protocol.C_TO_G_USE_INGAME_ITEM,
            async bytes => await HandleMessage<C_TO_G_USE_INGAME_ITEM>(bytes, HandleUseInGameItem));
        ProtocolRouter.RegisterHandler(Protocol.C_TO_G_PLAYER_STATE,
            async bytes => await HandleMessage<C_TO_G_PLAYER_STATE>(bytes, HandlePlayerState));

        // 탈출 절차 프로토콜
        ProtocolRouter.RegisterHandler(Protocol.C_TO_G_EXIT_ADVANCE,
            async bytes => await HandleMessage<C_TO_G_EXIT_ADVANCE>(bytes, HandleExitAdvance));

        // 로비 복귀 프로토콜
        ProtocolRouter.RegisterHandler(Protocol.C_TO_G_RETURN_TO_LOBBY,
            async bytes => await HandleMessage<C_TO_G_RETURN_TO_LOBBY>(bytes, HandleReturnToLobby));

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
        Logger.LogInformation("GameClient removed: PlayerId={PlayerId}", PlayerId);
        _onLeaveCallback(this);
    }

    public override void OnDisconnect()
    {
        StopAllPeriodicBuffs();
        _interactTimeoutCts?.Cancel();
        _interactTimeoutCts?.Dispose();
        _interactTimeoutCts = null;
        Logger.LogInformation("GameClient disconnected: PlayerId={PlayerId}", PlayerId);
        _onLeaveCallback(this);
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
            .Where(s => s.CurrentArea == area && (!excludeSelf || s.PlayerId != PlayerId))
            .ToList();
    }

    /// <summary>
    ///     같은 인스턴스의 다른 모든 세션 목록 반환 (영역 무관, PlayerId.HasValue 보장)
    /// </summary>
    private List<GameClientSession> GetOtherValidSessions(List<GameClientSession> allSessions)
    {
        return allSessions
            .Where(s => s.PlayerId.HasValue && s.PlayerId != PlayerId)
            .ToList();
    }

    private class PeriodicBuffEntry
    {
        public float ElapsedSeconds;
        public int IntervalSeconds;
        public BuffSubType SubType;
        public int Value;
    }
}
