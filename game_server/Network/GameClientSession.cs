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

public partial class GameClientSession : SessionBase
{
    private readonly Action<GameClientSession> _onLeaveCallback;
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
    private readonly SabotageManager _sabotageManager;

    public new long? PlayerId { get; private set; }
    public MapId CurrentMapId { get; private set; }
    public long CurrentMapSubId { get; private set; }
    public AreaType CurrentArea { get; private set; } = AreaType.None;
    public PlayerState CurrentState { get; private set; } = PlayerState.Idle;
    public int? CurrentExploringInteractId { get; private set; }
    private bool _isSleeping;

    // 플레이어 상호작용 요청 상태
    private long? _pendingInteractPlayerId;
    private CancellationTokenSource? _interactTimeoutCts;
    private DateTime _lastInteractRejectTime = DateTime.MinValue;
    private static readonly TimeSpan InteractCooldown = TimeSpan.FromSeconds(5);

    // 활성 대화 상대 PlayerId (수락 후 대화 중)
    private long? _activeConversationPlayerId;

    // 플레이어가 발견한 행동 수칙 (ruleId → 최초 발견자 PlayerId)
    private readonly Dictionary<int, long> _discoveredRules = new();

    // 이미 공유한 수칙 추적 (ruleId, targetPlayerId) — 동일 대상에 중복 공유 방지
    private readonly HashSet<(int RuleId, long TargetPlayerId)> _sharedRules = new();

    // 인게임 스탯 (게임 종료 시 초기화)
    public int Stamina { get; private set; } = 100;
    public int Corruption { get; private set; } = 0;
    private const int MaxStamina = 100;
    private const int MaxCorruption = 100;

    private Timer? _periodicBuffTimer;
    private readonly List<PeriodicBuffEntry> _activePeriodicBuffs = new();

    private class PeriodicBuffEntry
    {
        public BuffSubType SubType;
        public int Value;
        public int IntervalSeconds;
        public float ElapsedSeconds;
    }

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
        DoorStateManager doorStateManager,
        SabotageManager sabotageManager)
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

        _logger.LogInformation("GameClientSession created");
    }

    protected override void InitializeProtocolHandlers()
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

        // 플레이어 상호작용 프로토콜
        _protocolRouter.RegisterHandler(Protocol.C_TO_G_PLAYER_INTERACT_REQUEST, async (bytes) => await HandleMessage<C_TO_G_PLAYER_INTERACT_REQUEST>(bytes, HandlePlayerInteractRequest));
        _protocolRouter.RegisterHandler(Protocol.C_TO_G_PLAYER_INTERACT_RESPONSE, async (bytes) => await HandleMessage<C_TO_G_PLAYER_INTERACT_RESPONSE>(bytes, HandlePlayerInteractResponse));
        _protocolRouter.RegisterHandler(Protocol.C_TO_G_PLAYER_INTERACT_END, async (bytes) => await HandleMessage<C_TO_G_PLAYER_INTERACT_END>(bytes, HandlePlayerInteractEnd));
        _protocolRouter.RegisterHandler(Protocol.C_TO_G_PLAYER_INTERACT_USE_ITEM, async (bytes) => await HandleMessage<C_TO_G_PLAYER_INTERACT_USE_ITEM>(bytes, HandlePlayerInteractUseItem));
        _protocolRouter.RegisterHandler(Protocol.C_TO_G_PLAYER_INTERACT_SHARE_RULE, async (bytes) => await HandleMessage<C_TO_G_PLAYER_INTERACT_SHARE_RULE>(bytes, HandlePlayerInteractShareRule));
    }

    protected override bool ShouldSkipLogging(Protocol protocolId)
    {
        return protocolId == Protocol.C_TO_G_HEART_BEAT || protocolId == Protocol.C_TO_G_MOVE;
    }

    public override void OnRemoved()
    {
        StopAllPeriodicBuffs();
        _logger.LogInformation($"GameClient removed: PlayerId={PlayerId}");
        _onLeaveCallback(this);
    }

    public override void OnDisconnect()
    {
        StopAllPeriodicBuffs();
        _logger.LogInformation($"GameClient disconnected: PlayerId={PlayerId}");
        _onLeaveCallback(this);
    }

}
