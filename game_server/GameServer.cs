using System.Buffers.Binary;
using System.Collections.Concurrent;
using System.Diagnostics;
using System.Net;
using game_server.admin.dto;
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
using network.contracts.authentication;
using network.contracts.messaging;
using network.contracts.scaling;
using network.core;
using network.helpers;
using network.hosting;
using network.infrastructure;
using network.interfaces;
using network.packets;

namespace game_server;

public partial class GameServer(
    IConfiguration configuration,
    ILogger<GameServer> logger,
    INatsClientFactory natsClientFactory,
    ICacheHelper cacheHelper,
    MatchingLifecycleOutboxStore matchingLifecycleOutboxStore,
    INetworkService networkService,
    ServerConfig serverConfig,
    IGameHandoffTicketService gameHandoffTicketService,
    GameServerScalingOptions scalingOptions,
    GameServerNodeLease gameServerNodeLease,
    IHostApplicationLifetime applicationLifetime,
    ServerReadinessState readinessState)
    : IHostedService
{
    private const int HeartbeatCheckIntervalSeconds = 10;
    private const int MatchingLifecycleTerminalMatchRetention = 4096;
    private const int StaticMatchingLifecycleEnqueueAttempts = 5;
    private const int StaticMatchingLifecycleDirectPublishAttempts = 3;
    private static readonly TimeSpan StaticMatchingLifecycleDirectPublishTimeout =
        TimeSpan.FromSeconds(5);
    private static readonly TimeSpan ShutdownStageTimeout = TimeSpan.FromSeconds(5);

    private readonly GameSessionRegistry _sessionRegistry = new();
    private readonly DoorStateManager _doorStateManager = new();
    private readonly InGameInventoryManager _inGameInventoryManager = new();

    private readonly InteractableStateManager _interactableStateManager = new();
    private readonly AreaItemStockManager _areaItemStockManager =
        new(naturalExploreLootEnabled: !Config.MONSTER_SUMMON_ECONOMY_ENABLED);
    private readonly GroundItemManager _groundItemManager = new();
    private readonly SwarmMonsterDirector _swarmMonsterDirector = new();

    // #294 후속 — Swarm 상태 홀더는 matchingId 소유 런타임 아래에서 함께 생성·제거한다.
    private readonly SwarmMatchRuntimeStore _swarmMatchRuntimes = new();
    private readonly SummonStoneManager _summonStoneManager = new();
    private readonly MatchRosterManager _matchRosterManager = new(logger);
    private AreaClosureManager _areaClosureManager = null!;
    private readonly BotPlayerManager _botPlayerManager = new(logger);
    private readonly GameEventLogManager _gameEventLogManager = new();
    private readonly MatchSummaryFileStore _matchSummaryFileStore = new(
        configuration["MATCH_SUMMARY_DIRECTORY"],
        configuration.GetValue<int>("MATCH_SUMMARY_MAX_FILES", MatchSummaryFileStore.DefaultMaxSummaries));
    private readonly EncounterRevealManager _encounterRevealManager = new();
    private readonly ConcurrentDictionary<long, int> _lastMatchStartCountdownBroadcast = new();
    private readonly ConcurrentDictionary<long, ConcurrentDictionary<long, string>>
        _matchingLifecycleTerminalSubjects = new();
    private readonly ConcurrentQueue<long> _matchingLifecycleTerminalMatchOrder = new();
    private readonly MatchRuntimeRegistry _matchRuntimeRegistry = new();
    private MatchRuntimeCleanupCoordinator _matchRuntimeCleanupCoordinator = null!;
    private readonly ConcurrentDictionary<long, Task> _pendingMatchOwnerLossTasks = new();
    private readonly ConcurrentDictionary<long, Task> _pendingMatchingRedisCleanupTasks = new();
    private readonly ConcurrentDictionary<long, Task> _pendingMatchingLifecyclePublishTasks = new();
    private readonly ConcurrentDictionary<long, MatchingLifecyclePersistenceState>
        _matchingLifecyclePersistenceStates = new();
    private readonly object _matchingLifecycleEnqueueGate = new();
    // 재시작해도 되감기지 않도록 기동 시각을 섞는다. 고정 시드로 시작하면 서버를 다시
    // 올릴 때마다 같은 matchingId가 나오고, 매치 요약 파일이 같은 이름을 만나
    // 저장이 통째로 건너뛰어진다(기존 파일 우선 규칙).
    private long _adminBotOnlyMatchingIdSeed =
        9_000_000 + DateTime.UtcNow.Ticks / TimeSpan.TicksPerSecond % 900_000;
    private long _adminBotOnlyPlayerIdSeed = -900_000_000;
    private INatsClient? _matchingLifecycleNatsClient;
    private MatchingLifecycleOutboxWorker? _matchingLifecycleOutboxWorker;
    private long _nextMatchingLifecyclePublishId;
    private int _acceptingMatchingLifecycleEnqueues;
    private int _stopping;

    private bool IsDurableMatchingLifecycleEnabled =>
        scalingOptions.Enabled || configuration.GetValue("userServerScaling:enabled", false);

    private CancellationTokenSource _cts = new();
    private Timer? _heartbeatCheckTimer;
    private Timer? _resourceTickTimer;        // 폐쇄 구역 등 주기성 자원 변화
    private Timer? _areaClosureTickTimer;     // 구역 폐쇄 체크
    private Timer? _targetLocationTimer;      // 타겟 위치 전송
    private Timer? _botMovementTimer;         // #127 봇 walking step (250ms)

    // 자원 틱 설정 (GDD v0.0.5 확정 수치)
    private int _botMovementProcessing;

    // 봇 이동 틱 계측 (#207) — 틱이 밀려 스킵되면 봇 위치 브로드캐스트 간격이 벌어진다.
    private int _botMovementTickSkips;
    private int _botMovementTickCount;
    private double _botMovementTickTotalMs;
    private readonly List<double> _botMovementSnapshotSamples = new(200);
    private readonly List<double> _botMovementPlanningSamples = new(200);
    private readonly List<double> _botMovementWalkingSamples = new(200);
    private readonly List<double> _botMovementBroadcastSamples = new(200);
    private double _botMovementTickMaxMs;
    private readonly List<double> _botMovementTickSamples = new(200);
    private int _botMovementConsecutiveSkips;
    private int _botMovementMaxConsecutiveSkips;

    private sealed class MatchingLifecyclePersistenceState
    {
        public object SyncRoot { get; } = new();
        public int PendingCount { get; set; }
        public bool ClosingRequested { get; set; }
        public bool Sealed { get; set; }
        public bool PersistenceFailed { get; set; }
        public bool OwnerReleaseCompleted { get; set; }
        public TaskCompletionSource<bool>? Quiesced { get; set; }
    }

    private SwarmMatchRuntime GetSwarmMatchRuntime(long matchingId) =>
        _swarmMatchRuntimes.GetOrCreate(matchingId);

    private bool IsSwarmFrontOrbDamaged(long matchingId, long playerId) =>
        _swarmMatchRuntimes.TryGet(matchingId, out var runtime) &&
        runtime.Pacing.FrontOrbHp.ContainsKey((matchingId, playerId));

    internal const int ResourceTickIntervalSeconds = 5;
    // ClosedAreaStaminaPenaltyPerTick 제거 — v0.1.9 #66: 폐쇄 구역 패널티 → 오염도로 변경

    public async Task StartAsync(CancellationToken cancellationToken)
    {
        Volatile.Write(ref _stopping, 0);
        StopAcceptingMatchingLifecycleEnqueues();
        readinessState.MarkNotReady("starting");
        _cts.Dispose();
        _cts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);

        try
        {
            logger.LogInformation("Game server starting...");

            InitializeServices();

            StartTcpServer();
            StartHeartbeatChecker();
            StartResourceTickTimer();
            StartAreaClosureTickTimer();
            StartTargetLocationTimer();
            StartBotMovementTimer();
            StartProximityAutoCombatTimer();
            await gameServerNodeLease.StartAsync(
                () => _matchRuntimeRegistry.ActiveCount,
                OnGameServerRoutingLeaseLost,
                OnGameServerMatchOwnerLost,
                cancellationToken);

            readinessState.MarkReady();
            logger.LogInformation("Game server started successfully.");
        }
        catch (Exception ex)
        {
            readinessState.MarkNotReady("startup_failed");
            logger.LogError(ex, "Game server starting failed.");
            Volatile.Write(ref _stopping, 1);
            try
            {
                await StopAsync(CancellationToken.None);
            }
            catch (Exception shutdownException)
            {
                logger.LogWarning(
                    shutdownException,
                    "Game server cleanup after startup failure did not complete cleanly.");
            }
            throw;
        }
    }

    public async Task StopAsync(CancellationToken cancellationToken)
    {
        if (scalingOptions.Enabled && Volatile.Read(ref _stopping) == 0)
        {
            readinessState.MarkNotReady("draining");
            logger.LogInformation("Game server beginning routing drain...");
            try
            {
                await gameServerNodeLease.BeginDrainAsync();
                await WaitForScalingDrainAsync();
            }
            catch (Exception ex)
            {
                logger.LogWarning(ex, "GameServer routing drain failed; proceeding with bounded shutdown.");
            }
        }

        // Publish the stopping state before taking the session snapshot. Sessions accepted at
        // this boundary observe the same penalty-free claim-release policy in OnDisconnect.
        Volatile.Write(ref _stopping, 1);
        readinessState.MarkNotReady("stopping");
        logger.LogInformation("Game server stopping...");

        // 서버 셧다운 시 모든 세션을 서버 주도 종료로 마킹 → 페널티 면제
        foreach (var session in _sessionRegistry.SnapshotAll())
            session.MarkServerInitiatedDisconnect();

        // Host cancellation must not skip later cleanup stages. Log slow stages at a fixed
        // threshold, but keep dependencies alive until the stage actually quiesces.
        await RunShutdownStageAsync(
            networkService.StopAsync(CancellationToken.None),
            "network connections");
        await RunShutdownStageAsync(_cts.CancelAsync(), "server cancellation");

        Timer?[] timers =
        [
            _heartbeatCheckTimer,
            _resourceTickTimer,
            _areaClosureTickTimer,
            _targetLocationTimer,
            _botMovementTimer,
            _proximityAutoCombatTimer
        ];
        _heartbeatCheckTimer = null;
        _resourceTickTimer = null;
        _areaClosureTickTimer = null;
        _targetLocationTimer = null;
        _botMovementTimer = null;
        _proximityAutoCombatTimer = null;
        await RunShutdownStageAsync(
            Task.WhenAll(timers.Where(timer => timer != null)
                .Select(timer => timer!.DisposeAsync().AsTask())),
            "timers");

        await RunShutdownStageAsync(
            WaitForPendingMatchOwnerLossesAsync(),
            "match owner loss");
        await RunShutdownStageAsync(
            gameServerNodeLease.QuiesceHeartbeatsAsync(),
            "routing heartbeat quiesce");
        await RunShutdownStageAsync(
            WaitForPendingMatchOwnerLossesAsync(),
            "quiesced match owner loss");
        await RunShutdownStageAsync(
            WaitForPendingMatchingLifecyclePublishesAsync(),
            "matching lifecycle outbox persistence");
        await RunShutdownStageAsync(
            WaitForPendingMatchingRedisCleanupsAsync(),
            "matching Redis cleanup");
        StopAcceptingMatchingLifecycleEnqueues();
        await RunShutdownStageAsync(
            WaitForPendingMatchingLifecyclePublishesAsync(),
            "enqueue gate close");
        await RunShutdownStageAsync(
            WaitForPendingMatchingRedisCleanupsAsync(),
            "enqueue gate matching Redis cleanup");
        await RunShutdownStageAsync(gameServerNodeLease.StopAsync(), "routing lease");
        await RunShutdownStageAsync(
            WaitForPendingMatchOwnerLossesAsync(),
            "late match owner loss");
        await RunShutdownStageAsync(
            WaitForPendingMatchingLifecyclePublishesAsync(),
            "late matching lifecycle outbox persistence");
        await RunShutdownStageAsync(
            WaitForPendingMatchingRedisCleanupsAsync(),
            "late matching Redis cleanup");
        await RunShutdownStageAsync(
            StopMatchingLifecycleOutboxWorkerAsync(),
            "matching lifecycle outbox worker");

        _cts.Dispose();
        await CloseMatchingLifecycleNatsClientAsync();

        logger.LogInformation("Game server stopped.");
    }

    private async Task WaitForScalingDrainAsync()
    {
        DateTime deadlineUtc = DateTime.UtcNow + scalingOptions.DrainTimeout;
        while (DateTime.UtcNow < deadlineUtc)
        {
            int localActiveMatches = _matchRuntimeRegistry.ActiveCount;
            int reservedOrActiveMatches = await gameServerNodeLease.GetOwnedMatchCountAsync()
                .WaitAsync(TimeSpan.FromSeconds(2));
            if (localActiveMatches == 0 && reservedOrActiveMatches == 0)
            {
                logger.LogInformation("GameServer routing drain completed.");
                return;
            }

            await Task.Delay(TimeSpan.FromMilliseconds(200));
        }

        logger.LogWarning(
            "GameServer routing drain timed out: Timeout={Timeout}, LocalActiveMatches={LocalActiveMatches}",
            scalingOptions.DrainTimeout,
            _matchRuntimeRegistry.ActiveCount);
    }

    private void OnGameServerRoutingLeaseLost()
    {
        readinessState.MarkNotReady("routing_lease_lost");
        Volatile.Write(ref _stopping, 1);
        applicationLifetime.StopApplication();
    }

    private void OnGameServerMatchOwnerLost(long matchingId, IReadOnlyList<long> handoffPlayerIds)
    {
        List<GameClientSession> affectedSessions = GetSessionsByMatch(matchingId);
        var playerIdsToPublish = handoffPlayerIds
            .Where(playerId => playerId > 0)
            .ToHashSet();
        foreach (GameClientSession session in affectedSessions)
        {
            if (!session.PlayerId.HasValue)
                continue;

            if (session.TryMarkMatchingLifecycleHandledExternally())
                playerIdsToPublish.Add(session.PlayerId.Value);
            else
                playerIdsToPublish.Remove(session.PlayerId.Value);
        }

        long[] playerIds = playerIdsToPublish.ToArray();
        Task handlerTask = Task.Run(() =>
            HandleGameServerMatchOwnerLost(matchingId, affectedSessions, playerIds));
        _pendingMatchOwnerLossTasks[matchingId] = handlerTask;
        _ = handlerTask.ContinueWith(
            _ => ((ICollection<KeyValuePair<long, Task>>)_pendingMatchOwnerLossTasks)
                .Remove(new KeyValuePair<long, Task>(matchingId, handlerTask)),
            CancellationToken.None,
            TaskContinuationOptions.ExecuteSynchronously,
            TaskScheduler.Default);
    }

    private void HandleGameServerMatchOwnerLost(
        long matchingId,
        IReadOnlyList<GameClientSession> affectedSessions,
        IReadOnlyCollection<long> affectedPlayerIds)
    {
        logger.LogError(
            "Aborting match after its distributed GameServer owner fence was lost: MatchingId={MatchingId}, Players={PlayerCount}",
            matchingId,
            affectedPlayerIds.Count);

        Action? persistenceRegistration =
            BeginMatchingLifecyclePersistenceRegistration(matchingId);
        try
        {
            bool cleanupAccepted = TryCleanupMatchRuntime(matchingId, null, null);
            if (!cleanupAccepted && !_matchRuntimeRegistry.IsTerminal(matchingId))
            {
                logger.LogCritical(
                    "Match owner fence was lost but local runtime cleanup could not start: MatchingId={MatchingId}",
                    matchingId);
            }

            foreach (long playerId in affectedPlayerIds)
            {
                PublishMatchingLifecycle(
                    MatchingLifecycleSubjects.PlayerAdmissionFailed,
                    playerId,
                    matchingId);
            }
        }
        finally
        {
            persistenceRegistration?.Invoke();
        }

        foreach (GameClientSession session in affectedSessions)
            session.DisconnectForAdmissionFailure();
    }

    private async Task WaitForPendingMatchOwnerLossesAsync()
    {
        while (true)
        {
            Task[] pendingTasks = _pendingMatchOwnerLossTasks.Values.ToArray();
            if (pendingTasks.Length == 0)
                return;

            await Task.WhenAll(pendingTasks);
        }
    }

    private async Task RunShutdownStageAsync(Task operation, string stage)
    {
        try
        {
            await operation.WaitAsync(ShutdownStageTimeout);
        }
        catch (TimeoutException ex)
        {
            logger.LogWarning(ex, "Shutdown stage exceeded warning threshold: Stage={Stage}, Timeout={Timeout}",
                stage, ShutdownStageTimeout);
            try
            {
                await operation;
            }
            catch (Exception completionException)
            {
                logger.LogWarning(completionException, "Shutdown stage failed after timeout: Stage={Stage}", stage);
            }
        }
        catch (Exception ex)
        {
            logger.LogWarning(ex, "Shutdown stage failed: Stage={Stage}", stage);
        }
    }

    private void InitializeServices()
    {
        string natsEndpoint = configuration.GetRequiredString("natsEndPoint");

        _areaClosureManager = new AreaClosureManager(logger);
        _matchRuntimeCleanupCoordinator = new MatchRuntimeCleanupCoordinator(
            _matchRuntimeRegistry,
            [
                new MatchRuntimeCleanupStep(
                    "session runtime",
                    GameClientSession.CleanupAbandonedMatchingRuntime),
                new MatchRuntimeCleanupStep(
                    "session index",
                    _sessionRegistry.RemoveMatch),
                new MatchRuntimeCleanupStep(
                    "countdown broadcast",
                    matchingId => _lastMatchStartCountdownBroadcast.TryRemove(matchingId, out _)),
                new MatchRuntimeCleanupStep(
                    "settlement",
                    CleanupMatchSettlementState),
                new MatchRuntimeCleanupStep(
                    "swarm arena",
                    CleanupSwarmArenaState),
                new MatchRuntimeCleanupStep(
                    "area closure",
                    _areaClosureManager.CleanupMatching),
                new MatchRuntimeCleanupStep(
                    "bots",
                    _botPlayerManager.CleanupMatching),
                new MatchRuntimeCleanupStep(
                    "area item stock",
                    _areaItemStockManager.RemoveMatchingState),
                new MatchRuntimeCleanupStep(
                    "ground items",
                    _groundItemManager.RemoveMatchingState),
                new MatchRuntimeCleanupStep(
                    "monster broadcast slots",
                    CleanupSwarmAfterimageMonsterRuntime),
                new MatchRuntimeCleanupStep(
                    "summon stones",
                    _summonStoneManager.RemoveMatchingState),
                new MatchRuntimeCleanupStep(
                    "inventory",
                    _inGameInventoryManager.RemoveMatchingState),
                new MatchRuntimeCleanupStep(
                    "interactables",
                    _interactableStateManager.RemoveMatchingState),
                new MatchRuntimeCleanupStep(
                    "doors",
                    _doorStateManager.ClearMatching),
                new MatchRuntimeCleanupStep(
                    "roster",
                    _matchRosterManager.CleanupMatching),
                new MatchRuntimeCleanupStep(
                    "encounter reveal",
                    _encounterRevealManager.CleanupMatching),
                new MatchRuntimeCleanupStep(
                    "event log",
                    _gameEventLogManager.Clear),
                new MatchRuntimeCleanupStep(
                    "Redis cleanup scheduling",
                    StartMatchingRedisCleanup)
            ],
            logger);
        // M4: 폐쇄 구역은 스웜 신규 스폰을 멈춘다 (잔존 몹은 ReclaimStrandedMonsters가 걷어냄)
        _swarmMonsterDirector.IsAreaClosedResolver =
            (matchingId, area) => _areaClosureManager.IsAreaClosed(matchingId, area);
        // 인트로 산개 (2026-08-16): 카운트다운 동안에는 전 방을 공급 대상으로 열어
        // 운동장에서 열 방향으로 실제 몹이 뻗어 나가게 한다.
        _swarmMonsterDirector.IsGameplayActiveResolver = MatchStartGate.IsGameplayActive;
        // 무오브 우선 표적 (2026-08-16 유저 명세): 잔상 주인 배정·재배정이 이걸 본다.
        // 무오브는 자동 공격도 절단도 못 하므로, 잔상까지 남을 쫓으면 재건하는 동안
        // 아무 압력도 안 받아 무오브가 안전지대가 된다.
        _swarmMonsterDirector.IsPlayerOrblessResolver =
            (matchingId, playerId) => !HasAnySquadOrb(matchingId, playerId);

        try
        {
            natsClientFactory.Initialize(natsEndpoint);
            _matchingLifecycleNatsClient = natsClientFactory.Create();
            if (IsDurableMatchingLifecycleEnabled)
            {
                _matchingLifecycleNatsClient.EnsureDurableStream(new NatsDurableStreamOptions
                {
                    Name = MatchingLifecycleSubjects.Stream,
                    Subjects = [MatchingLifecycleSubjects.AllPlayerEvents],
                    Description = "Durable matching lifecycle events consumed by the UserServer cluster"
                });
                var outboxWorker = new MatchingLifecycleOutboxWorker(
                    matchingLifecycleOutboxStore,
                    _matchingLifecycleNatsClient,
                    logger);
                _matchingLifecycleOutboxWorker = outboxWorker;
                outboxWorker.Start();
                StartAcceptingMatchingLifecycleEnqueues();
            }
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
            // 문 상태는 페이즈와 별개다 (2026-08-16). 위 제공자는 ROOM_COMBAT에서만 채워져
            // 군집 모드에서는 항상 비었고, 그래서 봇이 잠긴 문을 그냥 통과했다.
            _botPlayerManager.SetDoorOpenResolver(_doorStateManager.IsDoorOpen);
            // 투사체 회피 반사 (#232 §9): 봇 걸음마다 교차사격 스냅샷을 물어 비켜설 방향을 받는다.
            _botPlayerManager.SetSwarmDodgeResolver(ResolveSwarmBotDodgeDirection);
            _interactableStateManager.Initialize(log);
            _inGameInventoryManager.Initialize(log);
        }
        catch (Exception ex)
        {
            throw new InvalidOperationException("Failed to initialize services.", ex);
        }
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

    // ===== 자원 틱 (폐쇄 구역 등 주기성 자원 변화) =====

    private void StartResourceTickTimer()
    {
        _resourceTickTimer = new Timer(ProcessResourceTick, null,
            TimeSpan.FromSeconds(ResourceTickIntervalSeconds),
            TimeSpan.FromSeconds(ResourceTickIntervalSeconds));
        logger.LogInformation("자원 틱 타이머 시작 ({Interval}초)", ResourceTickIntervalSeconds);
    }

    private void ProcessResourceTick(object? state)
    {
        try
        {
            var activeSessions = _sessionRegistry.SnapshotWhere(
                static session => session.PlayerId.HasValue && !session.IsEliminated);

            // Environmental damage and eliminations are settled per matching below.
            var matchingIds = GetActiveMatchingIds();

            foreach (long matchingId in matchingIds)
            {
                // #222 M3-2: 폐쇄·오버타임 오염은 이 정산 틱이 적용한다.
                if (MatchStartGate.IsGameplayActive(matchingId))
                    ProcessResourceTickForMatching(matchingId, activeSessions);
            }

        }
        catch (Exception ex)
        {
            logger.LogError(ex, "자원 틱 처리 중 오류");
        }
    }

    /// <summary>
    ///     #26: 봇 탈락 처리 + 게임 종료 판정.
    ///     MatchRosterManager.TryEliminatePlayer로 로스터에 탈락 사유·순위를 기록하고 전체에 브로드캐스트한다.
    /// </summary>
    private void ProcessBotElimination(long matchingId, long botId, EliminationReason reason,
        List<GameClientSession> activeSessions, long attackerPlayerId = 0, bool isAreaClosureElimination = false,
        bool isOvertimeElimination = false, bool deferGameOver = false, int forcedRank = 0)
    {
        try
        {
            var eliminatedBot = _botPlayerManager.GetBot(matchingId, botId);
            AreaType eliminatedArea = eliminatedBot?.CurrentArea ?? AreaType.None;
            int finalOrbTier = _inGameInventoryManager.GetEquippedBattleItemTier(matchingId, botId);
            var transition = _matchRosterManager.TryEliminatePlayer(matchingId, botId, reason,
                attackerPlayerId, eliminatedArea, isAreaClosureElimination, isOvertimeElimination, forcedRank,
                finalOrbTier);
            if (!transition.Applied)
            {
                logger.LogDebug(
                    "Duplicate bot elimination ignored: MatchingId={MatchingId}, BotId={BotId}, Reason={Reason}",
                    matchingId, botId, reason);
                return;
            }

            var affected = transition.AffectedPlayers;
            _groundItemManager.ReleaseClaimReservationsForPlayer(matchingId, botId);
            _gameEventLogManager.LogElimination(
                matchingId,
                botId,
                reason.ToString(),
                isBot: true,
                attackerPlayerId: attackerPlayerId,
                isAreaClosureElimination: isAreaClosureElimination,
                isOvertimeElimination: isOvertimeElimination);
            var matchingSessions = GetSessionsByMatch(matchingId);
            DropBotInventoryAtCurrentPosition(matchingId, botId, matchingSessions);

            // 1) 전체에게 봇 탈락 알림 (G_TO_C_PLAYER_ELIMINATED)
            using (var eliminatedPacket = Packet.Create((int)Protocol.G_TO_C_PLAYER_ELIMINATED))
            {
                var eliminatedMsg = new G_TO_C_PLAYER_ELIMINATED
                {
                    PlayerId = botId,
                    AttackerPlayerId = attackerPlayerId,
                    Reason = reason
                };
                eliminatedPacket.SetBody(MessagePackSerializer.Serialize(eliminatedMsg));
                foreach (var s in matchingSessions) s.Send(eliminatedPacket);
            }

            // 2) 영향받는 봇/세션 상태 동기화
            foreach (var (affectedId, newStatus) in affected)
            {
                // 봇 영향
                var bot = _botPlayerManager.GetBot(matchingId, affectedId);
                if (bot == null) continue;
                if (newStatus == PlayerMatchStatus.ELIMINATED)
                {
                    bot.IsEliminated = true;
                    bot.PlayerMatchStatus = PlayerMatchStatus.SPECTATING;
                }
                else
                {
                    bot.PlayerMatchStatus = newStatus;
                }
            }


            // 3) 게임 종료 판정 — 봇 탈락으로 최후 1인 결정 가능
            var (isGameOver, winnerId) = _matchRosterManager.CheckGameOver(matchingId);
            if (!deferGameOver && isGameOver && matchingSessions.Count > 0)
            {
                logger.LogInformation("게임 종료(봇 탈락 후): MatchingId={MatchingId}, Winner={WinnerId}",
                    matchingId, winnerId);
                matchingSessions[0].TryEndMatch(winnerId ?? 0, "last_survivor_after_combat");
            }
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "봇 탈락 처리 중 오류: BotId={BotId}", botId);
        }
    }

    private void DropBotInventoryAtCurrentPosition(
        long matchingId,
        long botPlayerId,
        IReadOnlyCollection<GameClientSession> matchingSessions)
    {
        var outcome = EliminationInventoryDropper.DropBotInventoryWithLogs(
            _botPlayerManager, _inGameInventoryManager, _groundItemManager, _gameEventLogManager,
            matchingId, botPlayerId);
        if (outcome == null)
            return;

        var (bot, drop) = outcome;
        BroadcastGroundItemSpawnChunked(
            matchingId, bot.CurrentArea, drop.SpawnedItems,
            matchingSessions.Where(session => session.CurrentArea == bot.CurrentArea));

        logger.LogInformation(
            "Bot elimination inventory scattered: MatchingId={MatchingId}, BotId={BotId}, Area={Area}, ItemCount={ItemCount}",
            matchingId, botPlayerId, bot.CurrentArea, drop.DroppedItemIds.Count);
    }
    // 바닥 아이템 스폰 브로드캐스트는 반드시 청크로 나눈다 (#229).
    // 단일 패킷은 버퍼 2048에 묶여 있는데 탈락 드롭은 개수가 열려 있다 — 오브 상한이 99로
    // 오르고 소환석 예산이 커지면서 실제로 넘겼다(실측 2091, 봇 탈락 처리 전체가 예외로 죽어
    // 드롭이 통째로 사라졌다). SpotArena가 #222에서 같은 이유로 8개씩 나눈 전례를 따른다.
    private const int GroundItemSpawnBroadcastChunkSize = 8;

    private void BroadcastGroundItemSpawnChunked(
        long matchingId, AreaType area, IReadOnlyList<GroundItemInfo> spawned,
        IEnumerable<GameClientSession> targets)
    {
        if (spawned == null || spawned.Count == 0 || area == AreaType.None)
            return;

        var receivers = targets.ToList();
        if (receivers.Count == 0)
            return;

        int remaining = _areaItemStockManager.GetRemainingCount(matchingId, (int)area);
        for (int offset = 0; offset < spawned.Count; offset += GroundItemSpawnBroadcastChunkSize)
        {
            var chunk = spawned.Skip(offset).Take(GroundItemSpawnBroadcastChunkSize).ToList();
            using var packet = PacketMaker.G_TO_C_GROUND_ITEM_SPAWN((int)area, remaining, chunk);
            foreach (var session in receivers)
                session.Send(packet);
        }
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

    private void BroadcastBotBattleItemEquips(long matchingId,
IReadOnlyCollection<(long botPlayerId, int itemId)> equips,
IReadOnlyCollection<GameClientSession> activeSessions)
    {
        foreach (var (botPlayerId, _) in equips)
        {
            var bot = _botPlayerManager.GetBot(matchingId, botPlayerId);
            var botInfo = _botPlayerManager.SynthesizePlayerInfo(matchingId, botPlayerId);
            if (bot == null || botInfo == null) continue;

            var sameAreaSessions = activeSessions
                .Where(session => session.PlayerId.HasValue &&
                                  session.CurrentMapSubId == matchingId &&
                                  session.CurrentArea == bot.CurrentArea)
                .ToList();
            if (sameAreaSessions.Count == 0) continue;

            using var packet = PacketMaker.G_TO_C_PLAYER_INFO([botInfo]);
            foreach (var session in sameAreaSessions)
                session.Send(packet);
        }
    }
    /// <summary>
    ///     #125: 봇 이동 이벤트를 같은 매칭의 영향권 인간 세션에 패킷 브로드캐스트.
    ///     - 영역 전환: G_TO_C_AREA_PLAYER_LEAVE(이전 영역) + G_TO_C_AREA_PLAYER_ENTER(새 영역) + G_TO_C_MOVE(텔레포트)
    ///     - 영역 내 wander: G_TO_C_MOVE(같은 영역)
    /// </summary>
    private void BroadcastBotGroundItemPickups(
        long matchingId,
        IReadOnlyCollection<BotGroundItemPickup> pickups,
        IReadOnlyCollection<GameClientSession> activeSessions)
    {
        foreach (var pickup in pickups)
        {
            if (pickup.CorruptionRecovery > 0)
                _gameEventLogManager.RecordRecovery(matchingId, pickup.BotPlayerId, pickup.CorruptionRecovery);
            if (pickup.AutoUsed)
            {
                _gameEventLogManager.LogRecoveryUse(
                    matchingId, pickup.BotPlayerId, pickup.Item.ItemId, pickup.EffectiveRecovery,
                    source: "ground_auto_use", isBot: true);
                _gameEventLogManager.LogPelletPickupOutcome(
                    matchingId, pickup.BotPlayerId, pickup.Item.ItemId, pickup.RequestedRecovery, pickup.EffectiveRecovery,
                    pickup.EffectiveRecovery == 0 ? "wasted" :
                    pickup.EffectiveRecovery == pickup.RequestedRecovery ? "effective" : "partial_waste",
                    isBot: true);
            }

            var area = (AreaType)pickup.Item.AreaType;
            _gameEventLogManager.LogGroundItemPickup(
                matchingId,
                pickup.BotPlayerId,
                pickup.DiscovererPlayerId,
                pickup.Item.GroundItemUid,
                pickup.Item.ItemId,
                area.ToString(),
                pickup.AutoUsed,
                isBot: true);
            if (pickup.SummonStoneAmount > 0)
            {
                _gameEventLogManager.LogSummonStoneAward(
                    matchingId,
                    pickup.BotPlayerId,
                    monsterId: 0,
                    pickup.SummonStoneAmount,
                    pickup.SummonStoneBalance,
                    area.ToString(),
                    isCore: false,
                    isBot: true);
            }
            else
            {
                var boardAfterPickup = _inGameInventoryManager.GetPlayerInventory(matchingId, pickup.BotPlayerId);
                _gameEventLogManager.LogOrbBoardTransition(
                    matchingId, pickup.BotPlayerId, boardAfterPickup.GetAllItems(),
                    boardAfterPickup.GetEquippedBattleItem()?.ItemId ?? 0, area.ToString(), "pickup", isBot: true);
            }
            using var packet = PacketMaker.G_TO_C_GROUND_ITEM_REMOVED(
                pickup.Item.GroundItemUid,
                pickup.BotPlayerId,
                pickup.AutoUsed);
            foreach (var session in activeSessions.Where(session =>
                         session.CurrentMapSubId == matchingId && session.CurrentArea == area))
            {
                session.Send(packet);
            }
        }

        var autoEquips = pickups
            .Where(pickup => pickup.AutoEquippedItemId > 0)
            .Select(pickup => (pickup.BotPlayerId, pickup.AutoEquippedItemId))
            .ToList();
        if (autoEquips.Count > 0)
            BroadcastBotBattleItemEquips(matchingId, autoEquips, activeSessions);
    }
    private void BroadcastBotMovement(long matchingId, BotMovementEvent ev,
        List<GameClientSession> activeSessions)
    {
        var matchingSessions = activeSessions
            .Where(s => s.PlayerId.HasValue && s.CurrentMapSubId == matchingId)
            .ToList();

        long serverTimestamp = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();

        if (ev.IsAreaTransition)
        {

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
        float orbOrbitPhase = _botPlayerManager.GetBot(matchingId, ev.BotPlayerId)?.OrbOrbitPhaseDegrees
                              ?? SwarmOrbOrbit.InitialPhaseDegrees(ev.BotPlayerId);
        using var movePacket = PacketMaker.G_TO_C_MOVE(
            ev.BotPlayerId,
            ev.Position,
            ev.Velocity,
            ev.Rotation,
            ev.ToCell,
            lastProcessedInput: 0u,
            serverTimestamp,
            orbOrbitPhase);
        foreach (var session in matchingSessions)
        {
            if (session.CurrentArea != ev.ToArea) continue;
            session.Send(movePacket);
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

    // ===== 구역 폐쇄 틱 =====

    private void StartAreaClosureTickTimer()
    {
        // 1초 간격 — 클라 카운트다운 종료 시점과 실제 폐쇄 트리거 사이 지연을 최소화
        _areaClosureTickTimer = new Timer(ProcessAreaClosureTick, null,
            TimeSpan.FromSeconds(1), TimeSpan.FromSeconds(1));
        logger.LogInformation("구역 폐쇄 타이머 시작 (1초 간격)");
    }

    private void BroadcastDoorStateChanges(
        IReadOnlyCollection<GameClientSession> sessions,
        IEnumerable<int> doorIds,
        bool isOpen,
        long openerPlayerId)
    {
        foreach (int doorId in doorIds.Distinct())
        {
            using var packet = PacketMaker.G_TO_C_DOOR_STATE_UPDATE(
                doorId,
                isOpen,
                ErrorCode.SUCCESS,
                openerPlayerId);
            foreach (var session in sessions)
                session.Send(packet);
        }
    }

    private void ProcessAreaClosureTick(object? state)
    {
        try
        {
            // 매칭별로 폐쇄 스케줄 체크
            var matchingIds = GetActiveMatchingIds();

            foreach (long matchingId in matchingIds)
            {
                // #272 자기장 폐쇄: 자기장에서 파생한 구역 시간표 하나로만 닫는다 —
                // 필드 오염은 정산 리소스 틱(GetSwarmFieldCorruptionPerTick)이 준다.
                if (!MatchStartGate.IsGameplayActive(matchingId)) continue;
                _matchRuntimeRegistry.TryExecute(
                    matchingId,
                    () => ProcessSwarmScheduledClosureTick(matchingId));
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
            var activeSessions = _sessionRegistry.SnapshotWhere(
                static session => session.PlayerId.HasValue && session.TargetPlayerId != 0 && !session.IsGameEnded);

            foreach (var session in activeSessions)
            {
                session.SendTargetLocation();
            }
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

    private void ProcessBotMovement(object? state)
    {
        if (System.Threading.Interlocked.Exchange(ref _botMovementProcessing, 1) == 1)
        {
            // 틱이 50ms를 넘기면 다음 틱이 통째로 스킵되어 봇 위치 브로드캐스트 간격이
            // 50ms와 100ms를 오간다. 클라 보간이 그대로 튀므로 빈도를 계측한다.
            System.Threading.Interlocked.Increment(ref _botMovementTickSkips);
            int consecutiveSkips = System.Threading.Interlocked.Increment(ref _botMovementConsecutiveSkips);
            UpdateMaximum(ref _botMovementMaxConsecutiveSkips, consecutiveSkips);
            return;
        }

        var botMovementTickStartedAt = DateTime.UtcNow;
        double snapshotElapsedMilliseconds = 0d;
        double planningElapsedMilliseconds = 0d;
        double walkingElapsedMilliseconds = 0d;
        double broadcastElapsedMilliseconds = 0d;
        try
        {
            var activeSessions = _sessionRegistry.SnapshotWhere(static session => session.PlayerId.HasValue);
            var matchingIds = GetActiveMatchingIds();
            BroadcastMatchStartCountdowns(matchingIds, activeSessions);

            foreach (long matchingId in matchingIds)
            {
                if (!MatchStartGate.IsGameplayActive(matchingId)) continue;
                if (!MatchStartGate.IsGameplayActive(matchingId)) continue;
                if (!_botPlayerManager.HasBots(matchingId)) continue;
                // 봇 배회 목적지(타겟 추적·흩어지기)를 위해 같은 매칭 인간 플레이어의 현재 영역을 넘긴다.
                _matchRuntimeRegistry.TryExecute(matchingId, () =>
                {
                    long snapshotStartedAt = Stopwatch.GetTimestamp();
                    var humanAreas = activeSessions
                        .Where(s => s.CurrentMapSubId == matchingId && s.PlayerId.HasValue)
                        .ToDictionary(s => s.PlayerId!.Value, s => s.CurrentArea);
                    // 잔상 사냥 경로(MONSTER_SUMMON_ECONOMY_ENABLED 동결)의 공급원이던
                    // SwarmAfterimageMonsterManager는 #274에서 삭제 — 플래그 부활 시 SwarmMonsterDirector에서 공급할 것.
                    IReadOnlyCollection<MonsterCombatTarget> pveTargets = [];
                    snapshotElapsedMilliseconds += Stopwatch.GetElapsedTime(snapshotStartedAt).TotalMilliseconds;

                    var movementResult = _botPlayerManager.ProcessBotMovementTick(
                        matchingId,
                        _areaClosureManager,
                        _areaItemStockManager,
                        humanAreas,
                                _inGameInventoryManager,
                        _groundItemManager,
                        pveTargets,
                        ResolveSwarmBotDirective,
                        _summonStoneManager);
                    planningElapsedMilliseconds += movementResult.PlanningElapsedMilliseconds;
                    walkingElapsedMilliseconds += movementResult.WalkingElapsedMilliseconds;

                    long broadcastStartedAt = Stopwatch.GetTimestamp();
                    foreach (var ev in movementResult.Movements)
                    {
                        // 오브 궤도 (#232): 봇도 이동한 거리만큼 돈다 — 사람 세션의 검증 이동 적산과 같은 규칙.
                        _botPlayerManager.GetBot(matchingId, ev.BotPlayerId)?.AdvanceOrbOrbit(ev.Position);
                        BroadcastBotMovement(matchingId, ev, activeSessions);
                    }
                    if (movementResult.GroundItemPickups.Count > 0)
                        BroadcastBotGroundItemPickups(matchingId, movementResult.GroundItemPickups, activeSessions);
                    broadcastElapsedMilliseconds += Stopwatch.GetElapsedTime(broadcastStartedAt).TotalMilliseconds;
                });
            }
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "봇 walking 틱 처리 중 오류");
        }
        finally
        {
            try
            {
                double botTickElapsedMs = (DateTime.UtcNow - botMovementTickStartedAt).TotalMilliseconds;
                System.Threading.Interlocked.Exchange(ref _botMovementConsecutiveSkips, 0);
                _botMovementTickSamples.Add(botTickElapsedMs);
                _botMovementSnapshotSamples.Add(snapshotElapsedMilliseconds);
                _botMovementPlanningSamples.Add(planningElapsedMilliseconds);
                _botMovementWalkingSamples.Add(walkingElapsedMilliseconds);
                _botMovementBroadcastSamples.Add(broadcastElapsedMilliseconds);
                _botMovementTickCount++;
                _botMovementTickTotalMs += botTickElapsedMs;
                if (botTickElapsedMs > _botMovementTickMaxMs) _botMovementTickMaxMs = botTickElapsedMs;
                if (_botMovementTickCount >= 200)
                {
                    var sortedSamples = _botMovementTickSamples.OrderBy(value => value).ToArray();
                    double p50Milliseconds = CalculatePercentile(sortedSamples, 0.50);
                    double p95Milliseconds = CalculatePercentile(sortedSamples, 0.95);
                    double p99Milliseconds = CalculatePercentile(sortedSamples, 0.99);
                    double snapshotP95Milliseconds = CalculatePercentile(
                        _botMovementSnapshotSamples.OrderBy(value => value).ToArray(), 0.95);
                    double planningP95Milliseconds = CalculatePercentile(
                        _botMovementPlanningSamples.OrderBy(value => value).ToArray(), 0.95);
                    double walkingP95Milliseconds = CalculatePercentile(
                        _botMovementWalkingSamples.OrderBy(value => value).ToArray(), 0.95);
                    double broadcastP95Milliseconds = CalculatePercentile(
                        _botMovementBroadcastSamples.OrderBy(value => value).ToArray(), 0.95);
                    int skippedTicks = System.Threading.Interlocked.Exchange(ref _botMovementTickSkips, 0);
                    int maxConsecutiveSkippedTicks =
                        System.Threading.Interlocked.Exchange(ref _botMovementMaxConsecutiveSkips, 0);
                    logger.LogInformation(
                        "Bot movement tick: avg={Avg:F1}ms max={Max:F1}ms skips={Skips} over {Count} ticks; " +
                        "p95 snapshot={SnapshotP95:F1}ms planning={PlanningP95:F1}ms walking={WalkingP95:F1}ms " +
                        "broadcast={BroadcastP95:F1}ms",
                        _botMovementTickTotalMs / _botMovementTickCount,
                        _botMovementTickMaxMs,
                        skippedTicks,
                        _botMovementTickCount,
                        snapshotP95Milliseconds,
                        planningP95Milliseconds,
                        walkingP95Milliseconds,
                        broadcastP95Milliseconds);
                    foreach (long matchingId in GetActiveMatchingIds()
                                 .Where(_botPlayerManager.HasBots))
                    {
                        _matchRuntimeRegistry.TryExecute(
                            matchingId,
                            () => _gameEventLogManager.LogBotMovementTickPerformance(
                                matchingId,
                                p50Milliseconds,
                                p95Milliseconds,
                                p99Milliseconds,
                                snapshotP95Milliseconds,
                                planningP95Milliseconds,
                                walkingP95Milliseconds,
                                broadcastP95Milliseconds,
                                _botMovementTickCount,
                                skippedTicks,
                                maxConsecutiveSkippedTicks));
                    }
                    _botMovementTickCount = 0;
                    _botMovementTickTotalMs = 0;
                    _botMovementTickMaxMs = 0;
                    _botMovementTickSamples.Clear();
                    _botMovementSnapshotSamples.Clear();
                    _botMovementPlanningSamples.Clear();
                    _botMovementWalkingSamples.Clear();
                    _botMovementBroadcastSamples.Clear();
                }
            }
            catch (Exception ex)
            {
                logger.LogWarning(ex, "Failed to record bot movement tick metrics");
            }
            finally
            {
                System.Threading.Volatile.Write(ref _botMovementProcessing, 0);
            }
        }
    }

    private static double CalculatePercentile(IReadOnlyList<double> sortedValues, double percentile)
    {
        if (sortedValues.Count == 0)
            return 0d;

        double position = (sortedValues.Count - 1) * Math.Clamp(percentile, 0d, 1d);
        int lowerIndex = (int)Math.Floor(position);
        int upperIndex = (int)Math.Ceiling(position);
        if (lowerIndex == upperIndex)
            return sortedValues[lowerIndex];
        double fraction = position - lowerIndex;
        return sortedValues[lowerIndex] + (sortedValues[upperIndex] - sortedValues[lowerIndex]) * fraction;
    }

    private static void UpdateMaximum(ref int location, int candidate)
    {
        int current = System.Threading.Volatile.Read(ref location);
        while (candidate > current)
        {
            int observed = System.Threading.Interlocked.CompareExchange(ref location, candidate, current);
            if (observed == current)
                return;
            current = observed;
        }
    }

    private void BroadcastMatchStartCountdowns(
        IEnumerable<long> matchingIds,
        IReadOnlyCollection<GameClientSession> activeSessions)
    {
        foreach (long matchingId in matchingIds)
        {
            if (MatchStartGate.IsAdmissionTimedOut(matchingId, DateTime.UtcNow))
            {
                var anchorSession = activeSessions.FirstOrDefault(
                    session => session.CurrentMapSubId == matchingId && session.PlayerId.HasValue);
                if (anchorSession != null)
                {
                    logger.LogWarning(
                        "Match admission deadline expired before every human became ready: MatchingId={MatchingId}",
                        matchingId);
                    AbortMatchAfterAdmissionFailure(anchorSession);
                    anchorSession.DisconnectForAdmissionFailure();
                }
                continue;
            }

            _matchRuntimeRegistry.TryExecute(matchingId, () =>
            {
                var snapshot = MatchStartGate.GetSnapshot(matchingId);
                if (!snapshot.IsKnown)
                    return;

                if (_lastMatchStartCountdownBroadcast.TryGetValue(matchingId, out int previous) &&
                    previous == snapshot.RemainingSeconds)
                {
                    return;
                }

                _lastMatchStartCountdownBroadcast[matchingId] = snapshot.RemainingSeconds;
                var matchingSessions = activeSessions
                    .Where(session => session.CurrentMapSubId == matchingId)
                    .ToList();
                if (matchingSessions.Count == 0)
                    return;

                using var packet = Packet.Create((int)Protocol.G_TO_C_MATCH_START_COUNTDOWN);
                packet.SetBody(MessagePackSerializer.Serialize(new G_TO_C_MATCH_START_COUNTDOWN
                {
                    MatchingId = matchingId,
                    RemainingSeconds = snapshot.RemainingSeconds,
                    ServerUnixMs = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds()
                }));

                foreach (var session in matchingSessions)
                    session.Send(packet);
            });
        }
    }

    private void CheckHeartbeatTimeouts(object? state)
    {
        try
        {
            var timedOutSessions = _sessionRegistry.SnapshotWhere(
                static session => session.PlayerId.HasValue && session.IsHeartbeatTimedOut());

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


    private void PublishMatchingLifecycle(string subject, long playerId, long matchingId)
    {
        if (!TryRegisterMatchingLifecycleTerminal(subject, playerId, matchingId))
            return;

        if (!IsDurableMatchingLifecycleEnabled)
        {
            PublishLegacyMatchingLifecycle(subject, playerId, matchingId);
            return;
        }

        MatchingLifecycleOutboxWorker? outboxWorker = _matchingLifecycleOutboxWorker;
        if (outboxWorker == null)
        {
            logger.LogError(
                "Matching lifecycle outbox is unavailable: Subject={Subject}, PlayerId={PlayerId}, MatchingId={MatchingId}",
                subject,
                playerId,
                matchingId);
            return;
        }

        string messageId;
        try
        {
            messageId = MatchingLifecycleMessageIds.Create(subject, playerId, matchingId);
        }
        catch (Exception ex)
        {
            logger.LogError(
                ex,
                "Matching lifecycle publish failed: Subject={Subject}, PlayerId={PlayerId}, MatchingId={MatchingId}",
                subject,
                playerId,
                matchingId);
            return;
        }

        var envelope = new MatchingLifecycleEnvelope
        {
            PlayerId = playerId,
            MatchingId = matchingId,
            OccurredAtUnixMilliseconds = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds(),
            EventId = messageId
        };
        byte[] payload = MessagePackSerializer.Serialize(envelope);
        var record = new MatchingLifecycleOutboxRecord
        {
            EventId = messageId,
            EventIdFingerprint = MatchingLifecycleOutboxKeys.FingerprintEventId(messageId),
            Subject = subject,
            Payload = payload,
            PlayerId = playerId,
            MatchingId = matchingId
        };
        TrackMatchingLifecyclePublish(outboxWorker, record);
    }

    private bool TryRegisterMatchingLifecycleTerminal(
        string subject,
        long playerId,
        long matchingId)
    {
        if (playerId <= 0 || matchingId <= 0)
            return true;

        if (!_matchingLifecycleTerminalSubjects.TryGetValue(
                matchingId,
                out ConcurrentDictionary<long, string>? playerSubjects))
        {
            var candidate = new ConcurrentDictionary<long, string>();
            if (_matchingLifecycleTerminalSubjects.TryAdd(matchingId, candidate))
            {
                playerSubjects = candidate;
                _matchingLifecycleTerminalMatchOrder.Enqueue(matchingId);
                while (_matchingLifecycleTerminalSubjects.Count >
                       MatchingLifecycleTerminalMatchRetention &&
                       _matchingLifecycleTerminalMatchOrder.TryDequeue(out long expiredMatchingId))
                {
                    _matchingLifecycleTerminalSubjects.TryRemove(expiredMatchingId, out _);
                    if (_matchingLifecyclePersistenceStates.TryGetValue(
                            expiredMatchingId,
                            out MatchingLifecyclePersistenceState? state))
                    {
                        TryRemoveExpiredMatchingLifecyclePersistenceState(expiredMatchingId, state);
                    }
                }
            }
            else
            {
                playerSubjects = _matchingLifecycleTerminalSubjects[matchingId];
            }
        }

        if (playerSubjects.TryAdd(playerId, subject))
            return true;

        playerSubjects.TryGetValue(playerId, out string? existingSubject);
        logger.LogDebug(
            "Ignored duplicate or conflicting matching lifecycle terminal event: MatchingId={MatchingId}, PlayerId={PlayerId}, ExistingSubject={ExistingSubject}, IgnoredSubject={IgnoredSubject}",
            matchingId,
            playerId,
            existingSubject,
            subject);
        return false;
    }

    private void PublishLegacyMatchingLifecycle(string subject, long playerId, long matchingId)
    {
        try
        {
            byte[] payload = new byte[sizeof(long) * 2];
            BinaryPrimitives.WriteInt64LittleEndian(payload, playerId);
            BinaryPrimitives.WriteInt64LittleEndian(payload.AsSpan(sizeof(long)), matchingId);
            _matchingLifecycleNatsClient?.Publish(subject, payload);
        }
        catch (Exception ex)
        {
            logger.LogError(
                ex,
                "Matching lifecycle publish failed: Subject={Subject}, PlayerId={PlayerId}, MatchingId={MatchingId}",
                subject,
                playerId,
                matchingId);
        }
    }

    private void TrackMatchingLifecyclePublish(
        MatchingLifecycleOutboxWorker outboxWorker,
        MatchingLifecycleOutboxRecord record)
    {
        TaskCompletionSource<bool> completion;
        Action<bool> completeMatchPersistence;
        long operationId;
        lock (_matchingLifecycleEnqueueGate)
        {
            if (Volatile.Read(ref _acceptingMatchingLifecycleEnqueues) == 0)
            {
                logger.LogWarning(
                    "Matching lifecycle outbox rejected a late enqueue: Subject={Subject}, PlayerId={PlayerId}, MatchingId={MatchingId}",
                    record.Subject,
                    record.PlayerId,
                    record.MatchingId);
                return;
            }

            Action<bool>? registration =
                TryBeginMatchingLifecyclePersistenceUnderGate(record.MatchingId);
            if (registration == null)
            {
                logger.LogWarning(
                    "Matching lifecycle outbox rejected an event after owner-release ordering was sealed: Subject={Subject}, PlayerId={PlayerId}, MatchingId={MatchingId}",
                    record.Subject,
                    record.PlayerId,
                    record.MatchingId);
                return;
            }

            completeMatchPersistence = registration;
            operationId = Interlocked.Increment(ref _nextMatchingLifecyclePublishId);
            completion = new TaskCompletionSource<bool>(
                TaskCreationOptions.RunContinuationsAsynchronously);
            _pendingMatchingLifecyclePublishTasks.TryAdd(operationId, completion.Task);
            _ = completion.Task.ContinueWith(
                completedTask => _pendingMatchingLifecyclePublishTasks.TryRemove(operationId, out _),
                CancellationToken.None,
                TaskContinuationOptions.ExecuteSynchronously,
                TaskScheduler.Default);
        }

        _ = RunTrackedMatchingLifecyclePublishAsync(
            outboxWorker,
            record,
            completeMatchPersistence,
            completion);
    }

    private async Task RunTrackedMatchingLifecyclePublishAsync(
        MatchingLifecycleOutboxWorker outboxWorker,
        MatchingLifecycleOutboxRecord record,
        Action<bool> completeMatchPersistence,
        TaskCompletionSource<bool> completion)
    {
        bool persistenceProtected = false;
        try
        {
            persistenceProtected =
                await PersistMatchingLifecycleDecisionAsync(outboxWorker, record);
        }
        catch (Exception ex)
        {
            logger.LogCritical(
                ex,
                "Unexpected matching lifecycle persistence failure: Subject={Subject}, PlayerId={PlayerId}, MatchingId={MatchingId}",
                record.Subject,
                record.PlayerId,
                record.MatchingId);
        }
        finally
        {
            completeMatchPersistence(persistenceProtected);
            completion.TrySetResult(true);
        }
    }

    private async Task<bool> PersistMatchingLifecycleDecisionAsync(
        MatchingLifecycleOutboxWorker outboxWorker,
        MatchingLifecycleOutboxRecord record)
    {
        int maximumEnqueueAttempts = scalingOptions.Enabled
            ? 3
            : StaticMatchingLifecycleEnqueueAttempts;
        for (int attempt = 1; attempt <= maximumEnqueueAttempts; attempt++)
        {
            try
            {
                MatchingLifecycleOutboxEnqueueResult result =
                    await outboxWorker.EnqueueAsync(record);
                if (result == MatchingLifecycleOutboxEnqueueResult.Fenced)
                {
                    if (!scalingOptions.Enabled)
                    {
                        logger.LogError(
                            "Static GameServer lifecycle enqueue was fenced, but this topology has no completed-owner recovery scanner; falling back to direct durable publish: Subject={Subject}, PlayerId={PlayerId}, MatchingId={MatchingId}",
                            record.Subject,
                            record.PlayerId,
                            record.MatchingId);
                        break;
                    }

                    logger.LogWarning(
                        "Matching lifecycle event lost the Redis abort-fence race; recovery owns the terminal abort: Subject={Subject}, PlayerId={PlayerId}, MatchingId={MatchingId}",
                        record.Subject,
                        record.PlayerId,
                        record.MatchingId);
                }
                return true;
            }
            catch (Exception ex)
            {
                logger.LogWarning(
                    ex,
                    "Matching lifecycle outbox enqueue attempt failed: Attempt={Attempt}, MaxAttempts={MaxAttempts}, Subject={Subject}, PlayerId={PlayerId}, MatchingId={MatchingId}",
                    attempt,
                    maximumEnqueueAttempts,
                    record.Subject,
                    record.PlayerId,
                    record.MatchingId);
                if (attempt < maximumEnqueueAttempts)
                {
                    await DelayMatchingLifecyclePersistenceRetryAsync(
                        TimeSpan.FromMilliseconds(200 * attempt));
                }
            }
        }

        if (!scalingOptions.Enabled)
        {
            bool published = await TryPublishStaticMatchingLifecycleFallbackAsync(record);
            if (!published)
            {
                logger.LogCritical(
                    "Static GameServer lifecycle event has no confirmed outbox persistence or direct JetStream PubAck; cleanup remains fail-closed and this topology has no completed-owner recovery scanner: Subject={Subject}, PlayerId={PlayerId}, MatchingId={MatchingId}",
                    record.Subject,
                    record.PlayerId,
                    record.MatchingId);
            }
            return published;
        }

        const int maximumAttempts = 3;
        for (int attempt = 1; attempt <= maximumAttempts; attempt++)
        {
            try
            {
                MatchingLifecycleAbortFenceAcquireResult result =
                    await matchingLifecycleOutboxStore.TryAcquireAbortFenceAsync(
                        record.PlayerId,
                        record.MatchingId);
                logger.LogError(
                    "Matching lifecycle enqueue exhausted; durable recovery protection was established: Protection={Protection}, Subject={Subject}, PlayerId={PlayerId}, MatchingId={MatchingId}",
                    result,
                    record.Subject,
                    record.PlayerId,
                    record.MatchingId);
                return true;
            }
            catch (Exception ex)
            {
                logger.LogError(
                    ex,
                    "Matching lifecycle abort-fence attempt failed: Attempt={Attempt}, MaxAttempts={MaxAttempts}, Subject={Subject}, PlayerId={PlayerId}, MatchingId={MatchingId}",
                    attempt,
                    maximumAttempts,
                    record.Subject,
                    record.PlayerId,
                    record.MatchingId);
                if (attempt < maximumAttempts)
                {
                    await DelayMatchingLifecyclePersistenceRetryAsync(
                        TimeSpan.FromMilliseconds(250 * attempt));
                }
            }
        }

        logger.LogCritical(
            "Matching lifecycle event has no durable record or abort fence; explicit owner release will be abandoned: Subject={Subject}, PlayerId={PlayerId}, MatchingId={MatchingId}",
            record.Subject,
            record.PlayerId,
            record.MatchingId);
        return false;
    }

    private async Task<bool> TryPublishStaticMatchingLifecycleFallbackAsync(
        MatchingLifecycleOutboxRecord record)
    {
        INatsClient? natsClient = _matchingLifecycleNatsClient;
        if (natsClient == null)
        {
            logger.LogCritical(
                "Static GameServer direct lifecycle fallback is unavailable because the NATS client is missing: Subject={Subject}, PlayerId={PlayerId}, MatchingId={MatchingId}",
                record.Subject,
                record.PlayerId,
                record.MatchingId);
            return false;
        }

        for (int attempt = 1; attempt <= StaticMatchingLifecycleDirectPublishAttempts; attempt++)
        {
            using var publishTimeout = new CancellationTokenSource(
                StaticMatchingLifecycleDirectPublishTimeout);
            try
            {
                NatsDurablePublishAck ack = await natsClient.PublishDurableAsync(
                    MatchingLifecycleSubjects.Stream,
                    record.Subject,
                    record.EventId,
                    record.Payload,
                    publishTimeout.Token);
                logger.LogWarning(
                    "Static GameServer lifecycle event was published directly after Redis outbox persistence could not be confirmed: EventId={EventId}, Stream={Stream}, Sequence={Sequence}, Duplicate={Duplicate}",
                    record.EventId,
                    ack.Stream,
                    ack.Sequence,
                    ack.Duplicate);
                return true;
            }
            catch (OperationCanceledException) when (publishTimeout.IsCancellationRequested)
            {
                logger.LogWarning(
                    "Static GameServer direct lifecycle publish timed out: Attempt={Attempt}, MaxAttempts={MaxAttempts}, Timeout={Timeout}, EventId={EventId}",
                    attempt,
                    StaticMatchingLifecycleDirectPublishAttempts,
                    StaticMatchingLifecycleDirectPublishTimeout,
                    record.EventId);
            }
            catch (Exception ex)
            {
                logger.LogWarning(
                    ex,
                    "Static GameServer direct lifecycle publish failed: Attempt={Attempt}, MaxAttempts={MaxAttempts}, EventId={EventId}",
                    attempt,
                    StaticMatchingLifecycleDirectPublishAttempts,
                    record.EventId);
            }

            if (attempt < StaticMatchingLifecycleDirectPublishAttempts)
            {
                await DelayMatchingLifecyclePersistenceRetryAsync(
                    TimeSpan.FromMilliseconds(250 * attempt));
            }
        }

        return false;
    }

    private async Task DelayMatchingLifecyclePersistenceRetryAsync(TimeSpan delay)
    {
        try
        {
            await Task.Delay(delay, _cts.Token);
        }
        catch (OperationCanceledException) when (_cts.IsCancellationRequested)
        {
            // Shutdown skips local backoff but still performs the remaining bounded Redis attempts.
        }
    }

    private async Task WaitForPendingMatchingLifecyclePublishesAsync()
    {
        while (!_pendingMatchingLifecyclePublishTasks.IsEmpty)
        {
            Task[] tasks = _pendingMatchingLifecyclePublishTasks.Values.ToArray();
            if (tasks.Length == 0)
                break;
            await Task.WhenAll(tasks);
        }
    }

    private void StartAcceptingMatchingLifecycleEnqueues()
    {
        lock (_matchingLifecycleEnqueueGate)
        {
            if (_matchingLifecycleOutboxWorker == null)
                throw new InvalidOperationException("Matching lifecycle outbox worker is unavailable.");
            Volatile.Write(ref _acceptingMatchingLifecycleEnqueues, 1);
        }
    }

    private void StopAcceptingMatchingLifecycleEnqueues()
    {
        lock (_matchingLifecycleEnqueueGate)
            Volatile.Write(ref _acceptingMatchingLifecycleEnqueues, 0);
    }

    private Action? BeginMatchingLifecyclePersistenceRegistration(long matchingId)
    {
        if (!IsDurableMatchingLifecycleEnabled)
            return static () => { };

        lock (_matchingLifecycleEnqueueGate)
        {
            if (Volatile.Read(ref _acceptingMatchingLifecycleEnqueues) == 0)
                return null;
            Action<bool>? registration =
                TryBeginMatchingLifecyclePersistenceUnderGate(matchingId);
            return registration == null ? null : () => registration(true);
        }
    }

    private Action<bool>? TryBeginMatchingLifecyclePersistenceUnderGate(long matchingId)
    {
        MatchingLifecyclePersistenceState state =
            _matchingLifecyclePersistenceStates.GetOrAdd(
                matchingId,
                static _ => new MatchingLifecyclePersistenceState());
        lock (state.SyncRoot)
        {
            if (state.Sealed)
                return null;
            state.PendingCount = checked(state.PendingCount + 1);
        }

        int completed = 0;
        return persistenceProtected =>
        {
            if (Interlocked.Exchange(ref completed, 1) == 0)
            {
                CompleteMatchingLifecyclePersistence(
                    matchingId,
                    state,
                    persistenceProtected);
            }
        };
    }

    private void CompleteMatchingLifecyclePersistence(
        long matchingId,
        MatchingLifecyclePersistenceState state,
        bool persistenceProtected)
    {
        TaskCompletionSource<bool>? quiesced = null;
        lock (state.SyncRoot)
        {
            if (state.PendingCount <= 0)
                throw new InvalidOperationException("Matching lifecycle persistence registration underflow.");

            state.PersistenceFailed |= !persistenceProtected;
            state.PendingCount--;
            if (state.PendingCount == 0 && state.ClosingRequested)
            {
                state.Sealed = true;
                quiesced = state.Quiesced;
            }
        }

        quiesced?.TrySetResult(true);
        TryRemoveExpiredMatchingLifecyclePersistenceState(matchingId, state);
    }

    private async Task<bool> SealAndWaitForMatchingLifecyclePersistenceAsync(long matchingId)
    {
        if (!IsDurableMatchingLifecycleEnabled)
            return true;

        MatchingLifecyclePersistenceState state =
            _matchingLifecyclePersistenceStates.GetOrAdd(
                matchingId,
                static _ => new MatchingLifecyclePersistenceState());
        Task waitTask;
        lock (state.SyncRoot)
        {
            state.ClosingRequested = true;
            if (state.PendingCount == 0)
            {
                state.Sealed = true;
                waitTask = Task.CompletedTask;
            }
            else
            {
                state.Quiesced ??= new TaskCompletionSource<bool>(
                    TaskCreationOptions.RunContinuationsAsynchronously);
                waitTask = state.Quiesced.Task;
            }
        }

        await waitTask;
        lock (state.SyncRoot)
            return !state.PersistenceFailed;
    }

    private void MarkMatchingLifecycleOwnerReleaseCompleted(long matchingId)
    {
        if (!_matchingLifecyclePersistenceStates.TryGetValue(
                matchingId,
                out MatchingLifecyclePersistenceState? state))
        {
            return;
        }

        lock (state.SyncRoot)
            state.OwnerReleaseCompleted = true;
        TryRemoveExpiredMatchingLifecyclePersistenceState(matchingId, state);
    }

    private void TryRemoveExpiredMatchingLifecyclePersistenceState(
        long matchingId,
        MatchingLifecyclePersistenceState state)
    {
        if (_matchingLifecycleTerminalSubjects.ContainsKey(matchingId))
            return;

        lock (state.SyncRoot)
        {
            if (!state.Sealed || state.PendingCount != 0 || !state.OwnerReleaseCompleted)
                return;
        }

        ((ICollection<KeyValuePair<long, MatchingLifecyclePersistenceState>>)
            _matchingLifecyclePersistenceStates).Remove(
            new KeyValuePair<long, MatchingLifecyclePersistenceState>(matchingId, state));
    }

    private async Task StopMatchingLifecycleOutboxWorkerAsync()
    {
        MatchingLifecycleOutboxWorker? worker =
            Interlocked.Exchange(ref _matchingLifecycleOutboxWorker, null);
        if (worker == null)
            return;

        try
        {
            await worker.StopAsync();
        }
        catch (Exception ex)
        {
            logger.LogWarning(ex, "Matching lifecycle outbox worker shutdown failed.");
        }
    }

    private async Task CloseMatchingLifecycleNatsClientAsync()
    {
        INatsClient? natsClient = Interlocked.Exchange(ref _matchingLifecycleNatsClient, null);
        if (natsClient == null)
            return;

        try
        {
            await natsClient.CloseAsync(CancellationToken.None);
        }
        catch (Exception ex)
        {
            logger.LogWarning(ex, "NATS close failed during shutdown.");
        }
    }

    private void OnClientSessionCreated(UserToken token)
    {
        if (Volatile.Read(ref _stopping) != 0)
        {
            token.Disconnect();
            return;
        }

        try
        {
            var redLockFactory = cacheHelper.GetRedLockFactory();
            _ = new GameClientSession(
                token,
                redLockFactory,
                logger,
                cacheHelper,
                ConsumeGameHandoffTicketAsync,
                OnClientSessionLeave,
                RegisterClientSession,
                GetSessionsByInstance,
                _interactableStateManager,
                _inGameInventoryManager,
                _areaItemStockManager,
                _groundItemManager,
                _summonStoneManager,
                _doorStateManager,
                _matchRosterManager,
                _areaClosureManager,
                _botPlayerManager,
                _gameEventLogManager,
                _matchSummaryFileStore,
                _encounterRevealManager,
                _matchRuntimeRegistry.TryAcquireOperation,
                _matchRuntimeRegistry.TryExecute,
                _matchRuntimeRegistry.TryBindOwnerFence,
                CleanupMatchRuntime,
                (playerId, matchingId) =>
                    PublishMatchingLifecycle(MatchingLifecycleSubjects.PlayerLeft, playerId, matchingId),
                (playerId, matchingId) =>
                    PublishMatchingLifecycle(MatchingLifecycleSubjects.PlayerCompleted, playerId, matchingId),
                (playerId, matchingId) =>
                    PublishMatchingLifecycle(MatchingLifecycleSubjects.PlayerReleased, playerId, matchingId),
                () => Volatile.Read(ref _stopping) != 0,
                AbortMatchAfterAdmissionFailure);

            logger.LogInformation("Game client session created");
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "Failed to create game client session");
            token.Disconnect();
        }
    }

    private void OnClientSessionLeave(GameClientSession session)
    {
        if (session.PlayerId.HasValue)
        {
            bool removed = _sessionRegistry.Remove(session);

            if (!removed)
            {
                logger.LogDebug(
                    "Ignored removal from a superseded game session: PlayerId={PlayerId}, MatchingId={MatchingId}",
                    session.PlayerId.Value,
                    session.CurrentMapSubId);
                if (session.CurrentMapSubId > 0)
                    CleanupMatchingIfNoHumanSessionsRemain(session.CurrentMapSubId);
                return;
            }

            logger.LogInformation("Game client session removed: PlayerId={SessionPlayerId}", session.PlayerId.Value);

            if (session.CurrentMapSubId > 0 && session.CurrentArea != AreaType.None)
            {
                using var leavePacket = PacketMaker.G_TO_C_AREA_PLAYER_LEAVE(session.PlayerId.Value);
                var sameAreaSessions = GetSessionsByMatch(session.CurrentMapSubId)
                    .Where(other =>
                        !ReferenceEquals(other, session) &&
                        other.CurrentArea == session.CurrentArea)
                    .ToList();
                foreach (var other in sameAreaSessions) other.Send(leavePacket);

                logger.LogInformation(
                    "Broadcasted disconnected player leave: PlayerId={PlayerId}, MatchingId={MatchingId}, Area={Area}, Receivers={ReceiverCount}",
                    session.PlayerId.Value,
                    session.CurrentMapSubId,
                    session.CurrentArea,
                    sameAreaSessions.Count);
            }

            if (session.CurrentMapSubId > 0)
                CleanupMatchingIfNoHumanSessionsRemain(session.CurrentMapSubId);
        }
    }

    private void AbortMatchAfterAdmissionFailure(GameClientSession session)
    {
        if (!session.PlayerId.HasValue || session.CurrentMapSubId <= 0)
            return;

        long playerId = session.PlayerId.Value;
        long matchingId = session.CurrentMapSubId;
        if (_sessionRegistry.TryGetCurrent(playerId, out GameClientSession? currentSession) &&
            currentSession != null &&
            !ReferenceEquals(currentSession, session) &&
            currentSession.CurrentMapSubId == matchingId)
        {
            logger.LogDebug(
                "Skipped admission-failed claim release for superseded session: PlayerId={PlayerId}, MatchingId={MatchingId}",
                playerId,
                matchingId);
            return;
        }

        IReadOnlyList<long> affectedPlayerIds = session.HandoffHumanPlayerIds.Count > 0
            ? session.HandoffHumanPlayerIds
            : [playerId];

        bool cleanupAccepted;
        Action? persistenceRegistration =
            BeginMatchingLifecyclePersistenceRegistration(matchingId);
        try
        {
            // Win the terminal transition before publishing or taking the session snapshot. A new
            // connection either registers under the same runtime lock before this transition and is
            // included below, or observes Finalizing and cannot publish a successful admission.
            cleanupAccepted = false;
            if (!_matchRuntimeRegistry.IsTerminal(matchingId))
                cleanupAccepted = TryCleanupMatchRuntime(matchingId, null, null);

            // A competing finalizer or an already terminal match still needs exact claim release for
            // a late, now-rejected ticket. Only skip when cleanup itself failed and the match remained active.
            if (!cleanupAccepted && !_matchRuntimeRegistry.IsTerminal(matchingId))
                return;

            foreach (long affectedPlayerId in affectedPlayerIds)
            {
                PublishMatchingLifecycle(
                    MatchingLifecycleSubjects.PlayerAdmissionFailed,
                    affectedPlayerId,
                    matchingId);
            }
        }
        finally
        {
            persistenceRegistration?.Invoke();
        }

        if (!cleanupAccepted)
            return;

        var otherSessions = GetSessionsByMatch(matchingId)
            .Where(other => !ReferenceEquals(other, session))
            .ToList();
        foreach (var otherSession in otherSessions)
            otherSession.DisconnectForAdmissionFailure();

        logger.LogWarning(
            "Match aborted after client admission failure: MatchingId={MatchingId}, FailedPlayerId={PlayerId}, AffectedPlayers={AffectedPlayers}",
            matchingId,
            playerId,
            affectedPlayerIds.Count);
    }

    /// <summary>
    ///     사람 세션 없이 진행되는 매치(관리자 봇 전용 인스턴스)를 정산한다.
    ///     승리 판정이 사람 세션에 의존해 최후 1인이 남아도 끝나지 않고, 오염도가 한계에
    ///     닿은 봇이 계속 살아 있는 상태로 매치가 무한히 이어지던 것을 막는다.
    /// </summary>
    public void EndBotOnlyMatchIfSettled(long matchingId, long winnerPlayerId)
    {
        if (HasHumanSessions(matchingId))
            return;

        CleanupMatchingIfNoHumanSessionsRemain(matchingId, "last_survivor_bot_only", winnerPlayerId);
    }

    private void CleanupMatchingIfNoHumanSessionsRemain(long matchingId) =>
        CleanupMatchingIfNoHumanSessionsRemain(matchingId, "last_human_left", 0);

    private void CleanupMatchingIfNoHumanSessionsRemain(long matchingId, string endReason, long winnerPlayerId)
    {
        if (HasHumanSessions(matchingId))
            return;

        bool cleanupAccepted = TryCleanupMatchRuntime(
            matchingId,
            () => !HasHumanSessions(matchingId),
            () =>
            {
                if (!_gameEventLogManager.TryBeginFinalization(matchingId))
                    return;

                DateTime endedAtUtc = DateTime.UtcNow;
                DateTime startedAtUtc =
                    _areaClosureManager.GetMatchingState(matchingId)?.GameStartTime ?? endedAtUtc;
                var finalPlayerStats = _matchRosterManager.BuildGameResult(matchingId)
                    .Select(row =>
                    {
                        var stats = _gameEventLogManager.GetResultStats(matchingId, row.playerId);
                        DateTime survivalEndUtc = row.eliminatedAt ?? endedAtUtc;
                        return new MatchFinalPlayerStats(
                            row.playerId,
                            row.eliminationRank,
                            Math.Max(0, (int)Math.Floor((survivalEndUtc - startedAtUtc).TotalSeconds)),
                            stats.KillCount + stats.MonsterKillCount,
                            stats.TotalDamageDealt + stats.MonsterDamageDealt,
                            stats.TotalRecovery,
                            // 승점 (#229): 사람이 나간 매치도 오브 수를 남긴다 — 봇 매치가 유일한
                            // 자동 검증 창구라 여기서 빠지면 결과 집계를 로그로 확인할 수 없다.
                            GetSwarmOrbScore(matchingId, row.playerId).OrbCount);
                    })
                    .ToList();
                _gameEventLogManager.LogMatchAbandoned(matchingId, endReason, finalPlayerStats);
                PersistMatchSummary(matchingId, endReason, winnerPlayerId);
            });

        if (cleanupAccepted)
        {
            logger.LogInformation(
                "Removed matching without human sessions: MatchingId={MatchingId}, EndReason={EndReason}",
                matchingId, endReason);
        }
    }

    /// <summary>
    ///     Runs every in-memory match cleanup behind one terminal lifecycle gate.
    ///     Each component cleanup is isolated so one failure cannot prevent the remaining
    ///     managers from releasing their matchingId state.
    /// </summary>
    private void CleanupMatchRuntime(long matchingId) => CleanupMatchRuntime(matchingId, null);

    private void CleanupMatchRuntime(long matchingId, Action? beforeCleanup)
    {
        TryCleanupMatchRuntime(matchingId, null, beforeCleanup);
    }

    private bool TryCleanupMatchRuntime(
        long matchingId,
        Func<bool>? canFinalize,
        Action? beforeCleanup)
    {
        return _matchRuntimeCleanupCoordinator.TryFinalize(
            matchingId,
            canFinalize,
            beforeCleanup);
    }

    private void PersistMatchSummary(long matchingId, string endReason, long winnerId) =>
        MatchSummaryPersistence.Persist(
            _gameEventLogManager, _matchSummaryFileStore, logger, matchingId, endReason, winnerId);

    private async Task CleanupAbandonedMatchingRedisAsync(long matchingId)
    {
        bool persistenceProtected =
            await SealAndWaitForMatchingLifecyclePersistenceAsync(matchingId);
        if (!persistenceProtected)
        {
            bool abandoned = false;
            try
            {
                if (scalingOptions.Enabled)
                    abandoned = await gameServerNodeLease.AbandonMatchOwnerAsync(matchingId);
            }
            catch (Exception ex)
            {
                logger.LogCritical(
                    ex,
                    "Failed to abandon the local match owner after lifecycle persistence failure: MatchingId={MatchingId}",
                    matchingId);
            }
            finally
            {
                MarkMatchingLifecycleOwnerReleaseCompleted(matchingId);
            }

            if (scalingOptions.Enabled)
            {
                logger.LogCritical(
                    "Matching cleanup is fail-closed because no lifecycle record or abort fence was persisted; handoff data remains and the Redis owner is left for TTL recovery: MatchingId={MatchingId}, LocalOwnerAbandoned={LocalOwnerAbandoned}",
                    matchingId,
                    abandoned);
            }
            else
            {
                logger.LogCritical(
                    "Static GameServer matching cleanup is fail-closed because neither outbox persistence nor direct JetStream publication was confirmed; handoff data remains, but this topology has no routing owner or completed-owner recovery scanner: MatchingId={MatchingId}",
                    matchingId);
            }
            return;
        }

        try
        {
            await cacheHelper.KeyDeleteAsync(MatchingHandoffRedisKeys.Key(matchingId));
            await cacheHelper.HashDeleteAsync("matching_bots", matchingId);
        }
        catch (Exception ex)
        {
            logger.LogWarning(ex,
                "Failed to remove abandoned matching from Redis: MatchingId={MatchingId}",
                matchingId);
        }

        try
        {
            bool released = await gameServerNodeLease.ReleaseMatchOwnerAsync(matchingId);
            if (scalingOptions.Enabled && !released)
            {
                logger.LogDebug(
                    "Match owner was already released, untracked, or fenced: MatchingId={MatchingId}",
                    matchingId);
            }
        }
        catch (Exception ex)
        {
            logger.LogWarning(
                ex,
                "Failed to release GameServer match owner; lease TTL will fence it: MatchingId={MatchingId}",
                matchingId);
        }
        finally
        {
            MarkMatchingLifecycleOwnerReleaseCompleted(matchingId);
        }
    }

    private async Task<GameHandoffContext?> ConsumeGameHandoffTicketAsync(string? ticket)
    {
        if (!scalingOptions.Enabled)
            return await gameHandoffTicketService.ConsumeAsync(ticket);
        if (Volatile.Read(ref _stopping) != 0 || !gameServerNodeLease.HasLease)
            return null;

        return await gameServerNodeLease.ConsumeAndTrackMatchOwnerAsync(
            () => gameHandoffTicketService.ConsumeForOwnerAsync(
                ticket,
                gameServerNodeLease.Identity,
                scalingOptions.NodeLeaseLifetime));
    }

    private void StartMatchingRedisCleanup(long matchingId)
    {
        Task cleanupTask = CleanupAbandonedMatchingRedisAsync(matchingId);
        _pendingMatchingRedisCleanupTasks[matchingId] = cleanupTask;
        _ = RemoveCompletedMatchingRedisCleanupAsync(matchingId, cleanupTask);
    }

    private async Task RemoveCompletedMatchingRedisCleanupAsync(long matchingId, Task cleanupTask)
    {
        await cleanupTask;
        ((ICollection<KeyValuePair<long, Task>>)_pendingMatchingRedisCleanupTasks)
            .Remove(new KeyValuePair<long, Task>(matchingId, cleanupTask));
    }

    private async Task WaitForPendingMatchingRedisCleanupsAsync()
    {
        while (true)
        {
            var pendingCleanups = _pendingMatchingRedisCleanupTasks.ToArray();
            if (pendingCleanups.Length == 0)
                return;

            await Task.WhenAll(pendingCleanups.Select(pair => pair.Value));
            foreach (var pendingCleanup in pendingCleanups)
            {
                ((ICollection<KeyValuePair<long, Task>>)_pendingMatchingRedisCleanupTasks)
                    .Remove(pendingCleanup);
            }
        }
    }

    private Action? RegisterClientSession(long playerId, GameClientSession session)
    {
        GameClientSession? existingSession = _sessionRegistry.Register(playerId, session, out bool added);
        if (existingSession == null)
        {
            if (added)
                logger.LogInformation("Game client session registered: PlayerId={PlayerId}", playerId);
            return null;
        }

        logger.LogWarning("Game client session replaced: PlayerId={PlayerId}", playerId);
        return () =>
        {
            existingSession.MarkServerInitiatedDisconnect();
            existingSession.ForceDisconnect();
        };
    }

    /// <summary>같은 매치 인스턴스의 인증된 세션 스냅샷 — 색인 조회라 전체 세션 스캔이 없다.</summary>
    private List<GameClientSession> GetSessionsByMatch(long matchingId)
    {
        return _sessionRegistry.GetByMatch(matchingId);
    }

    private bool HasHumanSessions(long matchingId)
    {
        return _sessionRegistry.HasSessions(matchingId);
    }

    private List<GameClientSession> GetSessionsByInstance(MapId mapId, long mapSubId)
    {
        return _sessionRegistry.GetByInstance(mapId, mapSubId);
    }

    // MMO 로그아웃 프로토콜 제거됨 - 세션 기반 게임에서는 불필요
    // private void SubscribeToLogoutEvents() { ... }
    // private async Task ProcessMessage(byte[] message) { ... }
    // private async Task HandleLogout(long playerId, byte[] body) { ... }

    // ===== 운영 어드민 API =====

    /// <summary>
    ///     운영툴 진행 로그 매니저 (AdminEndpoints에서 events 조회용)
    /// </summary>
    public GameEventLogManager GameEventLogManager => _gameEventLogManager;
    public MatchSummaryFileStore MatchSummaryFileStore => _matchSummaryFileStore;

    /// <summary>
    ///     활성 인스턴스 ID 목록 반환 (MatchingId 기준 dedup)
    /// </summary>
    public IReadOnlyList<long> GetActiveInstanceIds()
    {
        return GetActiveMatchingIds();
    }

    private List<long> GetActiveMatchingIds()
    {
        var ids = _sessionRegistry.GetActiveMatchingIds().ToHashSet();

        foreach (long matchingId in _botPlayerManager.GetActiveMatchingIds())
        {
            if (matchingId > 0)
                ids.Add(matchingId);
        }

        return ids.OrderBy(id => id).ToList();
    }

    public InstanceSnapshot? CreateBotOnlyInstance(int botCount = 10)
    {
        // 상한 = 매치 정원 (#223 10인 전환) — 스폰 포드 수와 일치.
        botCount = Math.Clamp(botCount, 2, Config.SWARM_PLAYERS_PER_MATCH);
        long matchingId = System.Threading.Interlocked.Increment(ref _adminBotOnlyMatchingIdSeed);
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
                TargetPlayerId = playerIds[targetIndex]
            });
        }

        var spawnAssignments = MatchSpawnData.CreatePhaseRoomAssignments(matchingId, playerIds);
        foreach (var botInfo in botInfoList)
            botInfo.SpawnCell = Cell.Clone(spawnAssignments[botInfo.PlayerId]);

        bool initialized = _matchRuntimeRegistry.TryExecute(matchingId, () =>
        {
            _botPlayerManager.RegisterBots(matchingId, Config.SWARM_MATCH_MAP, botInfoList);
            int matchSeed = MatchSpawnData.GetDeterministicSeed(matchingId);
            _gameEventLogManager.BeginMatch(matchingId, matchSeed);
            foreach (var bot in _botPlayerManager.GetBots(matchingId))
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

            foreach (var bot in botInfoList)
            {
                _matchRosterManager.RegisterEntry(matchingId, new RosterEntry
                {
                    PlayerId = bot.PlayerId,
                    TargetPlayerId = bot.TargetPlayerId
                });
            }

            _areaItemStockManager.InitializeMatching(matchingId);
            _groundItemManager.InitializeMatching(matchingId);
            _doorStateManager.InitializeMatching(matchingId, Array.Empty<AreaType>());

            _gameEventLogManager.LogSystem(matchingId,
                $"Bot-only instance created: botCount={botCount}, ids=[{string.Join(",", playerIds)}]");
        });
        if (!initialized)
            return null;

        logger.LogInformation(
            "Bot-only instance created: MatchingId={MatchingId}, BotCount={BotCount}, Ids=[{Ids}]",
            matchingId, botCount, string.Join(",", playerIds));

        return GetInstanceSnapshot(matchingId);
    }

    private bool IsBotOnlyChainPlayerActive(long matchingId, long playerId)
    {
        var link = _matchRosterManager.GetEntry(matchingId, playerId);
        return link != null
               && link.Status != PlayerMatchStatus.ELIMINATED
               && link.Status != PlayerMatchStatus.SPECTATING;
    }

    /// <summary>
    ///     인스턴스 요약 (목록 뷰용)
    /// </summary>
    public InstanceSummary? GetInstanceSummary(long matchingId)
    {
        var sessions = GetSessionsByMatch(matchingId);
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

        return new InstanceSummary
        {
            MatchingId = matchingId,
            MapId = mapId,
            PlayerCount = sessions.Count + bots.Count,
            AliveCount = aliveCount,
            ElapsedSeconds = Math.Round(elapsed, 1),
            RoundNumber = 0, // 라운드 시스템 퇴역(#246)
            TotalRounds = 0,
            RoundPhase = "",
            RoundRemainingSeconds = 0,
            RoundPhaseDurationSeconds = 0,
            RoundSessionEnded = false,
            ClosedAreas = closedAreas
        };
    }

    /// <summary>
    ///     인스턴스 풀 스냅샷 (폐쇄 스케줄 포함)
    /// </summary>
    public InstanceSnapshot? GetFullInstanceSnapshot(long matchingId)
    {
        var base_ = GetInstanceSnapshot(matchingId);
        if (base_ == null) return null;

        var (sequence, closedIds, nextArea, nextAtUnix, secondsLeft, warningActive) =
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
            WarningActive = warningActive
        };

        return base_;
    }

    /// <summary>
    ///     인스턴스 상세 스냅샷 (플레이어별 코어 상태 포함)
    /// </summary>
    public InstanceSnapshot? GetInstanceSnapshot(long matchingId)
    {
        var sessions = GetSessionsByMatch(matchingId);

        var bots = _botPlayerManager.GetBots(matchingId);

        if (sessions.Count == 0 && bots.Count == 0) return null;

        var closureState = _areaClosureManager.GetMatchingState(matchingId);
        double elapsed = closureState != null
            ? (DateTime.UtcNow - closureState.GameStartTime).TotalSeconds
            : 0;

        var closedAreas = closureState?.ClosedAreas
            .Select(a => a.ToString())
            .ToList() ?? [];

        // 모든 PlayerId(인간+봇)에 대한 RosterEntry 조회 → WatcherOfMe 역방향 매핑
        var allPlayerIds = sessions.Select(s => s.PlayerId!.Value).Concat(bots.Select(b => b.PlayerId)).ToList();
        var allLinks = allPlayerIds
            .Select(id => _matchRosterManager.GetEntry(matchingId, id))
            .Where(l => l != null)
            .ToList();
        long? FindWatcherOf(long playerId) =>
            allLinks.FirstOrDefault(l => l!.TargetPlayerId == playerId)?.PlayerId;

        var playerSnapshots = new List<PlayerSnapshot>();

        // 1) 인간 플레이어
        foreach (var s in sessions)
        {
            var chainLink = _matchRosterManager.GetEntry(matchingId, s.PlayerId!.Value);
            playerSnapshots.Add(new PlayerSnapshot
            {
                PlayerId = s.PlayerId!.Value,
                Area = s.CurrentArea.ToString(),
                Stamina = s.AdminStamina,
                Corruption = s.AdminCorruption,
                PlayerMatchStatus = s.PlayerMatchStatus.ToString(),
                TargetPlayerId = s.TargetPlayerId,
                IsBot = s.IsBot,
                IsEliminated = s.IsEliminated,
                WatcherOfMe = FindWatcherOf(s.PlayerId!.Value),
                ChainStatus = chainLink?.Status.ToString() ?? ""
            });
        }

        // 2) 봇 — TCP 세션이 없으므로 BotPlayerManager._botStates에서 조회
        foreach (var bot in bots)
        {
            var chainLink = _matchRosterManager.GetEntry(matchingId, bot.PlayerId);
            playerSnapshots.Add(new PlayerSnapshot
            {
                PlayerId = bot.PlayerId,
                Area = bot.CurrentArea.ToString(),
                Stamina = bot.Stamina,
                Corruption = bot.Corruption,
                PlayerMatchStatus = bot.PlayerMatchStatus.ToString(),
                TargetPlayerId = bot.TargetPlayerId,
                IsBot = true,
                IsEliminated = bot.IsEliminated,
                WatcherOfMe = FindWatcherOf(bot.PlayerId),
                ChainStatus = chainLink?.Status.ToString() ?? ""
            });
        }

        int aliveCount = sessions.Count(s => !s.IsEliminated) + bots.Count(b => !b.IsEliminated);
        string mapId = sessions.FirstOrDefault()?.CurrentMapId.ToString()
                       ?? _botPlayerManager.GetMatchingMapId(matchingId).ToString();

        return new InstanceSnapshot
        {
            MatchingId = matchingId,
            MapId = mapId,
            PlayerCount = sessions.Count + bots.Count,
            AliveCount = aliveCount,
            ElapsedSeconds = Math.Round(elapsed, 1),
            RoundNumber = 0, // 라운드 시스템 퇴역(#246)
            TotalRounds = 0,
            RoundPhase = "",
            RoundRemainingSeconds = 0,
            RoundPhaseDurationSeconds = 0,
            RoundSessionEnded = false,
            ClosedAreas = closedAreas,
            Players = playerSnapshots
        };
    }
}
