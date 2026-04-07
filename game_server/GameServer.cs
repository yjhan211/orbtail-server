using System.Collections.Concurrent;
using System.Net;
using game_server.controllers;
using game_server.network;
using game_server.services;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using network.common;
using network.common.data.helpers;
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

    // 보건실 힐링 설정
    private const int InfirmaryHealingIntervalSeconds = 1;
    private const int InfirmaryHealingAmount = 5;

    // 복도 정지 체크 간격
    private const int CorridorStopCheckIntervalMs = 500;
    private readonly AreaRuleManager _areaRuleManager = new();
    private readonly ConcurrentDictionary<long, GameClientSession> _clientSessions = new();
    private readonly CorridorRuleManager _corridorRuleManager = new();
    private readonly DoorStateManager _doorStateManager = new();
    private readonly ExitInstanceManager _exitInstanceManager = new();
    private readonly InGameInventoryManager _inGameInventoryManager = new();

    private readonly List<InstanceMapManager> _instanceControllerList = [];
    private readonly InteractableStateManager _interactableStateManager = new();
    private readonly InteractRuleManager _interactRuleManager = new();
    private readonly ItemPoolManager _itemPoolManager = new();
    private readonly SabotageManager _sabotageManager = new();

    private Timer? _corridorStopCheckTimer;
    private CancellationTokenSource _cts = new();
    private Timer? _heartbeatCheckTimer;
    private Timer? _infirmaryHealingTimer;

    public Task StartAsync(CancellationToken cancellationToken)
    {
        try
        {
            logger.LogInformation("Game server starting...");

            InitializeServices();
            InitializeControllers();
            StartTcpServer();
            StartHeartbeatChecker();
            StartInfirmaryHealingTimer();
            StartCorridorStopCheckTimer();

            _cts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);

            logger.LogInformation("Game server started successfully.");
            return Task.CompletedTask;
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

        await _cts.CancelAsync();

        // 타이머 정리
        if (_heartbeatCheckTimer != null)
        {
            await _heartbeatCheckTimer.DisposeAsync();
            _heartbeatCheckTimer = null;
        }

        if (_infirmaryHealingTimer != null)
        {
            await _infirmaryHealingTimer.DisposeAsync();
            _infirmaryHealingTimer = null;
        }

        if (_corridorStopCheckTimer != null)
        {
            await _corridorStopCheckTimer.DisposeAsync();
            _corridorStopCheckTimer = null;
        }

        await Task.WhenAll(_instanceControllerList.Select(c => c.ShutdownAsync()));

        _cts.Dispose();

        logger.LogInformation("Game server stopped.");
    }

    private void InitializeServices()
    {
        string natsEndpoint = configuration.GetRequiredString("natsEndPoint");

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
            _exitInstanceManager.Initialize(log);
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
            _exitInstanceManager, _corridorRuleManager);
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

    private void StartInfirmaryHealingTimer()
    {
        _infirmaryHealingTimer = new Timer(
            ProcessInfirmaryHealing,
            null,
            TimeSpan.FromSeconds(InfirmaryHealingIntervalSeconds),
            TimeSpan.FromSeconds(InfirmaryHealingIntervalSeconds));
        logger.LogInformation("Infirmary healing timer started (interval: {Interval}s, amount: {Amount})",
            InfirmaryHealingIntervalSeconds, InfirmaryHealingAmount);
    }

    /// <summary>
    ///     보건실에 있는 플레이어들의 정신오염도 감소 처리
    /// </summary>
    private void ProcessInfirmaryHealing(object? state)
    {
        try
        {
            // 보건실(Classroom3)에 있고 Corruption > 0인 플레이어 찾기
            var playersInInfirmary = _clientSessions.Values
                .Where(s => s is { PlayerId: not null, CurrentArea: AreaType.Classroom3, Corruption: > 0 })
                .ToList();

            foreach (var session in playersInInfirmary) session.ModifyStats(corruptionDelta: -InfirmaryHealingAmount);
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "Error processing infirmary healing");
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
                _exitInstanceManager,
                _itemPoolManager,
                _corridorRuleManager,
                _interactRuleManager,
                _doorStateManager,
                _sabotageManager);

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
}
