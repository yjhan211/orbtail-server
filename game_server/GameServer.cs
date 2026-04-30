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
    private readonly InteractRuleManager _interactRuleManager = new();
    private readonly ItemPoolManager _itemPoolManager = new();
    private readonly SabotageManager _sabotageManager = new();
    private readonly InteractionLogManager _interactionLogManager = new();
    private readonly ManittoChainManager _manittoChainManager = new(logger);
    private readonly MissionManager _missionManager = new(logger);
    private readonly MatchingConfigService _matchingConfigService = new(cacheHelper, logger);
    // _areaClosureManager은 InitializeServices()에서 _matchingConfigService 생성 후 초기화
    private AreaClosureManager _areaClosureManager = null!;
    private readonly TraceManager _traceManager = new();
    private readonly BotPlayerManager _botPlayerManager = new(logger);

    private Timer? _corridorStopCheckTimer;
    private CancellationTokenSource _cts = new();
    private Timer? _heartbeatCheckTimer;
    private Timer? _resourceTickTimer;        // 정신력 자연감소 + 타겟 근접 회복 + 시한부
    private Timer? _areaClosureTickTimer;     // 구역 폐쇄 체크
    private Timer? _targetLocationTimer;      // 타겟 위치 전송

    // 자원 틱 설정 (GDD v0.0.5 확정 수치)
    private const int ResourceTickIntervalSeconds = 5;
    // 오염도 점진적 가속: 0~5분 +2, 5~10분 +4, 10분+ +6 (전반적 증가량 2배 상향)
    private const int MentalDecayPhase1 = 2;            // 0~5분: 5초당 오염도 +2
    private const int MentalDecayPhase2 = 4;            // 5~10분: 5초당 오염도 +4
    private const int MentalDecayPhase3 = 6;            // 10분+: 5초당 오염도 +6
    private const int Phase2StartSeconds = 300;          // 5분
    private const int Phase3StartSeconds = 600;          // 10분
    private const int TargetProximityRecovery = 3;      // 타겟 동일 구역 시 회복량 (5초당 오염도 -3)
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
            _interactRuleManager.Initialize(log, _areaRuleManager);
            _itemPoolManager.Initialize(log);
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
                bool isTerminal = session.ManittoStatus == ManittoStatus.TERMINAL;

                // [TEMP] 1. 정신력 자연감소 — 디버깅용 비활성
                int corruptionDelta = 0;
                // int corruptionDelta = GetMentalDecayAmount(session.CurrentMapSubId);

                // [TEMP] 2. 타겟 동일 구역 회복 — 디버깅용 비활성
                // if (!isTerminal && session.CurrentArea != AreaType.None)
                // {
                //     var targetSession = activeSessions.FirstOrDefault(s => s.PlayerId == session.TargetPlayerId);
                //     bool targetInSameArea = targetSession != null && targetSession.CurrentArea == session.CurrentArea;
                //     if (!targetInSameArea)
                //     {
                //         var targetBot = _botPlayerManager.GetBot(session.CurrentMapSubId, session.TargetPlayerId);
                //         targetInSameArea = targetBot is { IsEliminated: false } && targetBot.CurrentArea == session.CurrentArea;
                //     }
                //     if (targetInSameArea)
                //         corruptionDelta = -TargetProximityRecovery;
                // }

                // [TEMP] 3. 시한부 추가 감소 — 디버깅용 비활성
                // if (isTerminal)
                //     corruptionDelta += TerminalDecayAmount;

                // 4. 강당 체류 오염도 추가 증가 (패키지 Y 3A, GDD §3.1.1, #24)
                if (!isTerminal && session.CurrentArea == (AreaType)Config.AUDITORIUM_AREA_TYPE)
                {
                    var targetSession2 = activeSessions.FirstOrDefault(s => s.PlayerId == session.TargetPlayerId);
                    bool targetInAuditorium = targetSession2 != null && targetSession2.CurrentArea == session.CurrentArea;
                    if (!targetInAuditorium)
                        corruptionDelta += Config.AUDITORIUM_STAY_CORRUPTION_BONUS;
                }

                // 4. 폐쇄 구역 체류 시 오염도 추가 증가
                if (session.CurrentArea != AreaType.None &&
                    _areaClosureManager.IsAreaClosed(session.CurrentMapSubId, session.CurrentArea))
                {
                    corruptionDelta += Config.CLOSED_AREA_CORRUPTION_TICK;
                }

                session.ModifyStats(corruptionDelta: corruptionDelta);

                // 5. 자원 고갈 탈락 체크
                session.CheckResourceElimination();
            }

            // 6. 봇 플레이어 자원 틱
            var matchingIds = activeSessions
                .Select(s => s.CurrentMapSubId)
                .Distinct()
                .ToList();

            foreach (long matchingId in matchingIds)
            {
                if (!_botPlayerManager.HasBots(matchingId)) continue;
                int botDecay = GetMentalDecayAmount(matchingId);
                _botPlayerManager.ProcessBotTick(matchingId, botDecay, _areaClosureManager);
            }
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "자원 틱 처리 중 오류");
        }
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
            var matchingIds = _clientSessions.Values
                .Where(s => s.PlayerId.HasValue)
                .Select(s => s.CurrentMapSubId)
                .Distinct()
                .ToList();

            foreach (long matchingId in matchingIds)
            {
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
            TimeSpan.FromSeconds(3), TimeSpan.FromSeconds(3));
        logger.LogInformation("타겟 위치 전송 타이머 시작 (3초 간격)");
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
                _interactRuleManager,
                _doorStateManager,
                _sabotageManager,
                _manittoChainManager,
                _missionManager,
                _areaClosureManager,
                _traceManager,
                new InteractionChoiceService(_interactionLogManager, _manittoChainManager),
                _botPlayerManager);

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
    ///     활성 인스턴스 ID 목록 반환 (MatchingId 기준 dedup)
    /// </summary>
    public IReadOnlyList<long> GetActiveInstanceIds()
    {
        return _clientSessions.Values
            .Where(s => s.PlayerId.HasValue && s.CurrentMapSubId > 0)
            .Select(s => s.CurrentMapSubId)
            .Distinct()
            .ToList();
    }

    /// <summary>
    ///     인스턴스 요약 (목록 뷰용)
    /// </summary>
    public InstanceSummary? GetInstanceSummary(long matchingId)
    {
        var sessions = _clientSessions.Values
            .Where(s => s.PlayerId.HasValue && s.CurrentMapSubId == matchingId)
            .ToList();

        if (sessions.Count == 0) return null;

        var closureState = _areaClosureManager.GetMatchingState(matchingId);
        double elapsed = closureState != null
            ? (DateTime.UtcNow - closureState.GameStartTime).TotalSeconds
            : 0;

        var closedAreas = closureState?.ClosedAreas
            .Select(a => a.ToString())
            .ToList() ?? [];

        int aliveCount = sessions.Count(s => !s.IsEliminated);
        string mapId = sessions.FirstOrDefault()?.CurrentMapId.ToString() ?? "";

        return new InstanceSummary
        {
            MatchingId = matchingId,
            MapId = mapId,
            PlayerCount = sessions.Count,
            AliveCount = aliveCount,
            ElapsedSeconds = Math.Round(elapsed, 1),
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

        // 플레이어별 직책 + 전체 미션 단계 보강 (description 포함)
        var sessions = _clientSessions.Values
            .Where(s => s.PlayerId.HasValue && s.CurrentMapSubId == matchingId)
            .ToDictionary(s => s.PlayerId!.Value);

        foreach (var player in base_.Players)
        {
            if (!sessions.TryGetValue(player.PlayerId, out var session)) continue;

            player.JobTitle = session.AdminJobTitle.ToKorean();

            // v0.2.0 — 부품 진행도로 어드민 표시 재구성
            var jobParts = GameMissionData.GetParts((short)session.AdminJobTitle);
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

        if (sessions.Count == 0) return null;

        var closureState = _areaClosureManager.GetMatchingState(matchingId);
        double elapsed = closureState != null
            ? (DateTime.UtcNow - closureState.GameStartTime).TotalSeconds
            : 0;

        var closedAreas = closureState?.ClosedAreas
            .Select(a => a.ToString())
            .ToList() ?? [];

        var playerSnapshots = sessions.Select(s =>
        {
            var missionState = _missionManager.GetState(matchingId, s.PlayerId!.Value);
            var chainLink = _manittoChainManager.GetLink(matchingId, s.PlayerId!.Value);

            // 이 플레이어를 타겟으로 가진 마니또 PlayerId
            long? manittoOfMe = null;
            if (closureState != null)
            {
                var allLinks = sessions
                    .Select(other => _manittoChainManager.GetLink(matchingId, other.PlayerId!.Value))
                    .Where(l => l != null && l.TargetPlayerId == s.PlayerId!.Value)
                    .FirstOrDefault();
                manittoOfMe = allLinks?.PlayerId;
            }

            return new PlayerSnapshot
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
                ManittoOfMe = manittoOfMe,
                ChainStatus = chainLink?.Status.ToString() ?? ""
            };
        }).ToList();

        int aliveCount = sessions.Count(s => !s.IsEliminated);
        string mapId = sessions.FirstOrDefault()?.CurrentMapId.ToString() ?? "";

        return new InstanceSnapshot
        {
            MatchingId = matchingId,
            MapId = mapId,
            PlayerCount = sessions.Count,
            AliveCount = aliveCount,
            ElapsedSeconds = Math.Round(elapsed, 1),
            ClosedAreas = closedAreas,
            Players = playerSnapshots
        };
    }
}
