using System.Collections.Concurrent;
using System.Net;
using game_server.admin.dto;
using game_server.controllers;
using game_server.network;
using game_server.services;
using MessagePack;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using network.common;
using network.common.data;
using network.common.data.helpers;
using network.common.data.models;
using network.core;
using network.helpers;
using network.infrastructure;
using network.interfaces;
using network.packets;

namespace game_server;

public class GameServer(
    IConfiguration configuration,
    ILogger<GameServer> logger,
    INatsClientFactory natsClientFactory,
    ICacheHelper cacheHelper,
    INetworkService networkService,
    IRedisConnectionPool redisPool,
    ServerConfig serverConfig)
    : IHostedService
{
    // 하트비트 체크 간격 (10초마다 체크)
    private const int HeartbeatCheckIntervalSeconds = 10;

    // 복도 정지 체크 간격
    private const int CorridorStopCheckIntervalMs = 500;
    private readonly AreaRuleManager _areaRuleManager = new();
    private readonly ConcurrentDictionary<long, GameClientSession> _clientSessions = new();
    private readonly CorridorRuleManager _corridorRuleManager = new();
    private readonly DoorStateManager _doorStateManager = new();
    private readonly InGameInventoryManager _inGameInventoryManager = new();

    private readonly List<InstanceMapManager> _instanceControllerList = [];
    private readonly InteractableStateManager _interactableStateManager = new();
    private readonly ItemPoolManager _itemPoolManager = new();
    private readonly SabotageManager _sabotageManager = new();
    private readonly InteractionLogManager _interactionLogManager = new();
    private readonly ManittoChainManager _manittoChainManager = new(logger);
    private readonly MissionManager _missionManager = new(logger);
    private readonly ChecklistManager _checklistManager = new(logger);
    private readonly MatchingConfigService _matchingConfigService = new(cacheHelper, logger);
    // _areaClosureManager은 InitializeServices()에서 _matchingConfigService 생성 후 초기화
    private AreaClosureManager _areaClosureManager = null!;
    private readonly TraceManager _traceManager = new();
    private readonly BotPlayerManager _botPlayerManager = new(logger);
    private readonly GameEventLogManager _gameEventLogManager = new();
    private readonly EncounterRevealManager _encounterRevealManager = new();
    private readonly Proto0PresenceTracker _presenceTracker = new();
    private readonly ConcurrentDictionary<long, Timer> _headlessRoundTimers = new();
    private long _adminBotOnlyMatchingIdSeed = 9_000_000;
    private long _adminBotOnlyPlayerIdSeed = -900_000_000;

    private Timer? _corridorStopCheckTimer;
    private CancellationTokenSource _cts = new();
    private Timer? _heartbeatCheckTimer;
    private Timer? _resourceTickTimer;        // 정신력 자연감소 + 타겟 근접 회복 + 시한부
    private Timer? _areaClosureTickTimer;     // 구역 폐쇄 체크
    private Timer? _targetLocationTimer;      // 타겟 위치 전송
    private Timer? _botMovementTimer;         // #127 봇 walking step (250ms)
    private Timer? _botMissionTimer;          // #134 봇 미션 처리 (RNG 채집/결합 — 1초 주기)
    private Timer? _checklistProgressTickTimer;

    // 자원 틱 설정 (GDD v0.0.5 확정 수치)
    private int _botMovementProcessing;

    internal const int ResourceTickIntervalSeconds = 5;
    private const int ChecklistProgressTickIntervalSeconds = 1;
    // 오염도 점진적 가속: 0~5분 +2, 5~10분 +4, 10분+ +6 (전반적 증가량 2배 상향)
    // 프로토 0: 타겟에서 떨어지면(복도/빈방) 압박이 실질적이도록 기본 감소를 회복(-3)과 균형 맞춰 상향. 튜닝 노브.
    private const int MentalDecayPhase1 = 3;            // 0~5분: 5초당 오염도 +3
    private const int MentalDecayPhase2 = 4;            // 5~10분: 5초당 오염도 +4
    private const int MentalDecayPhase3 = 5;            // 10분+: 5초당 오염도 +5
    private const int Phase2StartSeconds = 300;          // 5분
    private const int Phase3StartSeconds = 600;          // 10분
    internal const int TargetProximityRecovery = 8;      // 타겟 동일 구역 시 회복량 (5초당 오염도 -8)
    private const int IsolationStatusEffectId = 1001;   // status_effect_info: 고립
    internal const int NearbyStatusEffectId = 1002;      // status_effect_info: 의존
    internal const int ProximityStatusEffectId = 1010;   // status_effect_info: 교감 (#161)
    private const int ClosedAreaStatusEffectId = 1003;  // status_effect_info: 폐쇄 구역
    private const double SharpGazeRecoveryMultiplier = 0.5;
    private const int TerminalDecayAmount = 5;          // 시한부 추가 감소량 (5초당 오염도 +5)
    internal const int MoveStaminaCost = 3;              // 구역 이동 시 스태미나 소모 (인접 구역 진입)
    // ClosedAreaStaminaPenaltyPerTick 제거 — v0.1.9 #66: 폐쇄 구역 패널티 → 오염도로 변경
    internal const int TraceFoundManittoRecovery = 15;   // 흔적 발견 시 마니또 정신력 회복량
    internal const int TraceFoundTargetDecay = 10;       // 흔적 발견 시 타겟 오염도 증가량

    public async Task StartAsync(CancellationToken cancellationToken)
    {
        try
        {
            logger.LogInformation("Game server starting...");
            GameClientSession.SetPresenceTracker(_presenceTracker);

            InitializeServices();
            InitializeControllers();

            // Redis에서 폐쇄 config 복원 (재시작/핫리로드 후에도 어드민 설정 유지)
            await _matchingConfigService.LoadClosureConfigFromRedisAsync();

            StartTcpServer();
            StartHeartbeatChecker();
            StartCorridorStopCheckTimer();
            StartResourceTickTimer();
            StartAreaClosureTickTimer();
            StartTargetLocationTimer();
            StartBotMovementTimer();
            StartBotMissionTimer();
            StartChecklistProgressTickTimer();

            _cts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);

            logger.LogInformation("Game server started successfully.");
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "Game server starting failed.");
            throw;
        }
    }

    public async Task StopAsync(CancellationToken cancellationToken)
    {
        logger.LogInformation("Game server stopping...");

        // 서버 셧다운 시 모든 세션을 서버 주도 종료로 마킹 → 페널티 면제
        foreach (var session in _clientSessions.Values)
            session.MarkServerInitiatedDisconnect();

        await _cts.CancelAsync();

        // 타이머 정리
        if (_heartbeatCheckTimer != null)
        {
            await _heartbeatCheckTimer.DisposeAsync();
            _heartbeatCheckTimer = null;
        }

        if (_corridorStopCheckTimer != null)
        {
            await _corridorStopCheckTimer.DisposeAsync();
            _corridorStopCheckTimer = null;
        }

        if (_resourceTickTimer != null) { await _resourceTickTimer.DisposeAsync(); _resourceTickTimer = null; }
        if (_areaClosureTickTimer != null) { await _areaClosureTickTimer.DisposeAsync(); _areaClosureTickTimer = null; }
        if (_targetLocationTimer != null) { await _targetLocationTimer.DisposeAsync(); _targetLocationTimer = null; }
        if (_botMovementTimer != null) { await _botMovementTimer.DisposeAsync(); _botMovementTimer = null; }
        if (_botMissionTimer != null) { await _botMissionTimer.DisposeAsync(); _botMissionTimer = null; }
        if (_checklistProgressTickTimer != null) { await _checklistProgressTickTimer.DisposeAsync(); _checklistProgressTickTimer = null; }
        foreach (var timer in _headlessRoundTimers.Values)
            await timer.DisposeAsync();
        _headlessRoundTimers.Clear();

        await Task.WhenAll(_instanceControllerList.Select(c => c.ShutdownAsync()));

        _cts.Dispose();

        logger.LogInformation("Game server stopped.");
    }

    private void InitializeServices()
    {
        string natsEndpoint = configuration.GetRequiredString("natsEndPoint");

        // MatchingConfigService 의존 — _matchingConfigService 필드 초기화 후 생성
        _areaClosureManager = new AreaClosureManager(logger, _matchingConfigService);

        try
        {
            natsClientFactory.Initialize(natsEndpoint);
            // 서버 환경에서 CSV 파일 경로 설정
            // Dev: 소스 디렉토리에서 직접 읽기 (Docker 볼륨 마운트 대응)
            string networkSourcePath = Path.GetFullPath(Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "..", "..",
                "..", "..", "network"));
            GameDataHelper.SetBasePath(Directory.Exists(Path.Combine(networkSourcePath, "Common", "csv"))
                ? networkSourcePath
                : AppDomain.CurrentDomain.BaseDirectory);
            GameDataHelper.Initialize();
            MapHelper.Initialize(serverConfig.GameServerNum);
            Action<string> log = msg => logger.LogInformation(msg);
            _interactableStateManager.Initialize(log);
            _inGameInventoryManager.Initialize(log);
            _corridorRuleManager.Initialize(log, OnCorridorStopViolation);
            _areaRuleManager.Initialize(log);
            _itemPoolManager.Initialize(log);
            _checklistManager.Initialize(log);
            _sabotageManager.Initialize(log);
            _sabotageManager.SetStateChangeCallback(OnSabotageStateChange);
            _sabotageManager.SetTimeoutCallback(OnSabotageTimeout);
        }
        catch (Exception ex)
        {
            throw new InvalidOperationException("Failed to initialize services.", ex);
        }
    }

    private void InitializeControllers()
    {
        var instanceController = new InstanceMapManager(logger, natsClientFactory.Create(), cacheHelper,
            serverConfig, _clientSessions, _interactableStateManager, _inGameInventoryManager, _areaRuleManager,
            _corridorRuleManager);
        instanceController.Initialize();
        _instanceControllerList.Add(instanceController);
    }

    private void StartTcpServer()
    {
        short port = configuration.GetValue<short>("clientPort", 9001);
        networkService.SessionCreatedCallback += OnClientSessionCreated;
        networkService.Listen(IPAddress.Any, port);
        logger.LogInformation($"TCP server listening on port {port}");
    }

    private void StartHeartbeatChecker()
    {
        _heartbeatCheckTimer = new Timer(
            CheckHeartbeatTimeouts,
            null,
            TimeSpan.FromSeconds(HeartbeatCheckIntervalSeconds),
            TimeSpan.FromSeconds(HeartbeatCheckIntervalSeconds));
        logger.LogInformation("Heartbeat checker started (interval: {Interval}s)", HeartbeatCheckIntervalSeconds);
    }

    // ===== 자원 틱 (정신력 자연감소, 타겟 근접 회복, 시한부) =====

    private void StartResourceTickTimer()
    {
        _resourceTickTimer = new Timer(ProcessResourceTick, null,
            TimeSpan.FromSeconds(ResourceTickIntervalSeconds),
            TimeSpan.FromSeconds(ResourceTickIntervalSeconds));
        logger.LogInformation("자원 틱 타이머 시작 ({Interval}초)", ResourceTickIntervalSeconds);
    }

    private void StartChecklistProgressTickTimer()
    {
        _checklistProgressTickTimer = new Timer(ProcessChecklistProgressTick, null,
            TimeSpan.FromSeconds(ChecklistProgressTickIntervalSeconds),
            TimeSpan.FromSeconds(ChecklistProgressTickIntervalSeconds));
        logger.LogInformation("체크리스트 진행 틱 타이머 시작 ({Interval}초)", ChecklistProgressTickIntervalSeconds);
    }

    private void ProcessChecklistProgressTick(object? state)
    {
        try
        {
            var activeSessions = _clientSessions.Values
                .Where(s => s.PlayerId.HasValue && !s.IsEliminated)
                .ToList();

            foreach (var session in activeSessions)
            {
                if (!GameClientSession.IsRoundActionPhase(session.CurrentMapSubId))
                    continue;
                if (session.CurrentArea == AreaType.None || session.TargetPlayerId == 0)
                    continue;

                GameClientSession? targetSession = activeSessions.FirstOrDefault(s =>
                    s.PlayerId == session.TargetPlayerId &&
                    s.CurrentMapSubId == session.CurrentMapSubId &&
                    !s.IsEliminated);
                BotPlayerState? targetBot = targetSession == null
                    ? _botPlayerManager.GetBot(session.CurrentMapSubId, session.TargetPlayerId)
                    : null;
                if (targetSession == null && targetBot is not { IsEliminated: false })
                    continue;

                if (IsTargetWithinProximity(session, targetSession, targetBot))
                    session.AdvanceTargetProximityChecklistProgress(ChecklistProgressTickIntervalSeconds);
            }

            var matchingIds = GetActiveMatchingIds();
            foreach (long matchingId in matchingIds)
            {
                if (!GameClientSession.IsRoundActionPhase(matchingId)) continue;
                if (!_botPlayerManager.HasBots(matchingId)) continue;

                ProcessBotTargetProximityChecklistProgress(
                    matchingId,
                    activeSessions,
                    ChecklistProgressTickIntervalSeconds);
            }
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "체크리스트 진행 틱 처리 중 오류");
        }
    }

    /// <summary>
    ///     경과 시간에 따른 오염도 자연증가량 결정 (GDD v0.0.5 점진적 가속)
    /// </summary>
    private int GetMentalDecayAmount(long matchingId)
    {
        var closureState = _areaClosureManager.GetMatchingState(matchingId);
        if (closureState == null) return MentalDecayPhase1;

        double elapsed = (DateTime.UtcNow - closureState.GameStartTime).TotalSeconds;
        if (elapsed >= Phase3StartSeconds) return MentalDecayPhase3;
        if (elapsed >= Phase2StartSeconds) return MentalDecayPhase2;
        return MentalDecayPhase1;
    }

    private void ProcessResourceTick(object? state)
    {
        try
        {
            var activeSessions = _clientSessions.Values
                .Where(s => s.PlayerId.HasValue && !s.IsEliminated)
                .ToList();

            foreach (var session in activeSessions)
            {
                if (!GameClientSession.IsRoundActionPhase(session.CurrentMapSubId))
                    continue;

                bool isTerminal = session.ManittoStatus == ManittoStatus.TERMINAL;

                int corruptionDelta = 0;

                if (!isTerminal && session.CurrentArea != AreaType.None)
                {
                    GameClientSession? targetSession = null;
                    BotPlayerState? targetBot = null;
                    bool targetInSameArea = false;

                    targetSession = activeSessions.FirstOrDefault(s => s.PlayerId == session.TargetPlayerId);
                    targetInSameArea = targetSession != null && targetSession.CurrentArea == session.CurrentArea;
                    if (!targetInSameArea)
                    {
                        // 프로토 0: 회복은 타겟과 같은 영역에 있을 때 적용한다.
                        //   혼잡할수록 느림 — 회복량 = 기본 × (2 / 영역 총인원).
                        targetBot = _botPlayerManager.GetBot(session.CurrentMapSubId, session.TargetPlayerId);
                        targetInSameArea = targetBot is { IsEliminated: false } &&
                                           targetBot.CurrentArea == session.CurrentArea;
                    }

                    bool targetWithinProximity = IsTargetWithinProximity(session, targetSession, targetBot);

                    if (!targetInSameArea)
                    {
                        int isolationDelta = ResolveStatusEffectCorruptionDelta(
                            IsolationStatusEffectId,
                            GetMentalDecayAmount(session.CurrentMapSubId));
                        isolationDelta = PassiveBuffUtility.ApplyReduction(
                            isolationDelta,
                            session.ActiveBuffIds,
                            BuffSubType.ISOLATION_CORRUPTION_GAIN_DOWN);
                        corruptionDelta += isolationDelta;
                    }

                    if (targetInSameArea &&
                        !session.ShouldSkipTargetEncounterRecoveryTick(DateTime.UtcNow, ResourceTickIntervalSeconds))
                    {
                        int pop = CountAreaPopulation(activeSessions, session.CurrentMapSubId, session.CurrentArea);
                        int recovery = Math.Max(1,
                            (int)Math.Round(TargetProximityRecovery * (2.0 / Math.Max(2, pop))));
                        int recoveryDelta = ResolveStatusEffectCorruptionDelta(NearbyStatusEffectId, recovery);
                        recoveryDelta = ApplyTargetEncounterStability(session, recoveryDelta);
                        recoveryDelta = ApplySharpGazeRecoveryPenalty(session, targetSession, targetBot, recoveryDelta);
                        corruptionDelta += recoveryDelta;

                        // 교감(#161): 같은 영역에서 거리까지 좁히면 추가 회복.
                        // 1:1로 붙어 있는 상황 자체가 보상 조건이라 혼잡 보정은 없다.
                        if (targetWithinProximity)
                        {
                            corruptionDelta += ResolveStatusEffectCorruptionDelta(
                                ProximityStatusEffectId, Config.TARGET_PROXIMITY_RECOVERY_BONUS);
                        }

                        session.MarkTargetEncounterRecoveryApplied(DateTime.UtcNow);
                    }

                }

                // [TEMP] 3. 시한부 추가 감소 — 디버깅용 비활성
                // if (isTerminal)
                //     corruptionDelta += TerminalDecayAmount;

                // 4. 강당 체류 오염도 추가 증가 (패키지 Y 3A, GDD §3.1.1, #24)
                if (!isTerminal && session.CurrentArea == (AreaType)Config.AUDITORIUM_AREA_TYPE)
                {
                    var targetSession2 = activeSessions.FirstOrDefault(s => s.PlayerId == session.TargetPlayerId);
                    bool targetInGym = targetSession2 != null && targetSession2.CurrentArea == session.CurrentArea;
                    if (!targetInGym)
                        corruptionDelta += Config.AUDITORIUM_STAY_CORRUPTION_BONUS;
                }

                // 4. 폐쇄 구역 체류 시 오염도 추가 증가
                if (session.CurrentArea != AreaType.None &&
                    _areaClosureManager.IsAreaClosed(session.CurrentMapSubId, session.CurrentArea))
                {
                    int closedAreaBasePenalty = ResolveStatusEffectCorruptionDelta(
                        ClosedAreaStatusEffectId,
                        Config.CLOSED_AREA_CORRUPTION_TICK);
                    int closedAreaPenalty = ApplyClosedAreaResistance(session, closedAreaBasePenalty);
                    corruptionDelta += closedAreaPenalty;
                }

                session.ModifyStats(corruptionDelta: corruptionDelta);

                // 5. 자원 고갈 탈락 체크
                session.CheckResourceElimination();
            }

            // 6. 봇 플레이어 자원 틱
            var matchingIds = GetActiveMatchingIds();

            foreach (long matchingId in matchingIds)
            {
                if (!GameClientSession.IsRoundActionPhase(matchingId)) continue;
                if (!_botPlayerManager.HasBots(matchingId)) continue;
                // 봇도 사람과 같은 타겟 부재/의존/밀착/폐쇄구역 자원 변동을 적용한다.
                var botResourceSnapshots = activeSessions
                    .Where(s => s.CurrentMapSubId == matchingId && s.PlayerId.HasValue)
                    .Select(s => new BotBehaviorPlayerSnapshot
                    {
                        PlayerId = s.PlayerId!.Value,
                        TargetPlayerId = s.TargetPlayerId,
                        CurrentArea = s.CurrentArea,
                        Position = s.LastValidatedPosition,
                        IsEliminated = s.IsEliminated
                    })
                    .ToList();
                var tickResult = _botPlayerManager.ProcessBotTick(
                    matchingId,
                    ResolveStatusEffectCorruptionDelta(IsolationStatusEffectId, GetMentalDecayAmount(matchingId)),
                    ResolveStatusEffectCorruptionDelta(NearbyStatusEffectId, TargetProximityRecovery),
                    ResolveStatusEffectCorruptionDelta(ProximityStatusEffectId, Config.TARGET_PROXIMITY_RECOVERY_BONUS),
                    ResolveStatusEffectCorruptionDelta(ClosedAreaStatusEffectId, Config.CLOSED_AREA_CORRUPTION_TICK),
                    _areaClosureManager,
                    botResourceSnapshots);

                // #125: 봇 위치 이동 이벤트 → 같은 영역 인간 세션에 패킷 브로드캐스트
                foreach (var ev in tickResult.Movements)
                    BroadcastBotMovement(matchingId, ev, activeSessions);

                // #26: 봇 탈락 → 체인 단절 알림 + 영향받는 플레이어/봇 상태 변경
                foreach (var (botId, reason) in tickResult.Eliminated)
                {
                    _gameEventLogManager.LogElimination(matchingId, botId, reason.ToString(), isBot: true);
                    ProcessBotElimination(matchingId, botId, reason, activeSessions);
                }

                // 봇 미션 처리(부품 회수/결합/RNG 채집)는 별도 1초 타이머(ProcessBotMission)에서 수행.

                // #26: 시한부 봇 사보타주 + 색출 시뮬
                ProcessBotTerminalActionsForMatching(matchingId, activeSessions);

                // H6: DEMO_MODE BR 봇 09:30 함정 흔적 1회 배치
                var tracePlaced = _botPlayerManager.ProcessDemoBotTracePlacement(matchingId, _traceManager);
                if (tracePlaced.HasValue)
                    BroadcastTracePlacedAnnounce(matchingId, activeSessions, tracePlaced.Value);
            }

            // 7. 프로토 0 기척 틱 (#159) — 5초 조우 강도 계산 후 인간 세션에 전송
            foreach (long matchingId in matchingIds)
            {
                if (!GameClientSession.IsRoundActionPhase(matchingId)) continue;
                var playerAreas = BuildPlayerAreas(matchingId, activeSessions);
                _presenceTracker.Tick(matchingId, playerAreas);
                int roundNumber = GameClientSession.GetRoundSnapshot(matchingId)?.RoundNumber ?? 0;

                foreach (var session in activeSessions)
                {
                    if (session.CurrentMapSubId != matchingId || !session.PlayerId.HasValue) continue;

                    // roster = 현재 살아있는 전체 플레이어 → 타겟 제외 후보 전원(presence 0 포함)
                    var scored = _presenceTracker.GetCandidates(
                        matchingId, session.PlayerId.Value, session.TargetPlayerId, playerAreas.Keys);

                    var candidates = new List<(long playerId, float presence, string name, List<int> wear)>();
                    foreach (var (candidateId, presence) in scored)
                    {
                        string name = "";
                        List<int> wear = null;
                        // 봇은 서버 메모리에 정체성 보유 → 후보가 멀리 있어도 카드 채움. 인간은 빈값(클라가 폴백).
                        if (BotPlayerManager.IsBotPlayerId(candidateId))
                        {
                            var info = _botPlayerManager.SynthesizePlayerInfo(matchingId, candidateId);
                            if (info != null) { name = info.Name; wear = info.WearItemIdList; }
                        }

                        candidates.Add((candidateId, presence, name, wear));
                    }

                    session.SendPresenceUpdate(candidates);

                    var notebookRecords = _presenceTracker.GetNotebookRecords(
                        matchingId,
                        session.PlayerId.Value,
                        playerAreas.Keys,
                        includeEmpty: true);
                    session.SendPresenceNotebookUpdate(matchingId, roundNumber, notebookRecords);
                }
            }
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "자원 틱 처리 중 오류");
        }
    }

    /// <summary>프로토 0 기척: 인스턴스의 모든 플레이어(인간 + 생존 봇) 영역 맵.</summary>
    private Dictionary<long, AreaType> BuildPlayerAreas(long matchingId, List<GameClientSession> activeSessions)
    {
        var areas = new Dictionary<long, AreaType>();
        foreach (var session in activeSessions)
            if (session.CurrentMapSubId == matchingId && session.PlayerId.HasValue)
                areas[session.PlayerId.Value] = session.CurrentArea;

        if (_botPlayerManager.HasBots(matchingId))
            foreach (var bot in _botPlayerManager.GetBots(matchingId))
                if (!bot.IsEliminated)
                    areas[bot.PlayerId] = bot.CurrentArea;

        return areas;
    }

    /// <summary>프로토 0: 특정 영역의 총 인원(인간 + 봇). 회복 2/N 스케일링용.</summary>
    private int CountAreaPopulation(List<GameClientSession> sessions, long matchingId, AreaType area)
    {
        int humans = sessions.Count(s => s.CurrentMapSubId == matchingId && s.CurrentArea == area);
        return humans + _botPlayerManager.CountBotsInArea(matchingId, area);
    }

    private void ProcessBotTargetProximityChecklistProgress(
        long matchingId,
        List<GameClientSession> activeSessions,
        float deltaSeconds)
    {
        foreach (var bot in _botPlayerManager.GetBots(matchingId))
        {
            if (bot.IsEliminated || bot.TargetPlayerId == 0 || bot.CurrentArea == AreaType.None)
                continue;

            var targetSession = activeSessions.FirstOrDefault(s =>
                s.PlayerId == bot.TargetPlayerId &&
                s.CurrentMapSubId == matchingId &&
                !s.IsEliminated);
            var targetBot = targetSession == null
                ? _botPlayerManager.GetBot(matchingId, bot.TargetPlayerId)
                : null;
            if (targetSession == null && targetBot is not { IsEliminated: false })
                continue;

            bool sameArea = targetSession?.CurrentArea == bot.CurrentArea || targetBot?.CurrentArea == bot.CurrentArea;
            if (!sameArea || !IsBotTargetWithinProximity(bot, targetSession, targetBot))
                continue;

            var progress = _checklistManager.AdvanceActiveTaskProgress(
                matchingId,
                bot.PlayerId,
                "MANITTO_STAY_NEAR_TARGET_20",
                deltaSeconds);
            if (progress.Completion?.CompletedTask != null)
            {
                logger.LogInformation(
                    "Bot proximity checklist completed: MatchingId={MatchingId}, BotId={BotId}, TaskId={TaskId}",
                    matchingId, bot.PlayerId, progress.Completion.CompletedTask.TaskId);
            }

            TryPlaceBotGiftNearTarget(matchingId, bot);
        }
    }

    private bool TryPlaceBotGiftNearTarget(long matchingId, BotPlayerState bot)
    {
        bool hasGiftTask = _checklistManager.GetActiveTasks(matchingId, bot.PlayerId)
            .Any(task => task.TaskKey.Equals("MANITTO_TARGET_DISCOVERS_GIFT", StringComparison.OrdinalIgnoreCase));
        if (!hasGiftTask) return false;
        if (_missionManager.TryGetNextPlacedGift(matchingId, bot.PlayerId, bot.TargetPlayerId, out _))
            return false;

        var giftItem = _inGameInventoryManager.GetAllItems(matchingId, bot.PlayerId)
            .Where(item => item.Count > 0 &&
                           item.ItemId != 401000003 &&
                           GameItemData.GetItemType(item.ItemId) == ItemType.CONSUMABLE)
            .OrderBy(_ => Random.Shared.Next())
            .FirstOrDefault();
        if (giftItem == null) return false;

        var interact = GameInteractableData.GetByZone((int)bot.CurrentArea)
            .Where(info => info.CellX != 0 || info.CellY != 0)
            .OrderBy(_ => Random.Shared.Next())
            .FirstOrDefault();
        if (interact == null) return false;

        var placeResult = _missionManager.TryPlaceGift(
            matchingId,
            bot.PlayerId,
            bot.TargetPlayerId,
            giftItem.ItemUid,
            giftItem.ItemId,
            bot.CurrentArea,
            interact.Id);
        if (!placeResult.Success) return false;

        if (!_inGameInventoryManager.TryRemoveItem(matchingId, bot.PlayerId, giftItem.ItemUid, 1, out _))
        {
            _missionManager.RollbackPlacedGift(matchingId, bot.PlayerId, giftItem.ItemUid);
            return false;
        }

        RngCollectCooldownStore.ClearCooldown(matchingId, interact.Id);
        BroadcastBotRngCooldowns(matchingId, [(interact.Id, 0)]);
        logger.LogInformation(
            "Bot gift placed: MatchingId={MatchingId}, BotId={BotId}, Target={Target}, ItemId={ItemId}, InteractId={InteractId}",
            matchingId, bot.PlayerId, bot.TargetPlayerId, giftItem.ItemId, interact.Id);
        return true;
    }

    /// <summary>교감(#161) 판정 — 타겟(사람/봇)과의 평면 거리가 TARGET_PROXIMITY_DISTANCE 이내인지.</summary>
    private static bool IsTargetWithinProximity(
        GameClientSession session, GameClientSession? targetSession, BotPlayerState? targetBot)
    {
        var myPosition = session.LastValidatedPosition;
        if (myPosition == null) return false;

        var targetPosition = targetSession?.LastValidatedPosition ?? targetBot?.Position;
        if (targetPosition == null) return false;

        float dx = myPosition.X - targetPosition.X;
        float dy = myPosition.Y - targetPosition.Y;
        return dx * dx + dy * dy <=
               Config.TARGET_PROXIMITY_DISTANCE * Config.TARGET_PROXIMITY_DISTANCE;
    }

    private static bool IsBotTargetWithinProximity(
        BotPlayerState bot, GameClientSession? targetSession, BotPlayerState? targetBot)
    {
        var targetPosition = targetSession?.LastValidatedPosition ?? targetBot?.Position;
        if (targetPosition == null) return false;

        float dx = bot.Position.X - targetPosition.X;
        float dy = bot.Position.Y - targetPosition.Y;
        return dx * dx + dy * dy <=
               Config.TARGET_PROXIMITY_DISTANCE * Config.TARGET_PROXIMITY_DISTANCE;
    }

    internal static int ResolveStatusEffectCorruptionDelta(int statusEffectId, int value)
    {
        if (value == 0) return 0;
        if (!GameStatusEffectData.TryGet(statusEffectId, out var statusEffect) || statusEffect.BuffId <= 0)
            return 0;

        var buff = GameBuffData.Get(statusEffect.BuffId);
        int magnitude = Math.Abs(value);

        return buff.SubType switch
        {
            BuffSubType.CORRUPTION_ADD => magnitude,
            BuffSubType.CORRUPTION_DOWN => -magnitude,
            _ => 0
        };
    }

    private int ApplyTargetEncounterStability(GameClientSession session, int baseRecoveryDelta)
    {
        if (!session.PlayerId.HasValue || baseRecoveryDelta >= 0) return baseRecoveryDelta;
        if (!_missionManager.TryConsumeShortRewardUse(
                session.CurrentMapSubId,
                session.PlayerId.Value,
                MissionShortRewardType.TargetEncounterStability,
                out var reward) || reward == null)
        {
            return baseRecoveryDelta;
        }

        int bonusRecovery = Math.Max(1, (int)Math.Ceiling(Math.Abs(baseRecoveryDelta) * reward.ValuePercent / 100.0));
        int adjustedDelta = baseRecoveryDelta - bonusRecovery;

        logger.LogInformation(
            "타겟 조우 안정 적용: PlayerId={PlayerId}, BaseRecovery={BaseRecovery}, AdjustedRecovery={AdjustedRecovery}, RemainingUses={RemainingUses}",
            session.PlayerId, baseRecoveryDelta, adjustedDelta, reward.RemainingUses);

        return adjustedDelta;
    }

    private int ApplySharpGazeRecoveryPenalty(
        GameClientSession session,
        GameClientSession? targetSession,
        BotPlayerState? targetBot,
        int baseRecoveryDelta)
    {
        long? playerIdValue = session.PlayerId;
        long? targetPlayerIdValue = targetSession?.PlayerId;
        if (!targetPlayerIdValue.HasValue && targetBot != null) targetPlayerIdValue = targetBot.PlayerId;
        long targetBookmarkPlayerId = targetSession?.PresenceBookmarkPlayerId ?? targetBot?.PresenceBookmarkPlayerId ?? 0;
        if (!playerIdValue.HasValue || baseRecoveryDelta >= 0) return baseRecoveryDelta;
        if (!targetPlayerIdValue.HasValue) return baseRecoveryDelta;

        long playerId = playerIdValue.Value;
        long targetPlayerId = targetPlayerIdValue.Value;
        if (session.TargetPlayerId != targetPlayerId) return baseRecoveryDelta;
        if (targetBookmarkPlayerId != playerId) return baseRecoveryDelta;

        var targetManitto = _manittoChainManager.FindManittoOf(session.CurrentMapSubId, targetPlayerId);
        if (targetManitto?.PlayerId != playerId) return baseRecoveryDelta;

        int recovery = Math.Abs(baseRecoveryDelta);
        int adjustedRecovery = Math.Max(1, (int)Math.Ceiling(recovery * SharpGazeRecoveryMultiplier));
        return -adjustedRecovery;
    }

    private int ApplyClosedAreaResistance(GameClientSession session, int basePenalty)
    {
        if (!session.PlayerId.HasValue || basePenalty <= 0) return basePenalty;

        if (!_missionManager.TryGetActiveShortReward(
                session.CurrentMapSubId,
                session.PlayerId.Value,
                MissionShortRewardType.ClosedAreaResistance,
                out var reward) || reward == null)
        {
            if (!_missionManager.TryActivateTimedShortReward(
                    session.CurrentMapSubId,
                    session.PlayerId.Value,
                    MissionShortRewardType.ClosedAreaResistance,
                    out reward) || reward == null)
            {
                return basePenalty;
            }

            logger.LogInformation(
                "폐쇄구역 대응 발동: PlayerId={PlayerId}, DurationSeconds={DurationSeconds}, ExpiresAt={ExpiresAt}",
                session.PlayerId, reward.DurationSeconds, reward.ExpiresAt);
        }

        int reduction = Math.Max(1, (int)Math.Ceiling(basePenalty * reward.ValuePercent / 100.0));
        int adjustedPenalty = Math.Max(0, basePenalty - reduction);

        logger.LogInformation(
            "폐쇄구역 대응 적용: PlayerId={PlayerId}, BasePenalty={BasePenalty}, AdjustedPenalty={AdjustedPenalty}",
            session.PlayerId, basePenalty, adjustedPenalty);

        return adjustedPenalty;
    }

    /// <summary>
    ///     #26: 봇 자원 고갈 탈락 시 체인 단절 처리 + 게임 종료 판정.
    ///     ManittoChainManager.EliminatePlayer로 체인 단절 (마니또 시한부 / 타겟 해방 등) 일괄 적용.
    /// </summary>
    private void ProcessBotElimination(long matchingId, long botId, EliminationReason reason,
        List<GameClientSession> activeSessions)
    {
        try
        {
            var affected = _manittoChainManager.EliminatePlayer(matchingId, botId, reason);
            var matchingSessions = _clientSessions.Values
                .Where(s => s.PlayerId.HasValue && s.CurrentMapSubId == matchingId)
                .ToList();

            // 1) 전체에게 봇 탈락 알림 (G_TO_C_PLAYER_ELIMINATED)
            using (var eliminatedPacket = Packet.Create((int)Protocol.G_TO_C_PLAYER_ELIMINATED))
            {
                var eliminatedMsg = new G_TO_C_PLAYER_ELIMINATED { PlayerId = botId, Reason = reason };
                eliminatedPacket.SetBody(MessagePackSerializer.Serialize(eliminatedMsg));
                foreach (var s in matchingSessions) s.Send(eliminatedPacket);
            }

            // 2) 영향받는 봇/세션 상태 동기화 + 체인 단절 알림
            foreach (var (affectedId, newStatus) in affected)
            {
                var session = matchingSessions.FirstOrDefault(s => s.PlayerId == affectedId);
                if (session != null)
                {
                    session.ApplyChainBreakStatus(newStatus, botId);
                    continue;
                }

                // 봇 영향
                var bot = _botPlayerManager.GetBot(matchingId, affectedId);
                if (bot == null) continue;
                if (newStatus == ManittoStatus.ELIMINATED)
                {
                    bot.IsEliminated = true;
                    bot.ManittoStatus = ManittoStatus.SPECTATING;
                }
                else
                {
                    bot.ManittoStatus = newStatus;
                }
            }

            foreach (var (affectedId, newStatus) in affected)
            {
                if (newStatus != ManittoStatus.TERMINAL) continue;
                _missionManager.NotifyTargetLost(matchingId, affectedId, botId, reason);
            }

            // 3) 게임 종료 판정 — 봇 탈락으로 최후 1인 결정 가능
            var (isGameOver, winnerId) = _manittoChainManager.CheckGameOver(matchingId);
            if (isGameOver && matchingSessions.Count > 0)
            {
                logger.LogInformation("게임 종료(봇 탈락 후): MatchingId={MatchingId}, Winner={WinnerId}",
                    matchingId, winnerId);
                matchingSessions[0].EndGameByBotRaceCompletion(winnerId ?? 0);
            }
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "봇 탈락 처리 중 오류: BotId={BotId}", botId);
        }
    }

    /// <summary>
    ///     #26: 봇 미션 시뮬 — 부품 회수 + 자동 결합. 최종 결합 시 즉시 게임 종료.
    /// </summary>
    private void ProcessBotMissionForMatching(long matchingId, List<GameClientSession> activeSessions)
    {
        try
        {
            var missionResult = _botPlayerManager.ProcessBotMissionTick(
                matchingId, _missionManager, _inGameInventoryManager, _itemPoolManager, _checklistManager);

            // 운영툴 진행 로그 — 봇 부품 회수/선행/결합 이벤트
            foreach (var (botId, partId) in missionResult.CollectedParts)
            {
                var part = GameMissionData.GetPart(partId);
                _gameEventLogManager.LogMission(matchingId, botId,
                    $"부품 회수: {part?.PartNameKr ?? partId.ToString()}", isBot: true);
            }
            foreach (var (botId, shareGroup) in missionResult.CollectedPrereqs)
                _gameEventLogManager.LogMission(matchingId, botId,
                    $"선행 아이템 회수 (그룹 {shareGroup})", isBot: true);
            foreach (var (botId, outputPartId, isRace) in missionResult.Combined)
            {
                var part = GameMissionData.GetPart(outputPartId);
                _gameEventLogManager.LogMission(matchingId, botId,
                    isRace
                        ? $"최종 결합 완성! ({part?.PartNameKr ?? outputPartId.ToString()}) — race 완주"
                        : $"부품 결합: {part?.PartNameKr ?? outputPartId.ToString()}",
                    isBot: true);
            }

            // #134 — 봇 RNG progress 시작/종료 → 같은 영역 인간 세션에 EXPLORE_START/END (봇 EXPLORE_1 애니 동기화)
            foreach (var (botId, taskId, interactId, area) in missionResult.StartedChecklistActivities)
            {
                var task = GameChecklistData.GetTask(taskId);
                _gameEventLogManager.LogSchoolActivityStart(matchingId, botId, taskId,
                    area.ToString(), interactId, task?.TitleKr ?? "", isBot: true);
            }

            foreach (var (botId, taskId, interactId, area, awardedScore, awardedContribution) in missionResult.CompletedChecklistActivities)
            {
                var task = GameChecklistData.GetTask(taskId);
                _gameEventLogManager.LogSchoolActivityComplete(matchingId, botId, taskId,
                    area.ToString(), interactId, awardedScore, awardedContribution,
                    task?.TitleKr ?? "", isBot: true);
            }

            if (missionResult.BotExploreStarts.Count > 0)
                BroadcastBotExploreStarts(matchingId, missionResult.BotExploreStarts, activeSessions);
            if (missionResult.BotExploreEnds.Count > 0)
            {
                BroadcastBotExploreEnds(matchingId, missionResult.BotExploreEnds, activeSessions);
                ResolvePendingRoomDiscoveriesForBotExploreEnds(matchingId, missionResult.BotExploreEnds,
                    activeSessions);
            }
            if (missionResult.BotRestStarts.Count > 0)
                BroadcastBotPlayerStates(matchingId, missionResult.BotRestStarts,
                    global::network.common.PlayerState.SLEEP, activeSessions);
            if (missionResult.BotRestEnds.Count > 0)
                BroadcastBotPlayerStates(matchingId, missionResult.BotRestEnds,
                    global::network.common.PlayerState.IDLE, activeSessions);

            // #134 — 봇 RNG 채집으로 발생한 인스턴스 쿨타임 broadcast
            if (missionResult.RngCooldownBroadcasts.Count > 0)
                BroadcastBotRngCooldowns(matchingId, missionResult.RngCooldownBroadcasts);

            if (missionResult.GiftDiscoveries.Count > 0)
                SendBotGiftProgress(matchingId, missionResult.GiftDiscoveries, activeSessions);

            // race 완주 발생 — 즉시 게임 종료 처리 (#87 정합)
            if (missionResult.RaceWinnerPlayerId == 0) return;

            long winnerPlayerId = missionResult.RaceWinnerPlayerId;
            var sessions = _clientSessions.Values
                .Where(s => s.PlayerId.HasValue && s.CurrentMapSubId == matchingId)
                .ToList();
            if (sessions.Count == 0) return;

            logger.LogInformation("race 완주: MatchingId={MatchingId}, WinnerId={Winner}", matchingId, winnerPlayerId);

            // 임의 세션을 통해 게임 종료 트리거
            sessions[0].EndGameByBotRaceCompletion(winnerPlayerId);
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "봇 미션 틱 처리 중 오류: MatchingId={MatchingId}", matchingId);
        }
    }

    /// <summary>
    ///     #26: 시한부 봇 사보타주 + 색출 시뮬.
    /// </summary>
    private void ProcessBotTerminalActionsForMatching(long matchingId, List<GameClientSession> activeSessions)
    {
        try
        {
            // 1) 시한부 봇 사보타주 — 살아있는 사람 PlayerId(세션 + 다른 봇) 후보 목록
            var sessionPlayerIds = activeSessions
                .Where(s => s.CurrentMapSubId == matchingId && s.PlayerId.HasValue)
                .Select(s => s.PlayerId!.Value);
            var aliveBotIds = _botPlayerManager.GetBots(matchingId)
                .Where(b => !b.IsEliminated)
                .Select(b => b.PlayerId);
            var aliveCandidates = sessionPlayerIds.Concat(aliveBotIds).Distinct().ToList();

            var sabotaged = _botPlayerManager.ProcessTerminalSabotage(matchingId, _missionManager, aliveCandidates);
            foreach (var (botId, victim, partId) in sabotaged)
            {
                if (!partId.HasValue) continue;
                var part = GameMissionData.GetPart(partId.Value);
                var victimSession = _clientSessions.Values.FirstOrDefault(s => s.PlayerId == victim);
                if (victimSession == null) continue;

                using var packet = Packet.Create(
                    (int)Protocol.G_TO_C_PART_INVALIDATED, victim);
                var msg = new G_TO_C_PART_INVALIDATED
                {
                    PartId = partId.Value,
                    PartNameKr = part?.PartNameKr ?? "",
                    PartTier = part != null ? (int)part.PartTier : 0,
                    TargetPlayerId = victim
                };
                packet.SetBody(MessagePackSerializer.Serialize(msg));
                victimSession.Send(packet);
                logger.LogInformation("봇 사보타주 알림 송신: VictimSession={V}, PartId={Part}", victim, partId.Value);
            }

            // 2) 색출 시도 — 봇별로 자기 마니또(자기를 타겟으로 가진 사람) 후보 추리
            var attempts = _botPlayerManager.CollectDetectionAttempts(matchingId, botId =>
            {
                // 봇의 마니또 = 봇을 TargetPlayerId로 가진 링크 (봇/세션 모두 가능)
                var manittoLink = _manittoChainManager.GetLink(matchingId, botId);
                if (manittoLink == null) return null;
                // ManittoChainManager 내부에서 자기 마니또(=자기를 타겟으로 가진) 찾기
                // RegisterLink로 모든 링크 등록되어 있으므로 검색 가능
                return FindManittoOf(matchingId, botId);
            });

            foreach (var (detecterBotId, candidate) in attempts)
            {
                var (isCorrect, _) = _manittoChainManager.TryDetect(matchingId, detecterBotId, candidate);
                logger.LogInformation("봇 색출 결과: BotId={B}, Cand={C}, Correct={R}",
                    detecterBotId, candidate, isCorrect);

                BroadcastDetectionAnnounce(matchingId, activeSessions, detecterBotId, candidate, isCorrect);

                if (!isCorrect) continue;

                // 적중 — 부품 전이 + 마니또 탈락
                int? stolen = _missionManager.StealHighestPart(matchingId, candidate, detecterBotId);
                _traceManager.InvalidateTracesByPlacer(matchingId, candidate);
                logger.LogInformation("봇 색출 적중: BotId={B}, Manitto={M}, StolenPart={P}",
                    detecterBotId, candidate, stolen);

                // 마니또 탈락 — 세션 중 임의를 통해 ProcessElimination
                var anySession = activeSessions.FirstOrDefault(s => s.CurrentMapSubId == matchingId);
                anySession?.ProcessBotDetectedElimination(candidate, detecterBotId);
            }
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "봇 시한부/색출 처리 중 오류: MatchingId={MatchingId}", matchingId);
        }
    }

    /// <summary>
    ///     색출 시도를 매칭 내 모든 활성 세션에 브로드캐스트 — 영상 cut 시각화용.
    ///     봇 detecter는 ChainLink로 직책 조회. 시도 결과(isCorrect) 포함.
    /// </summary>
    private void BroadcastDetectionAnnounce(long matchingId, List<GameClientSession> activeSessions,
        long detecterId, long targetId, bool isCorrect)
    {
        var detecterLink = _manittoChainManager.GetLink(matchingId, detecterId);
        var targetLink = _manittoChainManager.GetLink(matchingId, targetId);
        if (detecterLink == null || targetLink == null) return;

        var msg = new G_TO_C_DETECTION_ANNOUNCE
        {
            DetecterPlayerId = detecterId,
            DetecterJobTitle = detecterLink.MyJobTitle,
            TargetPlayerId = targetId,
            TargetJobTitle = targetLink.MyJobTitle,
            IsCorrect = isCorrect
        };
        byte[] body = MessagePackSerializer.Serialize(msg);

        foreach (var session in activeSessions)
        {
            if (session.CurrentMapSubId != matchingId) continue;
            if (session.IsEliminated) continue;
            using var packet = Packet.Create((int)Protocol.G_TO_C_DETECTION_ANNOUNCE);
            packet.SetBody(body);
            session.Send(packet);
        }

        logger.LogInformation("색출 broadcast: Detecter={D}({DJ}), Target={T}({TJ}), Correct={R}",
            detecterId, detecterLink.MyJobTitle, targetId, targetLink.MyJobTitle, isCorrect);
    }

    /// <summary>
    ///     흔적 배치를 매칭 내 모든 활성 세션에 브로드캐스트 — 영상 cut 시각화용.
    ///     봇 placer는 ChainLink로 직책 조회. 발견자 본인 효과는 기존 TRACE_CREATED 흐름 유지(본 패킷은 cut 신호만).
    /// </summary>
    private void BroadcastTracePlacedAnnounce(long matchingId, List<GameClientSession> activeSessions,
        (long placerPlayerId, AreaType area, int interactId, string description) trace)
    {
        var placerLink = _manittoChainManager.GetLink(matchingId, trace.placerPlayerId);
        if (placerLink == null) return;

        var msg = new G_TO_C_TRACE_PLACED_ANNOUNCE
        {
            PlacerPlayerId = trace.placerPlayerId,
            PlacerJobTitle = placerLink.MyJobTitle,
            AreaType = trace.area,
            InteractId = trace.interactId,
            Description = trace.description
        };
        byte[] body = MessagePackSerializer.Serialize(msg);

        foreach (var session in activeSessions)
        {
            if (session.CurrentMapSubId != matchingId) continue;
            if (session.IsEliminated) continue;
            using var packet = Packet.Create((int)Protocol.G_TO_C_TRACE_PLACED_ANNOUNCE);
            packet.SetBody(body);
            session.Send(packet);
        }

        logger.LogInformation("흔적 배치 broadcast: Placer={P}({J}), Area={A}, InteractId={I}",
            trace.placerPlayerId, placerLink.MyJobTitle, trace.area, trace.interactId);
    }

    /// <summary>
    ///     #134 — 봇 RNG progress 시작을 같은 영역 인간 세션에 G_TO_C_EXPLORE_START broadcast.
    ///     클라가 봇 캐릭터를 EXPLORE_1 상태로 설정 → 탐색 애니메이션 + 사운드 자동 재생.
    /// </summary>
    private void BroadcastBotExploreStarts(long matchingId,
        List<(long botId, int interactId, AreaType area)> starts,
        List<GameClientSession> activeSessions)
    {
        foreach (var (botId, interactId, area) in starts)
        {
            var sameAreaSessions = activeSessions
                .Where(s => s.PlayerId.HasValue && s.CurrentMapSubId == matchingId && s.CurrentArea == area)
                .ToList();
            if (sameAreaSessions.Count == 0) continue;

            var msg = new G_TO_C_EXPLORE_START { PlayerId = botId, InteractId = interactId };
            var body = MessagePackSerializer.Serialize(msg);
            foreach (var session in sameAreaSessions)
            {
                using var packet = Packet.Create((int)Protocol.G_TO_C_EXPLORE_START, session.PlayerId!.Value);
                packet.SetBody(body);
                session.Send(packet);
            }
        }
    }

    /// <summary>
    ///     #134 — 봇 RNG progress 종료 broadcast. 클라가 봇 캐릭터 EXPLORE_1 → IDLE 복귀.
    /// </summary>
    private void BroadcastBotExploreEnds(long matchingId,
        List<(long botId, AreaType area)> ends, List<GameClientSession> activeSessions)
    {
        foreach (var (botId, area) in ends)
        {
            var sameAreaSessions = activeSessions
                .Where(s => s.PlayerId.HasValue && s.CurrentMapSubId == matchingId && s.CurrentArea == area)
                .ToList();
            if (sameAreaSessions.Count == 0) continue;

            var msg = new G_TO_C_EXPLORE_END { PlayerId = botId };
            var body = MessagePackSerializer.Serialize(msg);
            foreach (var session in sameAreaSessions)
            {
                using var packet = Packet.Create((int)Protocol.G_TO_C_EXPLORE_END, session.PlayerId!.Value);
                packet.SetBody(body);
                session.Send(packet);
            }
        }
    }

    private void ResolvePendingRoomDiscoveriesForBotExploreEnds(long matchingId,
        List<(long botId, AreaType area)> ends,
        List<GameClientSession> activeSessions)
    {
        // Room discovery now resolves through the discoverer's server-side decision timer.
    }

    private void BroadcastBotPlayerStates(long matchingId,
        List<(long botId, AreaType area)> states, global::network.common.PlayerState playerState,
        List<GameClientSession> activeSessions)
    {
        foreach (var (botId, area) in states)
        {
            var sameAreaSessions = activeSessions
                .Where(s => s.PlayerId.HasValue && s.CurrentMapSubId == matchingId && s.CurrentArea == area)
                .ToList();
            if (sameAreaSessions.Count == 0) continue;

            using var packet = PacketMaker.G_TO_C_PLAYER_STATE(botId, playerState);
            foreach (var session in sameAreaSessions)
                session.Send(packet);
        }
    }

    private void SendBotGiftProgress(long matchingId, List<GiftDiscoveryResult> discoveries,
        List<GameClientSession> activeSessions)
    {
        foreach (var discovery in discoveries)
        {
            var ownerSession = activeSessions.FirstOrDefault(s =>
                s.PlayerId == discovery.OwnerPlayerId && s.CurrentMapSubId == matchingId && !s.IsEliminated);
            if (ownerSession == null)
            {
                if (BotPlayerManager.IsBotPlayerId(discovery.OwnerPlayerId) &&
                    _checklistManager.TryCompleteTargetGiftTask(matchingId, discovery.OwnerPlayerId))
                {
                    logger.LogInformation(
                        "Bot target gift checklist completed: MatchingId={MatchingId}, BotId={BotId}, Discoverer={Discoverer}",
                        matchingId, discovery.OwnerPlayerId, discovery.DiscovererPlayerId);
                }

                SendBotToNextPlacedGift(matchingId, discovery);
                continue;
            }

            ownerSession.CompleteTargetGiftChecklist();

            var msg = new G_TO_C_GIFT_PROGRESS
            {
                DeliveredCount = discovery.DeliveredCount,
                RequiredCount = discovery.RequiredCount,
                FinalPartId = discovery.FinalPartId,
                IsRaceComplete = discovery.IsRaceComplete,
                InteractId = discovery.InteractId,
                AreaType = discovery.AreaType,
                HasPlacedGiftAtInteract = discovery.HasPlacedGiftAtInteract,
                HasPlacedGiftInArea = discovery.HasPlacedGiftInArea
            };
            var body = MessagePackSerializer.Serialize(msg);
            using var packet = Packet.Create((int)Protocol.G_TO_C_GIFT_PROGRESS, discovery.OwnerPlayerId);
            packet.SetBody(body);
            ownerSession.Send(packet);

            logger.LogInformation(
                "봇 선물 발견 진행도 전송: MatchingId={MatchingId}, Owner={Owner}, BotTarget={Bot}, Delivered={Delivered}/{Required}",
                matchingId, discovery.OwnerPlayerId, discovery.DiscovererPlayerId,
                discovery.DeliveredCount, discovery.RequiredCount);
            SendBotToNextPlacedGift(matchingId, discovery);
        }
    }

    private void SendBotToNextPlacedGift(long matchingId, GiftDiscoveryResult discovery)
    {
        if (!BotPlayerManager.IsBotPlayerId(discovery.DiscovererPlayerId)) return;
        if (!_missionManager.TryGetNextPlacedGift(
                matchingId,
                discovery.OwnerPlayerId,
                discovery.DiscovererPlayerId,
                out var nextGift) || nextGift == null)
            return;

        bool started = _botPlayerManager.TrySendBotToInteract(
            matchingId,
            discovery.DiscovererPlayerId,
            nextGift.AreaType,
            nextGift.InteractId,
            _areaClosureManager);

        if (started)
            logger.LogInformation(
                "봇 다음 선물 회수 이동: MatchingId={MatchingId}, BotId={BotId}, InteractId={InteractId}",
                matchingId, discovery.DiscovererPlayerId, nextGift.InteractId);
    }

    /// <summary>
    ///     #134 — 봇이 RNG 채집한 InteractObject 쿨타임을 같은 매칭 모든 클라에 broadcast.
    ///     플레이어 회수 시 GameClientSession.BroadcastRngCollectCooldown과 동일한 패킷.
    ///     결과 정보(직책 매칭 여부)는 포함 X — 노출 방지.
    /// </summary>
    private void BroadcastBotRngCooldowns(long matchingId, List<(int interactId, int cooldownSeconds)> broadcasts)
    {
        var sessions = _clientSessions.Values
            .Where(s => s.PlayerId.HasValue && s.CurrentMapSubId == matchingId)
            .ToList();
        if (sessions.Count == 0) return;

        foreach (var (interactId, cooldown) in broadcasts)
        {
            var msg = new G_TO_C_RNG_COLLECT_COOLDOWN_BROADCAST
            {
                InteractId = interactId,
                CooldownSeconds = cooldown
            };
            var body = MessagePackSerializer.Serialize(msg);
            foreach (var session in sessions)
            {
                using var packet = Packet.Create((int)Protocol.G_TO_C_RNG_COLLECT_COOLDOWN_BROADCAST,
                    session.PlayerId!.Value);
                packet.SetBody(body);
                session.Send(packet);
            }
            logger.LogInformation("봇 RNG 쿨타임 broadcast: MatchingId={Mid}, InteractId={Iid}, Cooldown={Sec}s",
                matchingId, interactId, cooldown);
        }
    }

    /// <summary>
    ///     #125: 봇 이동 이벤트를 같은 매칭의 영향권 인간 세션에 패킷 브로드캐스트.
    ///     - 영역 전환: G_TO_C_AREA_PLAYER_LEAVE(이전 영역) + G_TO_C_AREA_PLAYER_ENTER(새 영역) + G_TO_C_MOVE(텔레포트)
    ///     - 영역 내 wander: G_TO_C_MOVE(같은 영역)
    /// </summary>
    private void BroadcastBotMovement(long matchingId, BotMovementEvent ev,
        List<GameClientSession> activeSessions)
    {
        var matchingSessions = activeSessions
            .Where(s => s.PlayerId.HasValue && s.CurrentMapSubId == matchingId)
            .ToList();

        long serverTimestamp = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();

        if (ev.IsAreaTransition)
        {
            _presenceTracker.SetPlayerArea(matchingId, ev.BotPlayerId, ev.ToArea, countAsEntry: true);

            _gameEventLogManager.LogMove(matchingId, ev.BotPlayerId,
                ev.FromArea.ToString(), ev.ToArea.ToString(), isBot: true);

            if (matchingSessions.Count == 0) return;

            // 1) 이전 영역의 인간들에게 LEAVE
            using var leavePacket = PacketMaker.G_TO_C_AREA_PLAYER_LEAVE(ev.BotPlayerId);
            foreach (var session in matchingSessions)
            {
                if (session.CurrentArea != ev.FromArea) continue;
                session.Send(leavePacket);
            }

            // 2) 새 영역의 인간들에게 ENTER (합성 PlayerInfo)
            var botInfo = _botPlayerManager.SynthesizePlayerInfo(matchingId, ev.BotPlayerId);
            if (botInfo != null)
            {
                using var enterPacket = PacketMaker.G_TO_C_AREA_PLAYER_ENTER(botInfo, ev.ToCell);
                foreach (var session in matchingSessions)
                {
                    if (session.CurrentArea != ev.ToArea) continue;
                    session.Send(enterPacket);
                }
            }
        }
        else if (matchingSessions.Count == 0)
        {
            return;
        }

        // 3) 새 영역의 인간들에게 MOVE (텔레포트 또는 wander)
        using var movePacket = PacketMaker.G_TO_C_MOVE(
            ev.BotPlayerId,
            ev.Position,
            ev.Velocity,
            ev.Rotation,
            ev.ToCell,
            lastProcessedInput: 0u,
            serverTimestamp);
        foreach (var session in matchingSessions)
        {
            if (session.CurrentArea != ev.ToArea) continue;
            session.Send(movePacket);
        }

        foreach (var session in matchingSessions)
        {
            if (session.CurrentArea != ev.ToArea || session.TargetPlayerId != ev.BotPlayerId) continue;
            session.TryApplyImmediateTargetEncounterRecovery(matchingSessions);
        }

        TrySendBotCorridorEncounterEvent(matchingId, ev, matchingSessions);
    }

    private void TrySendBotCorridorEncounterEvent(
        long matchingId,
        BotMovementEvent ev,
        List<GameClientSession> matchingSessions)
    {
        if (!ev.ToArea.IsCorridor())
            return;

        var candidates = matchingSessions
            .Where(session =>
                session.PlayerId.HasValue &&
                !session.IsEliminated &&
                session.CurrentMapSubId == matchingId &&
                session.CurrentArea == ev.ToArea &&
                session.LastValidatedPosition != null)
            .Select(session => (session.PlayerId!.Value, session.LastValidatedPosition!))
            .ToList();
        if (candidates.Count == 0)
            return;

        var decision = _encounterRevealManager.ResolveCorridorEncounter(
            matchingId,
            ev.BotPlayerId,
            ev.Position,
            candidates.Select(entry => (entry.Item1, entry.Item2!)),
            PassiveBuffUtility.GetValuePercent(
                _botPlayerManager.GetBot(matchingId, ev.BotPlayerId)?.ActiveBuffIds ?? [],
                BuffSubType.RISK_EVENT_CHANCE_DOWN),
            PassiveBuffUtility.GetValuePercent(
                _botPlayerManager.GetBot(matchingId, ev.BotPlayerId)?.ActiveBuffIds ?? [],
                BuffSubType.ENCOUNTER_ESCAPE_CHANCE_ADD));
        if (!decision.HasEvent)
            return;

        var targetSession = matchingSessions.FirstOrDefault(session => session.PlayerId == decision.TargetPlayerId);
        if (targetSession == null)
            return;

        using var packet = PacketMaker.G_TO_C_ENCOUNTER_REVEAL(
            ev.BotPlayerId,
            ev.ToArea,
            decision.EventType,
            decision.CooldownSeconds,
            decision.RevealDelayMs);
        targetSession.Send(packet);

        logger.LogInformation(
            "Bot corridor encounter event: Matching={MatchingId}, Bot={Bot}, Target={Target}, Area={Area}, EventType={EventType}",
            matchingId,
            ev.BotPlayerId,
            decision.TargetPlayerId,
            ev.ToArea,
            decision.EventType);
    }

    /// <summary>
    ///     매칭 내에서 botId의 마니또(=botId를 타겟으로 가진 링크)를 찾는다.
    /// </summary>
    private long? FindManittoOf(long matchingId, long botId)
    {
        // ChainLink 직접 순회 — ManittoChainManager에 헬퍼가 없으므로 BuildGameResult 활용은 무거움.
        // Reflection 우회 대신 단순 GetLink 순회로 대체 — 매칭 인원 5명 안팎이라 비용 무시 가능.
        // 모든 PlayerId 후보(세션 + 봇)에서 TargetPlayerId == botId인 링크 찾기
        var sessionIds = _clientSessions.Values
            .Where(s => s.PlayerId.HasValue && s.CurrentMapSubId == matchingId)
            .Select(s => s.PlayerId!.Value);
        var botIds = _botPlayerManager.GetBots(matchingId).Select(b => b.PlayerId);
        foreach (long candidateId in sessionIds.Concat(botIds))
        {
            var link = _manittoChainManager.GetLink(matchingId, candidateId);
            if (link != null && link.TargetPlayerId == botId) return candidateId;
        }
        return null;
    }

    // ===== 구역 폐쇄 틱 =====

    private void StartAreaClosureTickTimer()
    {
        // 1초 간격 — 클라 카운트다운 종료 시점과 실제 폐쇄 트리거 사이 지연을 최소화
        _areaClosureTickTimer = new Timer(ProcessAreaClosureTick, null,
            TimeSpan.FromSeconds(1), TimeSpan.FromSeconds(1));
        logger.LogInformation("구역 폐쇄 타이머 시작 (1초 간격)");
    }

    private void ProcessAreaClosureTick(object? state)
    {
        try
        {
            // 매칭별로 폐쇄 스케줄 체크
            var matchingIds = GetActiveMatchingIds();

            foreach (long matchingId in matchingIds)
            {
                if (!GameClientSession.IsRoundActionPhase(matchingId)) continue;

                var (warningArea, warningSeconds, closureAtUnixMs, closingArea) =
                    _areaClosureManager.CheckClosureSchedule(matchingId);

                var sessions = _clientSessions.Values
                    .Where(s => s.PlayerId.HasValue && s.CurrentMapSubId == matchingId)
                    .ToList();

                // 경고 브로드캐스트
                if (warningArea.HasValue)
                {
                    using var packet = Packet.Create((int)Protocol.G_TO_C_AREA_CLOSURE_WARNING);
                    var msg = new G_TO_C_AREA_CLOSURE_WARNING
                    {
                        AreaType = warningArea.Value,
                        SecondsRemaining = warningSeconds,
                        ClosureAtUnixMs = closureAtUnixMs
                    };
                    packet.SetBody(MessagePackSerializer.Serialize(msg));
                    foreach (var s in sessions) s.Send(packet);
                }

                // 폐쇄 확정 브로드캐스트
                if (closingArea.HasValue)
                {
                    _gameEventLogManager.LogClosure(matchingId, ((AreaType)closingArea.Value).ToString());

                    using var packet = Packet.Create((int)Protocol.G_TO_C_AREA_CLOSED);
                    var msg = new G_TO_C_AREA_CLOSED { AreaType = closingArea.Value };
                    packet.SetBody(MessagePackSerializer.Serialize(msg));
                    foreach (var s in sessions) s.Send(packet);
                }
            }
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "구역 폐쇄 틱 처리 중 오류");
        }
    }

    // ===== 타겟 위치 전송 =====

    private void StartTargetLocationTimer()
    {
        _targetLocationTimer = new Timer(ProcessTargetLocation, null,
            TimeSpan.FromSeconds(1), TimeSpan.FromSeconds(1));
        logger.LogInformation("타겟 위치 전송 타이머 시작 (1초 간격)");
    }

    private void ProcessTargetLocation(object? state)
    {
        try
        {
            var activeSessions = _clientSessions.Values
                .Where(s => s.PlayerId.HasValue && s.TargetPlayerId != 0)
                .ToList();

            foreach (var session in activeSessions)
                session.SendTargetLocation();
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "타겟 위치 전송 처리 중 오류");
        }
    }

    // ===== #127 봇 walking 타이머 =====

    private const int BotMovementTickIntervalMs = 50; // 봇 walking step 주기 (실제 플레이어 sendInterval=50ms와 동등 — 클라 보간 일치)

    private void StartBotMovementTimer()
    {
        _botMovementTimer = new Timer(ProcessBotMovement, null,
            TimeSpan.FromMilliseconds(BotMovementTickIntervalMs),
            TimeSpan.FromMilliseconds(BotMovementTickIntervalMs));
        logger.LogInformation("봇 walking 타이머 시작 ({Ms}ms 간격)", BotMovementTickIntervalMs);
    }

    private const int BotMissionTickIntervalMs = 1000; // #134 봇 미션 처리(RNG 채집/결합) 주기

    private void StartBotMissionTimer()
    {
        _botMissionTimer = new Timer(ProcessBotMission, null,
            TimeSpan.FromMilliseconds(BotMissionTickIntervalMs),
            TimeSpan.FromMilliseconds(BotMissionTickIntervalMs));
        logger.LogInformation("봇 미션 타이머 시작 ({Ms}ms 간격)", BotMissionTickIntervalMs);
    }

    private void ProcessBotMission(object? state)
    {
        try
        {
            var activeSessions = _clientSessions.Values
                .Where(s => s.PlayerId.HasValue)
                .ToList();
            var matchingIds = GetActiveMatchingIds();

            foreach (long matchingId in matchingIds)
            {
                if (!GameClientSession.IsRoundActionPhase(matchingId)) continue;
                if (!_botPlayerManager.HasBots(matchingId)) continue;
                ProcessBotMissionForMatching(matchingId, activeSessions);
            }
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "봇 미션 틱 처리 중 오류");
        }
    }

    private void ProcessBotMovement(object? state)
    {
        if (System.Threading.Interlocked.Exchange(ref _botMovementProcessing, 1) == 1)
            return;

        try
        {
            var activeSessions = _clientSessions.Values
                .Where(s => s.PlayerId.HasValue)
                .ToList();
            var matchingIds = GetActiveMatchingIds();

            foreach (long matchingId in matchingIds)
            {
                if (!GameClientSession.IsRoundActionPhase(matchingId)) continue;
                if (!_botPlayerManager.HasBots(matchingId)) continue;
                // 프로토 0: 봇 타겟 추적/떠보기를 위해 같은 매칭 인간 플레이어의 현재 영역을 넘긴다.
                var humanAreas = activeSessions
                    .Where(s => s.CurrentMapSubId == matchingId && s.PlayerId.HasValue)
                    .ToDictionary(s => s.PlayerId!.Value, s => s.CurrentArea);
                var movementResult = _botPlayerManager.ProcessBotMovementTick(
                    matchingId, _areaClosureManager, humanAreas, _checklistManager);
                foreach (var ev in movementResult.Movements)
                    BroadcastBotMovement(matchingId, ev, activeSessions);
                if (movementResult.ExploreEnds.Count > 0)
                {
                    BroadcastBotExploreEnds(matchingId, movementResult.ExploreEnds, activeSessions);
                    ResolvePendingRoomDiscoveriesForBotExploreEnds(matchingId, movementResult.ExploreEnds,
                        activeSessions);
                }
                StartTargetBotInterrogations(matchingId, activeSessions);
            }
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "봇 walking 틱 처리 중 오류");
        }
        finally
        {
            System.Threading.Volatile.Write(ref _botMovementProcessing, 0);
        }
    }

    private static readonly TimeSpan TargetBotInterrogationDelay = TimeSpan.FromSeconds(3);
    private static readonly TimeSpan BotToBotStatementHold = TimeSpan.FromSeconds(2);

    private void StartTargetBotInterrogations(long matchingId, List<GameClientSession> activeSessions)
    {
        var now = DateTime.UtcNow;
        var matchingSessions = activeSessions
            .Where(s => s.CurrentMapSubId == matchingId)
            .ToList();

        var bots = _botPlayerManager.GetBots(matchingId)
            .Where(b => !b.IsEliminated)
            .ToList();

        foreach (var bot in bots)
        {
            long guardedPlayerId = bot.PresenceBookmarkPlayerId;
            if (guardedPlayerId == 0 || guardedPlayerId == bot.PlayerId)
            {
                PruneGuardedBotEncounter(bot, guardedPlayerId, false);
                continue;
            }

            if (bot.IsInInteraction || bot.CurrentArea == AreaType.None)
                continue;

            var targetSession = matchingSessions.FirstOrDefault(s => s.PlayerId == guardedPlayerId);
            var targetBot = targetSession == null
                ? bots.FirstOrDefault(b => b.PlayerId == guardedPlayerId)
                : null;

            bool targetSameArea =
                targetSession != null &&
                !targetSession.IsEliminated &&
                targetSession.CurrentArea == bot.CurrentArea;
            if (!targetSameArea)
                targetSameArea =
                    targetBot is { IsEliminated: false } &&
                    !targetBot.IsInInteraction &&
                    targetBot.CurrentArea == bot.CurrentArea;

            PruneGuardedBotEncounter(bot, guardedPlayerId, targetSameArea);
            if (!targetSameArea) continue;
            if (bot.TargetInterrogationRequestedInEncounterPlayerIds.Contains(guardedPlayerId)) continue;

            if (!bot.TargetEncounterStartedAtByPlayerId.TryGetValue(guardedPlayerId, out var encounterStartedAt))
            {
                bot.TargetEncounterStartedAtByPlayerId[guardedPlayerId] = now;
                continue;
            }

            if (now - encounterStartedAt < TargetBotInterrogationDelay) continue;

            bool started = targetSession != null
                ? targetSession.TryStartTargetBotInterrogation(bot)
                : targetBot != null && TryCreateBotToBotStatement(matchingId, bot, targetBot);
            if (!started) continue;

            bot.TargetEncounterStartedAtByPlayerId[guardedPlayerId] = now;
            bot.TargetInterrogationRequestedInEncounterPlayerIds.Add(guardedPlayerId);
        }
    }

    private static void PruneGuardedBotEncounter(BotPlayerState bot, long guardedPlayerId, bool targetSameArea)
    {
        foreach (long playerId in bot.TargetEncounterStartedAtByPlayerId.Keys.ToList())
            if (playerId != guardedPlayerId || !targetSameArea)
                bot.TargetEncounterStartedAtByPlayerId.Remove(playerId);

        foreach (long playerId in bot.TargetInterrogationRequestedInEncounterPlayerIds.ToList())
            if (playerId != guardedPlayerId || !targetSameArea)
                bot.TargetInterrogationRequestedInEncounterPlayerIds.Remove(playerId);
    }

    private bool TryCreateBotToBotStatement(long matchingId, BotPlayerState askerBot, BotPlayerState answererBot)
    {
        if (askerBot.PlayerId == answererBot.PlayerId) return false;
        if (askerBot.CurrentArea == AreaType.None || askerBot.CurrentArea != answererBot.CurrentArea) return false;

        var interactionChoiceService = new InteractionChoiceService(
            _interactionLogManager,
            _manittoChainManager,
            _gameEventLogManager);

        var questionSet = interactionChoiceService.GenerateQuestionSet(
            matchingId,
            askerBot.PlayerId,
            answererBot.PlayerId,
            askerBot.CurrentArea,
            null);
        var question = questionSet.Questions.FirstOrDefault(q => q.QuestionType == InteractionQuestionType.ASK_NEARBY_REASON)
                       ?? questionSet.Questions.FirstOrDefault();
        if (question == null) return false;

        var answerTask = ResolveBotAnswerTask(matchingId, answererBot);
        var answerSet = interactionChoiceService.GenerateAnswerSet(
            matchingId,
            answererBot.PlayerId,
            askerBot.PlayerId,
            question.QuestionType,
            answererBot.CurrentArea,
            questionSet.Contexts.FirstOrDefault(c => c.QuestionType == question.QuestionType),
            ResolveTaskArea(answerTask),
            answerTask?.TaskId ?? 0);
        if (answerSet.Answers.Count == 0) return false;

        int answerIndex = _botPlayerManager.PickAnswerIndex(answerSet.Answers.Count);
        answerIndex = Math.Clamp(answerIndex, 0, answerSet.Answers.Count - 1);
        var answerContext = answerSet.Contexts.ElementAtOrDefault(answerIndex);
        if (answerContext == null) return false;

        int roundId = GameClientSession.GetRoundSnapshot(matchingId)?.RoundNumber ?? 0;
        var statement = _gameEventLogManager.LogStatement(
            matchingId,
            roundId,
            answererBot.PlayerId,
            askerBot.PlayerId,
            answerContext.Area.ToString(),
            answerContext.QuestionId,
            answerContext.QuestionText,
            answerContext.AnswerType,
            answerContext.AnswerText,
            answerContext.LinkedLogIds,
            isBot: true);

        askerBot.HoldForInteraction(BotToBotStatementHold);
        answererBot.HoldForInteraction(BotToBotStatementHold);

        logger.LogInformation(
            "[BOT_STATEMENT] MatchingId={MatchingId}, Asker={Asker}, Answerer={Answerer}, AnswerType={AnswerType}, Description={Description}",
            matchingId, askerBot.PlayerId, answererBot.PlayerId, answerContext.AnswerType, statement.Description);
        return true;
    }

    private ChecklistTaskData? ResolveBotAnswerTask(long matchingId, BotPlayerState bot)
    {
        if (bot.PendingChecklistTaskId > 0)
        {
            var pendingTask = GameChecklistData.GetTask(bot.PendingChecklistTaskId);
            if (pendingTask != null)
                return pendingTask;
        }

        return _checklistManager.GetNextActiveGeneralInteractTask(matchingId, bot.PlayerId);
    }

    private static AreaType? ResolveTaskArea(ChecklistTaskData? task)
    {
        if (task?.AreaType > 0 && Enum.IsDefined(typeof(AreaType), task.AreaType))
            return (AreaType)task.AreaType;

        return null;
    }

    private void StartCorridorStopCheckTimer()
    {
        _corridorStopCheckTimer = new Timer(
            ProcessCorridorStopCheck,
            null,
            TimeSpan.FromMilliseconds(CorridorStopCheckIntervalMs),
            TimeSpan.FromMilliseconds(CorridorStopCheckIntervalMs));
        logger.LogInformation("Corridor stop check timer started (interval: {Interval}ms)",
            CorridorStopCheckIntervalMs);
    }

    /// <summary>
    ///     복도에서 정지한 플레이어들의 정신오염도 증가 처리 (규칙 6)
    /// </summary>
    private void ProcessCorridorStopCheck(object? state)
    {
        try
        {
            var violations = _corridorRuleManager.CheckAllStoppedPlayersForRule6();

            foreach ((long matchingId, long playerId, var result) in violations)
            {
                // 규칙 6번이 적용된 매칭인지 확인
                int corridorRuleId = _areaRuleManager.GetFirstCorridorRuleId(matchingId);
                if (corridorRuleId != 6) continue;

                if (_clientSessions.TryGetValue(playerId, out var session))
                {
                    session.ModifyStats(corruptionDelta: result.CorruptionDelta);
                    logger.LogInformation(
                        "Player {PlayerId} corridor stop violation (timer): {Message}, Corruption +{Delta}",
                        playerId, result.Message, result.CorruptionDelta);
                }
            }
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "Error processing corridor stop check");
        }
    }

    private void CheckHeartbeatTimeouts(object? state)
    {
        try
        {
            var timedOutSessions = _clientSessions.Values
                .Where(s => s.PlayerId.HasValue && s.IsHeartbeatTimedOut())
                .ToList();

            foreach (var session in timedOutSessions)
            {
                logger.LogWarning("Heartbeat timeout for PlayerId={PlayerId}, forcing disconnect", session.PlayerId);
                session.ForceDisconnect();
            }

            if (timedOutSessions.Count > 0)
                logger.LogInformation("Disconnected {Count} sessions due to heartbeat timeout",
                    timedOutSessions.Count);
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "Error checking heartbeat timeouts");
        }
    }

    private void OnClientSessionCreated(UserToken token)
    {
        try
        {
            var redLockFactory = redisPool.GetRedLockFactory();
            natsClientFactory.Create();
            _ = new GameClientSession(
                token,
                redLockFactory,
                logger,
                cacheHelper,
                OnClientSessionLeave,
                RegisterClientSession,
                GetSessionsByInstance,
                _interactableStateManager,
                _inGameInventoryManager,
                _areaRuleManager,
                _itemPoolManager,
                _corridorRuleManager,
                _doorStateManager,
                _sabotageManager,
                _manittoChainManager,
                _missionManager,
                _checklistManager,
                _areaClosureManager,
                _traceManager,
                new InteractionChoiceService(_interactionLogManager, _manittoChainManager, _gameEventLogManager),
                _botPlayerManager,
                _gameEventLogManager,
                _encounterRevealManager);

            logger.LogInformation("Game client session created");
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "Failed to create game client session");
        }
    }

    private void OnClientSessionLeave(GameClientSession session)
    {
        if (session.PlayerId.HasValue)
        {
            _clientSessions.TryRemove(session.PlayerId.Value, out _);
            logger.LogInformation("Game client session removed: PlayerId={SessionPlayerId}", session.PlayerId.Value);

            // 복도 규칙 플레이어 상태 정리
            if (session.CurrentMapSubId > 0)
                _corridorRuleManager.RemovePlayerState(session.CurrentMapSubId, session.PlayerId.Value);

            // 인스턴스 컨트롤러에 연결 해제 알림 (모든 유저 연결 해제 시 게임 종료 처리)
            if (session.CurrentMapSubId > 0)
                foreach (var controller in _instanceControllerList)
                    controller.OnPlayerDisconnected(session.CurrentMapId, session.CurrentMapSubId,
                        session.PlayerId.Value);
        }
    }

    private void RegisterClientSession(long playerId, GameClientSession session)
    {
        _clientSessions.TryAdd(playerId, session);
        logger.LogInformation("Game client session registered: PlayerId={PlayerId}", playerId);
    }

    private List<GameClientSession> GetSessionsByInstance(MapId mapId, long mapSubId)
    {
        return _clientSessions.Values
            .Where(s => s.CurrentMapId == mapId && s.CurrentMapSubId == mapSubId)
            .ToList();
    }

    /// <summary>
    ///     복도 정지 위반 시 해당 플레이어의 정신오염도 증가
    /// </summary>
    private void OnCorridorStopViolation(long matchingId, long playerId, int corruptionDelta)
    {
        if (_clientSessions.TryGetValue(playerId, out var session))
        {
            session.ModifyStats(corruptionDelta: corruptionDelta);
            logger.LogInformation("Player {PlayerId} corridor stop violation: Corruption +{Delta}", playerId,
                corruptionDelta);
        }
    }

    /// <summary>
    ///     사보타주 상태 변경 콜백 - InteractableState 업데이트 및 브로드캐스트
    /// </summary>
    private void OnSabotageStateChange(long matchingId, int interactId, int newState, AreaType triggerArea)
    {
        logger.LogInformation(
            "Sabotage state change: MatchingId={MatchingId}, InteractId={InteractId}, NewState={NewState}, TriggerArea={TriggerArea}",
            matchingId, interactId, newState, triggerArea);

        // InteractableStateManager 상태 업데이트
        _interactableStateManager.SetInteractableState(matchingId, interactId, newState);

        // 해당 매칭의 해당 Area에 있는 모든 플레이어에게 브로드캐스트
        var sessionsInArea = _clientSessions.Values
            .Where(s => s.CurrentMapSubId == matchingId && s.CurrentArea == triggerArea && s.PlayerId.HasValue)
            .ToList();

        using var packet = PacketMaker.G_TO_C_INTERACTABLE_STATE_CHANGE(interactId, newState);
        foreach (var session in sessionsInArea) session.Send(packet);

        logger.LogInformation("Broadcasted INTERACTABLE_STATE_CHANGE to {Count} players in Area {Area}",
            sessionsInArea.Count, triggerArea);
    }

    /// <summary>
    ///     사보타주 타임아웃 콜백 - 해당 매칭의 모든 플레이어에게 정신오염도 증가
    /// </summary>
    private void OnSabotageTimeout(long matchingId, AreaType triggerArea, int corruptionDelta)
    {
        logger.LogInformation("Sabotage timeout: MatchingId={MatchingId}, Area={Area}, Corruption +{Delta}",
            matchingId, triggerArea, corruptionDelta);

        // 해당 매칭의 모든 플레이어에게 정신오염도 증가
        var matchingSessions = _clientSessions.Values
            .Where(s => s.CurrentMapSubId == matchingId && s.PlayerId.HasValue)
            .ToList();

        foreach (var session in matchingSessions) session.ModifyStats(corruptionDelta: corruptionDelta);

        logger.LogInformation("Applied corruption +{Delta} to {Count} players in MatchingId={MatchingId}",
            corruptionDelta, matchingSessions.Count, matchingId);
    }

    // MMO 로그아웃 프로토콜 제거됨 - 세션 기반 게임에서는 불필요
    // private void SubscribeToLogoutEvents() { ... }
    // private async Task ProcessMessage(byte[] message) { ... }
    // private async Task HandleLogout(long playerId, byte[] body) { ... }

    // ===== 운영 어드민 API =====

    /// <summary>
    ///     글로벌 매칭 config 서비스 (AdminEndpoints에서 직접 접근)
    /// </summary>
    public MatchingConfigService MatchingConfigService => _matchingConfigService;

    /// <summary>
    ///     운영툴 진행 로그 매니저 (AdminEndpoints에서 events 조회용)
    /// </summary>
    public GameEventLogManager GameEventLogManager => _gameEventLogManager;

    /// <summary>
    ///     활성 인스턴스 ID 목록 반환 (MatchingId 기준 dedup)
    /// </summary>
    public IReadOnlyList<long> GetActiveInstanceIds()
    {
        return GetActiveMatchingIds();
    }

    private List<long> GetActiveMatchingIds()
    {
        var ids = _clientSessions.Values
            .Where(s => s.PlayerId.HasValue && s.CurrentMapSubId > 0)
            .Select(s => s.CurrentMapSubId)
            .ToHashSet();

        foreach (long matchingId in _botPlayerManager.GetActiveMatchingIds())
        {
            if (matchingId > 0)
                ids.Add(matchingId);
        }

        return ids.OrderBy(id => id).ToList();
    }

    public InstanceSnapshot? CreateBotOnlyInstance(int botCount = 5)
    {
        botCount = Math.Clamp(botCount, 2, 5);
        long matchingId = System.Threading.Interlocked.Increment(ref _adminBotOnlyMatchingIdSeed);
        var jobs = BuildBotOnlyJobPool(botCount);
        var playerIds = Enumerable.Range(0, botCount)
            .Select(_ => System.Threading.Interlocked.Decrement(ref _adminBotOnlyPlayerIdSeed))
            .ToList();

        var botInfoList = new List<BotMatchingInfo>();
        for (int i = 0; i < botCount; i++)
        {
            int targetIndex = (i + 1) % botCount;
            botInfoList.Add(new BotMatchingInfo
            {
                PlayerId = playerIds[i],
                TargetPlayerId = playerIds[targetIndex],
                MyJobTitle = jobs[i],
                TargetJobTitle = jobs[targetIndex]
            });
        }

        _botPlayerManager.RegisterBots(matchingId, MapId.School, botInfoList);

        foreach (var bot in botInfoList)
        {
            _manittoChainManager.RegisterLink(matchingId, new ChainLink
            {
                PlayerId = bot.PlayerId,
                TargetPlayerId = bot.TargetPlayerId,
                MyJobTitle = bot.MyJobTitle,
                TargetJobTitle = bot.TargetJobTitle
            });

            _missionManager.InitializePlayer(matchingId, bot.PlayerId, bot.MyJobTitle);
            _missionManager.EnsureBroadcastTransmitterGift(matchingId, bot.PlayerId, bot.TargetPlayerId);

            foreach ((int itemId, int count) in GameRuleData.InGameItemList)
                _inGameInventoryManager.AddItem(matchingId, bot.PlayerId, itemId, count);
        }

        _areaClosureManager.InitializeMatching(matchingId, jobs);
        _doorStateManager.InitializeMatching(matchingId);
        _checklistManager.StartRound(matchingId, 1, playerIds,
            playerId => ResolveBotOnlyChecklistChainContext(matchingId, playerId));
        GameClientSession.TryStartHeadlessActionRound(matchingId);
        StartHeadlessRoundTimer(matchingId);

        _gameEventLogManager.LogSystem(matchingId,
            $"Bot-only instance created: botCount={botCount}, ids=[{string.Join(",", playerIds)}]");
        logger.LogInformation(
            "Bot-only instance created: MatchingId={MatchingId}, BotCount={BotCount}, Ids=[{Ids}]",
            matchingId, botCount, string.Join(",", playerIds));

        return GetInstanceSnapshot(matchingId);
    }

    private static List<JobTitle> BuildBotOnlyJobPool(int botCount)
    {
        var jobs = new List<JobTitle>
        {
            JobTitle.BROADCAST_MEMBER,
            JobTitle.DISCIPLINE_MEMBER,
            JobTitle.LIBRARY_COMMITTEE,
            JobTitle.SPORTS_CAPTAIN,
            JobTitle.CLEANING_MEMBER
        };

        return jobs.Take(botCount).ToList();
    }

    private ChecklistChainContext ResolveBotOnlyChecklistChainContext(long matchingId, long playerId)
    {
        var myLink = _manittoChainManager.GetLink(matchingId, playerId);
        bool targetAlive = myLink != null && IsBotOnlyChainPlayerActive(matchingId, myLink.TargetPlayerId);
        var manittoLink = _manittoChainManager.FindManittoOf(matchingId, playerId);
        bool manittoAlive = manittoLink != null
                            && manittoLink.Status != ManittoStatus.ELIMINATED
                            && manittoLink.Status != ManittoStatus.SPECTATING;
        return new ChecklistChainContext(targetAlive, manittoAlive);
    }

    private bool IsBotOnlyChainPlayerActive(long matchingId, long playerId)
    {
        var link = _manittoChainManager.GetLink(matchingId, playerId);
        return link != null
               && link.Status != ManittoStatus.ELIMINATED
               && link.Status != ManittoStatus.SPECTATING;
    }

    private void StartHeadlessRoundTimer(long matchingId)
    {
        var timer = new Timer(_ => ProcessHeadlessRoundTimerTick(matchingId), null,
            TimeSpan.Zero, TimeSpan.FromSeconds(1));
        if (!_headlessRoundTimers.TryAdd(matchingId, timer))
            timer.Dispose();
    }

    private void StopHeadlessRoundTimer(long matchingId)
    {
        if (_headlessRoundTimers.TryRemove(matchingId, out var timer))
            timer.Dispose();
    }

    private void ProcessHeadlessRoundTimerTick(long matchingId)
    {
        if (!GameClientSession.GameRoundStates.TryGetValue(matchingId, out var state))
        {
            StopHeadlessRoundTimer(matchingId);
            return;
        }

        lock (state.SyncRoot)
        {
            if (state.IsSessionEnded)
            {
                StopHeadlessRoundTimer(matchingId);
                return;
            }

            if (DateTime.UtcNow < state.PhaseEndsAtUtc)
                return;

            AdvanceHeadlessRoundPhase(matchingId, state);
        }
    }

    private void AdvanceHeadlessRoundPhase(long matchingId, GameClientSession.RoundRuntimeState state)
    {
        switch (state.Phase)
        {
            case RoundPhase.Action:
                EnterHeadlessSettlementNomination(matchingId, state);
                break;
            case RoundPhase.SettlementNomination:
                EnterHeadlessSettlementResult(matchingId, state);
                break;
            case RoundPhase.SettlementResult:
                EnterHeadlessSettlementContribution(matchingId, state);
                break;
            case RoundPhase.SettlementContributionReveal:
                EnterHeadlessSettlementSubPhase(matchingId, state, RoundPhase.SettlementDetectionResultReveal,
                    Config.ROUND_SETTLEMENT_DETECTION_RESULT_SECONDS);
                break;
            case RoundPhase.SettlementDetectionResultReveal:
                EnterHeadlessSettlementSubPhase(matchingId, state, RoundPhase.SettlementEliminationReveal,
                    Config.ROUND_SETTLEMENT_ELIMINATION_SECONDS);
                break;
            case RoundPhase.SettlementEliminationReveal:
                ApplyHeadlessSettlementElimination(matchingId, state);
                AdvanceHeadlessRoundOrEnd(matchingId, state);
                break;
        }
    }

    private void EnterHeadlessSettlementNomination(long matchingId, GameClientSession.RoundRuntimeState state)
    {
        state.Phase = RoundPhase.SettlementNomination;
        state.PhaseDurationSeconds = Config.ROUND_SETTLEMENT_NOMINATION_SECONDS;
        state.PhaseEndsAtUtc = DateTime.UtcNow.AddSeconds(Config.ROUND_SETTLEMENT_NOMINATION_SECONDS);
        state.SettlementNominations.Clear();
        state.BotNominationsInjected = false;
        ClearHeadlessSettlementContributionResult(state);
        state.SettlementEliminationApplied = false;
        _presenceTracker.FreezeNotebookOverlaps(matchingId);
        _gameEventLogManager.LogSystem(matchingId,
            $"Headless settlement nomination started: Round={state.RoundNumber}");
    }

    private void EnterHeadlessSettlementResult(long matchingId, GameClientSession.RoundRuntimeState state)
    {
        InjectHeadlessBotPresenceSettlementNominations(matchingId, state);
        state.Phase = RoundPhase.SettlementResult;
        state.PhaseDurationSeconds = Config.ROUND_SETTLEMENT_RESULT_SECONDS;
        state.PhaseEndsAtUtc = DateTime.UtcNow.AddSeconds(Config.ROUND_SETTLEMENT_RESULT_SECONDS);
        _gameEventLogManager.LogSystem(matchingId,
            $"Headless settlement result: Round={state.RoundNumber}, Nominations={state.SettlementNominations.Count}");
    }

    private void EnterHeadlessSettlementContribution(long matchingId, GameClientSession.RoundRuntimeState state)
    {
        EnterHeadlessSettlementSubPhase(matchingId, state, RoundPhase.SettlementContributionReveal,
            Config.ROUND_SETTLEMENT_CONTRIBUTION_SECONDS);
        BuildHeadlessSettlementContributionResult(matchingId, state);
    }

    private void EnterHeadlessSettlementSubPhase(long matchingId, GameClientSession.RoundRuntimeState state,
        RoundPhase phase, int durationSeconds)
    {
        state.Phase = phase;
        state.PhaseDurationSeconds = Math.Max(1, durationSeconds);
        state.PhaseEndsAtUtc = DateTime.UtcNow.AddSeconds(state.PhaseDurationSeconds);
        _gameEventLogManager.LogSystem(matchingId,
            $"Headless settlement phase: Round={state.RoundNumber}, Phase={phase}");
    }

    private void InjectHeadlessBotPresenceSettlementNominations(long matchingId,
        GameClientSession.RoundRuntimeState state)
    {
        if (state.BotNominationsInjected)
            return;

        state.BotNominationsInjected = true;
        var roster = GetHeadlessActivePlayerIds(matchingId);
        if (roster.Count <= 1)
            return;

        foreach (var bot in _botPlayerManager.GetBots(matchingId)
                     .Where(bot => !bot.IsEliminated && bot.ManittoStatus != ManittoStatus.SPECTATING)
                     .OrderBy(bot => bot.PlayerId))
        {
            if (state.SettlementNominations.ContainsKey(bot.PlayerId))
                continue;

            var candidate = _presenceTracker
                .GetNominationCandidates(matchingId, bot.PlayerId, roster, bot.TargetPlayerId)
                .FirstOrDefault();

            long targetPlayerId = candidate?.CandidateId ?? 0;
            if (targetPlayerId == 0)
            {
                targetPlayerId = ResolveFallbackBotNominationTarget(roster, bot.PlayerId, bot.TargetPlayerId);
            }

            if (targetPlayerId == 0)
                continue;

            state.SettlementNominations[bot.PlayerId] = targetPlayerId;
            ApplyHeadlessBotSettlementBookmark(matchingId, state.RoundNumber, bot, targetPlayerId,
                candidate != null ? "suspicion" : "fallback", candidate);
            if (candidate != null)
            {
                _gameEventLogManager.LogSystem(matchingId,
                    $"Headless bot nomination: Bot={bot.PlayerId}, Target={targetPlayerId}, Score={candidate.Score:0.##}, Presence={candidate.Presence:0.##}, TotalOverlap={candidate.TotalOverlapSeconds}, FollowEntries={candidate.EnterAfterObserverCount}");
            }
            else
            {
                _gameEventLogManager.LogSystem(matchingId,
                    $"Headless bot nomination fallback: Bot={bot.PlayerId}, Target={targetPlayerId}");
            }
        }
    }

    private void ApplyHeadlessBotSettlementBookmark(long matchingId, int roundNumber, BotPlayerState bot,
        long targetPlayerId, string reason, PresenceNominationCandidate? candidate)
    {
        if (targetPlayerId == 0 || targetPlayerId == bot.PlayerId) return;

        bot.SetPresenceBookmark(targetPlayerId);
        _gameEventLogManager.LogSystem(matchingId,
            candidate != null
                ? $"Headless bot guard target set: Round={roundNumber}, Bot={bot.PlayerId}, Target={targetPlayerId}, Reason={reason}, Score={candidate.Score:0.##}, Presence={candidate.Presence:0.##}, TotalOverlap={candidate.TotalOverlapSeconds}, FollowEntries={candidate.EnterAfterObserverCount}"
                : $"Headless bot guard target set: Round={roundNumber}, Bot={bot.PlayerId}, Target={targetPlayerId}, Reason={reason}");
    }

    private static long ResolveFallbackBotNominationTarget(
        IEnumerable<long> roster, long botPlayerId, long botTargetPlayerId)
    {
        return roster
            .Where(playerId => playerId != botPlayerId && playerId != botTargetPlayerId)
            .OrderBy(playerId => playerId)
            .FirstOrDefault();
    }

    private void BuildHeadlessSettlementContributionResult(long matchingId,
        GameClientSession.RoundRuntimeState state)
    {
        ClearHeadlessSettlementContributionResult(state);

        var playerIds = GetHeadlessActivePlayerIds(matchingId);
        if (playerIds.Count == 0)
            return;

        var checklistEntries = _checklistManager.BuildSettlementContributionEntries(matchingId, playerIds);
        if (checklistEntries.Any(entry => entry.Contribution > 0))
        {
            state.SettlementContributionEntries.AddRange(checklistEntries);
        }
        else
        {
            foreach (var entry in BuildHeadlessFallbackSettlementContributionEntries(playerIds))
                state.SettlementContributionEntries.Add(entry);
        }

        var top = state.SettlementContributionEntries
            .OrderByDescending(entry => entry.Contribution)
            .ThenBy(entry => entry.PlayerId)
            .First();
        var lowest = state.SettlementContributionEntries
            .OrderBy(entry => entry.Contribution)
            .ThenBy(entry => entry.PlayerId)
            .First();

        state.SettlementContributionTopPlayerId = top.PlayerId;
        state.SettlementContributionLowestPlayerId = lowest.PlayerId;
        state.SettlementContributionTopValue = top.Contribution;
        state.SettlementContributionLowestValue = lowest.Contribution;

        long decisiveTargetPlayerId = state.SettlementNominations.TryGetValue(top.PlayerId, out long nominatedTarget)
            ? nominatedTarget
            : 0;
        bool success = decisiveTargetPlayerId != 0
                       && _manittoChainManager.IsAliveManittoOf(matchingId, top.PlayerId, decisiveTargetPlayerId);

        state.SettlementContributionDecisiveTargetPlayerId = decisiveTargetPlayerId;
        state.SettlementContributionNominationSuccess = success;
        state.SettlementContributionEliminatedPlayerId = success ? decisiveTargetPlayerId : lowest.PlayerId;

        _gameEventLogManager.LogSystem(matchingId,
            $"Headless contribution result: Round={state.RoundNumber}, Top={top.PlayerId}:{top.Contribution}, Lowest={lowest.PlayerId}:{lowest.Contribution}, Success={success}, Eliminated={state.SettlementContributionEliminatedPlayerId}");
    }

    private static List<SettlementContributionEntry> BuildHeadlessFallbackSettlementContributionEntries(List<long> playerIds)
    {
        var values = new List<int> { 9, 21, 27, 21, 15 };
        var entries = new List<SettlementContributionEntry>();
        for (int i = 0; i < playerIds.Count; i++)
        {
            entries.Add(new SettlementContributionEntry
            {
                PlayerId = playerIds[i],
                Contribution = values[i % values.Count]
            });
        }

        return entries;
    }

    private void ApplyHeadlessSettlementElimination(long matchingId, GameClientSession.RoundRuntimeState state)
    {
        if (state.SettlementEliminationApplied)
            return;

        long eliminatedPlayerId = state.SettlementContributionEliminatedPlayerId;
        if (eliminatedPlayerId == 0 || !IsBotOnlyChainPlayerActive(matchingId, eliminatedPlayerId))
        {
            state.SettlementEliminationApplied = true;
            _gameEventLogManager.LogSystem(matchingId,
                $"Headless elimination skipped: Round={state.RoundNumber}, Candidate={eliminatedPlayerId}");
            return;
        }

        state.SettlementEliminationApplied = true;
        var reason = state.SettlementContributionNominationSuccess
            ? EliminationReason.DETECTED
            : EliminationReason.SETTLEMENT_LOW_CONTRIBUTION;

        _gameEventLogManager.LogElimination(matchingId, eliminatedPlayerId, reason.ToString(), isBot: true);
        ProcessBotElimination(matchingId, eliminatedPlayerId, reason, []);
        _gameEventLogManager.LogSystem(matchingId,
            $"Headless elimination applied: Round={state.RoundNumber}, Player={eliminatedPlayerId}, Reason={reason}");
    }

    private void AdvanceHeadlessRoundOrEnd(long matchingId, GameClientSession.RoundRuntimeState state)
    {
        var (isGameOver, winnerId) = _manittoChainManager.CheckGameOver(matchingId);
        if (state.RoundNumber >= Config.ROUND_TOTAL_COUNT || isGameOver)
        {
            state.Phase = RoundPhase.Ended;
            state.PhaseDurationSeconds = 0;
            state.PhaseEndsAtUtc = DateTime.UtcNow;
            state.IsSessionEnded = true;

            winnerId ??= _manittoChainManager.DetermineWinnerByResources(matchingId, playerId =>
            {
                var session = _clientSessions.Values.FirstOrDefault(s => s.PlayerId == playerId);
                if (session != null) return (session.AdminStamina, session.AdminCorruption, 100);

                var bot = _botPlayerManager.GetBot(matchingId, playerId);
                return bot != null ? (bot.Stamina, bot.Corruption, 100) : (0, 100, 100);
            });

            _gameEventLogManager.LogSystem(matchingId,
                $"Headless game ended: Round={state.RoundNumber}, Winner={winnerId ?? 0}");
            StopHeadlessRoundTimer(matchingId);
            return;
        }

        state.RoundNumber++;
        state.Phase = RoundPhase.Action;
        state.PhaseDurationSeconds = Config.ROUND_ACTION_SECONDS;
        state.PhaseEndsAtUtc = DateTime.UtcNow.AddSeconds(Config.ROUND_ACTION_SECONDS);
        state.SettlementEliminationApplied = false;
        state.SettlementNominations.Clear();
        state.BotNominationsInjected = false;
        ClearHeadlessSettlementContributionResult(state);

        var playerIds = GetHeadlessActivePlayerIds(matchingId);
        _checklistManager.StartRound(matchingId, state.RoundNumber, playerIds,
            playerId => ResolveBotOnlyChecklistChainContext(matchingId, playerId));
        _gameEventLogManager.LogSystem(matchingId,
            $"Headless round advanced: Round={state.RoundNumber}, Players={string.Join(",", playerIds)}");
    }

    private List<long> GetHeadlessActivePlayerIds(long matchingId)
    {
        var ids = _clientSessions.Values
            .Where(session => session.PlayerId.HasValue
                              && session.CurrentMapSubId == matchingId
                              && !session.IsEliminated
                              && session.ManittoStatus != ManittoStatus.SPECTATING)
            .Select(session => session.PlayerId!.Value)
            .ToList();

        ids.AddRange(_botPlayerManager.GetBots(matchingId)
            .Where(bot => !bot.IsEliminated && bot.ManittoStatus != ManittoStatus.SPECTATING)
            .Select(bot => bot.PlayerId));

        return ids.Distinct().OrderBy(id => id).ToList();
    }

    private static void ClearHeadlessSettlementContributionResult(GameClientSession.RoundRuntimeState state)
    {
        state.SettlementContributionEntries.Clear();
        state.SettlementContributionTopPlayerId = 0;
        state.SettlementContributionLowestPlayerId = 0;
        state.SettlementContributionTopValue = 0;
        state.SettlementContributionLowestValue = 0;
        state.SettlementContributionDecisiveTargetPlayerId = 0;
        state.SettlementContributionNominationSuccess = false;
        state.SettlementContributionEliminatedPlayerId = 0;
    }

    /// <summary>
    ///     인스턴스 요약 (목록 뷰용)
    /// </summary>
    public InstanceSummary? GetInstanceSummary(long matchingId)
    {
        var sessions = _clientSessions.Values
            .Where(s => s.PlayerId.HasValue && s.CurrentMapSubId == matchingId)
            .ToList();
        var bots = _botPlayerManager.GetBots(matchingId);

        if (sessions.Count == 0 && bots.Count == 0) return null;

        var closureState = _areaClosureManager.GetMatchingState(matchingId);
        double elapsed = closureState != null
            ? (DateTime.UtcNow - closureState.GameStartTime).TotalSeconds
            : 0;

        var closedAreas = closureState?.ClosedAreas
            .Select(a => a.ToString())
            .ToList() ?? [];

        int aliveCount = sessions.Count(s => !s.IsEliminated) + bots.Count(b => !b.IsEliminated);
        string mapId = sessions.FirstOrDefault()?.CurrentMapId.ToString()
                       ?? _botPlayerManager.GetMatchingMapId(matchingId).ToString();
        var round = GameClientSession.GetRoundSnapshot(matchingId);

        return new InstanceSummary
        {
            MatchingId = matchingId,
            MapId = mapId,
            PlayerCount = sessions.Count + bots.Count,
            AliveCount = aliveCount,
            ElapsedSeconds = Math.Round(elapsed, 1),
            RoundNumber = round?.RoundNumber ?? 0,
            TotalRounds = round?.TotalRounds ?? 0,
            RoundPhase = round?.Phase ?? "",
            RoundRemainingSeconds = round?.RemainingSeconds ?? 0,
            RoundPhaseDurationSeconds = round?.PhaseDurationSeconds ?? 0,
            RoundSessionEnded = round?.IsSessionEnded ?? false,
            ClosedAreas = closedAreas
        };
    }

    /// <summary>
    ///     인스턴스 풀 스냅샷 (폐쇄 스케줄 + 미션 전체 단계 포함)
    /// </summary>
    public InstanceSnapshot? GetFullInstanceSnapshot(long matchingId)
    {
        var base_ = GetInstanceSnapshot(matchingId);
        if (base_ == null) return null;

        // 폐쇄 스케줄 조립 (startDelaySec, intervalSec 포함)
        var (sequence, closedIds, nextArea, nextAtUnix, secondsLeft, warningActive, startDelaySec, intervalSec) =
            _areaClosureManager.GetClosureSnapshot(matchingId);

        // 시퀀스 한글명 목록
        var areaNames = sequence.Select(a => GameAreaNameData.Get((AreaType)a)).ToList();

        base_.Closure = new ClosureSnapshot
        {
            ClosureSequence = sequence,
            AreaNames = areaNames,
            ClosedAreaIds = closedIds,
            NextClosureAreaType = nextArea,
            NextClosureAtUnix = nextAtUnix,
            NextClosureSecondsLeft = secondsLeft,
            WarningActive = warningActive,
            StartDelaySec = startDelaySec,
            IntervalSec = intervalSec
        };

        // 플레이어별 직책 + 전체 미션 단계 보강 (description 포함). 인간 + 봇 모두 처리.
        var sessions = _clientSessions.Values
            .Where(s => s.PlayerId.HasValue && s.CurrentMapSubId == matchingId)
            .ToDictionary(s => s.PlayerId!.Value);
        var botMap = _botPlayerManager.GetBots(matchingId).ToDictionary(b => b.PlayerId);

        foreach (var player in base_.Players)
        {
            JobTitle jobTitle;
            if (sessions.TryGetValue(player.PlayerId, out var session))
            {
                jobTitle = session.AdminJobTitle;
            }
            else if (botMap.TryGetValue(player.PlayerId, out var bot))
            {
                jobTitle = bot.MyJobTitle;
            }
            else
            {
                continue;
            }

            player.JobTitle = jobTitle.ToKorean();

            // v0.2.0 — 부품 진행도로 어드민 표시 재구성
            var jobParts = GameMissionData.GetParts((short)jobTitle);
            var partState = _missionManager.GetState(matchingId, player.PlayerId);
            int order = 0;
            player.AllSteps = jobParts.Select(p => new MissionFullStep
            {
                Order = ++order,
                PartId = p.PartId,
                PartNameKr = p.PartNameKr,
                PartTier = (int)p.PartTier,
                TargetAreaType = p.TargetArea,
                TargetAreaName = p.TargetArea > 0 ? GameAreaNameData.Get((AreaType)p.TargetArea) : "",
                TargetObjectType = p.TargetObjectType,
                IsCollected = partState?.CollectedParts.Contains(p.PartId) ?? false,
                PrerequisiteShareGroup = p.PrerequisiteShareGroup
            }).ToList();
        }

        return base_;
    }

    /// <summary>
    ///     인스턴스 상세 스냅샷 (플레이어별 코어 상태 포함)
    /// </summary>
    public InstanceSnapshot? GetInstanceSnapshot(long matchingId)
    {
        var sessions = _clientSessions.Values
            .Where(s => s.PlayerId.HasValue && s.CurrentMapSubId == matchingId)
            .ToList();

        var bots = _botPlayerManager.GetBots(matchingId);

        if (sessions.Count == 0 && bots.Count == 0) return null;

        var closureState = _areaClosureManager.GetMatchingState(matchingId);
        double elapsed = closureState != null
            ? (DateTime.UtcNow - closureState.GameStartTime).TotalSeconds
            : 0;

        var closedAreas = closureState?.ClosedAreas
            .Select(a => a.ToString())
            .ToList() ?? [];

        // 모든 PlayerId(인간+봇)에 대한 ChainLink 조회 → ManittoOfMe 역방향 매핑
        var allPlayerIds = sessions.Select(s => s.PlayerId!.Value).Concat(bots.Select(b => b.PlayerId)).ToList();
        var allLinks = allPlayerIds
            .Select(id => _manittoChainManager.GetLink(matchingId, id))
            .Where(l => l != null)
            .ToList();
        long? FindManittoOf(long playerId) =>
            allLinks.FirstOrDefault(l => l!.TargetPlayerId == playerId)?.PlayerId;

        var playerSnapshots = new List<PlayerSnapshot>();

        // 1) 인간 플레이어
        foreach (var s in sessions)
        {
            var missionState = _missionManager.GetState(matchingId, s.PlayerId!.Value);
            var chainLink = _manittoChainManager.GetLink(matchingId, s.PlayerId!.Value);
            playerSnapshots.Add(new PlayerSnapshot
            {
                PlayerId = s.PlayerId!.Value,
                Area = s.CurrentArea.ToString(),
                Stamina = s.AdminStamina,
                Corruption = s.AdminCorruption,
                ManittoStatus = s.ManittoStatus.ToString(),
                TargetPlayerId = s.TargetPlayerId,
                IsBot = s.IsBot,
                IsEliminated = s.IsEliminated,
                MissionStep = missionState?.CollectedParts.Count ?? 0,
                MissionTotalSteps = missionState != null
                    ? GameMissionData.GetTotalParts((short)missionState.JobTitle)
                    : 0,
                MissionCompleted = missionState?.IsCompleted ?? false,
                ManittoOfMe = FindManittoOf(s.PlayerId!.Value),
                ChainStatus = chainLink?.Status.ToString() ?? ""
            });
        }

        // 2) 봇 — TCP 세션이 없으므로 BotPlayerManager._botStates에서 조회
        foreach (var bot in bots)
        {
            var missionState = _missionManager.GetState(matchingId, bot.PlayerId);
            var chainLink = _manittoChainManager.GetLink(matchingId, bot.PlayerId);
            playerSnapshots.Add(new PlayerSnapshot
            {
                PlayerId = bot.PlayerId,
                Area = bot.CurrentArea.ToString(),
                Stamina = bot.Stamina,
                Corruption = bot.Corruption,
                ManittoStatus = bot.ManittoStatus.ToString(),
                TargetPlayerId = bot.TargetPlayerId,
                IsBot = true,
                IsEliminated = bot.IsEliminated,
                MissionStep = missionState?.CollectedParts.Count ?? 0,
                MissionTotalSteps = missionState != null
                    ? GameMissionData.GetTotalParts((short)missionState.JobTitle)
                    : 0,
                MissionCompleted = missionState?.IsCompleted ?? false,
                ManittoOfMe = FindManittoOf(bot.PlayerId),
                ChainStatus = chainLink?.Status.ToString() ?? ""
            });
        }

        int aliveCount = sessions.Count(s => !s.IsEliminated) + bots.Count(b => !b.IsEliminated);
        string mapId = sessions.FirstOrDefault()?.CurrentMapId.ToString()
                       ?? _botPlayerManager.GetMatchingMapId(matchingId).ToString();
        var round = GameClientSession.GetRoundSnapshot(matchingId);

        return new InstanceSnapshot
        {
            MatchingId = matchingId,
            MapId = mapId,
            PlayerCount = sessions.Count + bots.Count,
            AliveCount = aliveCount,
            ElapsedSeconds = Math.Round(elapsed, 1),
            RoundNumber = round?.RoundNumber ?? 0,
            TotalRounds = round?.TotalRounds ?? 0,
            RoundPhase = round?.Phase ?? "",
            RoundRemainingSeconds = round?.RemainingSeconds ?? 0,
            RoundPhaseDurationSeconds = round?.PhaseDurationSeconds ?? 0,
            RoundSessionEnded = round?.IsSessionEnded ?? false,
            ClosedAreas = closedAreas,
            Players = playerSnapshots
        };
    }
}
