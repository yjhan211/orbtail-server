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

/// <summary>
///     Hosts authoritative match admission, simulation, ordered publication, terminal lifecycle, and
///     distributed owner cleanup. Match state preparation is serialized per matchingId; normal and terminal
///     network publications run outside MatchRuntime.SyncRoot while holding the appropriate turn/lease, and
///     terminal component cleanup runs under the monitor before lifecycle, Redis, and summary continuations.
/// </summary>
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
    private readonly ConcurrentDictionary<long, ConcurrentDictionary<long, string>>
        _matchingLifecycleTerminalSubjects = new();
    private readonly ConcurrentQueue<long> _matchingLifecycleTerminalMatchOrder = new();
    private readonly MatchRuntimeRegistry _matchRuntimeRegistry = new();
    private readonly SwarmCombatPublicationCoordinator _swarmCombatPublicationCoordinator =
        new(TimeSpan.FromMilliseconds(ProximityAutoCombatTickIntervalMs));
    private readonly SwarmBotTickCoordinator _swarmBotTickCoordinator = new();
    private MatchRuntimeCleanupCoordinator _matchRuntimeCleanupCoordinator = null!;
    private SwarmBotMovementCoordinator _swarmBotMovementCoordinator = null!;
    private SwarmClosurePublicationCoordinator _swarmClosurePublicationCoordinator = null!;
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
    private long _nextMatchingRedisCleanupId;
    private long _nextMatchingLifecyclePublishId;
    private int _acceptingMatchingLifecycleEnqueues;
    private int _acceptingLegacyMatchingLifecyclePublishes;
    private int _stopping;

    private bool IsDurableMatchingLifecycleEnabled =>
        scalingOptions.Enabled || configuration.GetValue("userServerScaling:enabled", false);

    private CancellationTokenSource _cts = new();
    private Timer? _heartbeatCheckTimer;
    private Timer? _resourceTickTimer;        // 폐쇄 구역 등 주기성 자원 변화
    private Timer? _areaClosureTickTimer;     // 구역 폐쇄 체크
    private Timer? _targetLocationTimer;      // 타겟 위치 전송
    private Timer? _botMovementTimer;         // #127 봇 walking step (50ms)

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

    private sealed record AdmissionFailureTerminalSnapshot(
        IReadOnlyList<GameClientSession> Sessions,
        IReadOnlyCollection<long> PlayerIds);

    private SwarmMatchRuntime GetSwarmMatchRuntime(long matchingId) =>
        _swarmMatchRuntimes.GetOrCreate(matchingId);

    private void RegisterMatchRuntimeComponents(long matchingId)
    {
        if (!_swarmBotTickCoordinator.RegisterMatching(matchingId))
        {
            throw new InvalidOperationException(
                $"Bot tick state was already registered for match {matchingId}.");
        }

        if (!_doorStateManager.RegisterMatching(matchingId))
        {
            _swarmBotTickCoordinator.ClearMatching(matchingId);
            throw new InvalidOperationException(
                $"Door state was already registered for match {matchingId}.");
        }

        if (!_swarmCombatPublicationCoordinator.RegisterMatching(matchingId))
        {
            _doorStateManager.ClearMatching(matchingId);
            _swarmBotTickCoordinator.ClearMatching(matchingId);
            throw new InvalidOperationException(
                $"Combat publication state was already registered for match {matchingId}.");
        }
    }

    private bool IsSwarmFrontOrbDamaged(long matchingId, long playerId) =>
        _swarmMatchRuntimes.TryGet(matchingId, out var runtime) &&
        runtime.Pacing.FrontOrbHp.ContainsKey((matchingId, playerId));

    internal const int ResourceTickIntervalSeconds = 5;
    // ClosedAreaStaminaPenaltyPerTick 제거 — v0.1.9 #66: 폐쇄 구역 패널티 → 오염도로 변경

    public async Task StartAsync(CancellationToken cancellationToken)
    {
        Volatile.Write(ref _stopping, 0);
        StopAcceptingMatchingLifecycleEnqueues();
        StopAcceptingLegacyMatchingLifecyclePublishes();
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
        StopAcceptingLegacyMatchingLifecyclePublishes();
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
        long[] affectedPlayerIds = handoffPlayerIds
            .Where(playerId => playerId > 0)
            .Distinct()
            .ToArray();
        Task handlerTask = Task.Run(() =>
            HandleGameServerMatchOwnerLost(matchingId, affectedPlayerIds));
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
        IReadOnlyCollection<long> affectedPlayerIds)
    {
        logger.LogError(
            "Aborting match after its distributed GameServer owner fence was lost: MatchingId={MatchingId}, Players={PlayerCount}",
            matchingId,
            affectedPlayerIds.Count);

        bool cleanupAccepted = TryAbortMatchAtTerminalBoundary(
            matchingId,
            () =>
            {
                List<GameClientSession> affectedSessions = GetSessionsByMatch(matchingId);
                var playerIdsToPublish = affectedPlayerIds.ToHashSet();
                foreach (GameClientSession session in affectedSessions)
                {
                    if (!session.PlayerId.HasValue)
                        continue;

                    if (session.TryMarkMatchingLifecycleHandledExternally())
                        playerIdsToPublish.Add(session.PlayerId.Value);
                    else
                        playerIdsToPublish.Remove(session.PlayerId.Value);
                }

                return new AdmissionFailureTerminalSnapshot(
                    affectedSessions,
                    playerIdsToPublish.ToArray());
            });
        if (!cleanupAccepted && !_matchRuntimeRegistry.IsTerminal(matchingId))
        {
            logger.LogCritical(
                "Match owner fence was lost but local runtime cleanup could not start: MatchingId={MatchingId}",
                matchingId);
        }
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
        _swarmBotMovementCoordinator = new SwarmBotMovementCoordinator(
            _botPlayerManager,
            _areaClosureManager,
            _areaItemStockManager,
            _inGameInventoryManager,
            _groundItemManager,
            _summonStoneManager,
            _encounterRevealManager,
            _gameEventLogManager);
        _swarmClosurePublicationCoordinator = new SwarmClosurePublicationCoordinator();
        _matchRuntimeRegistry.SetRuntimeInitializer(RegisterMatchRuntimeComponents);
        _matchRuntimeCleanupCoordinator = new MatchRuntimeCleanupCoordinator(
            _matchRuntimeRegistry,
            [
                new MatchRuntimeCleanupStep(
                    "combat publication",
                    _swarmCombatPublicationCoordinator.ClearMatching),
                new MatchRuntimeCleanupStep(
                    "session runtime",
                    GameClientSession.CleanupAbandonedMatchingRuntime),
                new MatchRuntimeCleanupStep(
                    "session index",
                    _sessionRegistry.RemoveMatch),
                new MatchRuntimeCleanupStep(
                    "settlement",
                    CleanupMatchSettlementState),
                new MatchRuntimeCleanupStep(
                    "swarm arena",
                    CleanupSwarmArenaState),
                new MatchRuntimeCleanupStep(
                    "bot movement ticks",
                    _swarmBotTickCoordinator.ClearMatching),
                new MatchRuntimeCleanupStep(
                    "bot movement publication",
                    _swarmBotMovementCoordinator.ClearMatching),
                new MatchRuntimeCleanupStep(
                    "field closure publication",
                    _swarmClosurePublicationCoordinator.ClearMatching),
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
                    _gameEventLogManager.Clear)
            ],
            logger,
            PrepareMatchingRedisCleanup);
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
            StartAcceptingLegacyMatchingLifecyclePublishes();
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
        IDisposable? publicationGroup = null;
        try
        {
            // Roster/drop/log changes are authoritative preparation. When capture is active, a
            // later transport failure skips only this bot's remaining outbound group; it does not
            // roll back state or prevent later combat plan groups from dispatching.
            publicationGroup = _swarmCombatPublicationCoordinator.TryBeginBestEffortGroup(
                ex => logger.LogError(ex, "봇 탈락 처리 중 오류: BotId={BotId}", botId));
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
        finally
        {
            publicationGroup?.Dispose();
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
                SwarmClosurePublicationPlan? plan = null;
                SwarmClosurePublicationTicket? publicationTicket = null;
                GameClientSession[]? sessionSnapshot = null;
                IDisposable? runtimeOperation = _matchRuntimeRegistry.TryAcquireOperation(
                    matchingId,
                    () =>
                    {
                        sessionSnapshot = GetSessionsByMatch(matchingId).ToArray();
                        plan = PrepareSwarmScheduledClosureTick(matchingId, sessionSnapshot);
                        if (plan != null)
                        {
                            publicationTicket =
                                _swarmClosurePublicationCoordinator.ReservePublication(matchingId);
                        }
                    });
                if (runtimeOperation == null)
                    continue;
                if (plan == null || sessionSnapshot == null || publicationTicket == null)
                {
                    SwarmClosurePublicationTicket? ticketToRetire = publicationTicket;
                    DispatchWithMatchRuntimeLease(
                        runtimeOperation,
                        () =>
                        {
                            if (ticketToRetire is { } ticket)
                            {
                                _swarmClosurePublicationCoordinator.DispatchInOrder(
                                    ticket,
                                    static () => { });
                            }
                        });
                    continue;
                }

                SwarmClosurePublicationPlan capturedPlan = plan;
                SwarmClosurePublicationTicket capturedTicket = publicationTicket.Value;
                GameClientSession[] capturedSessions = sessionSnapshot;
                DispatchWithMatchRuntimeLease(
                    runtimeOperation,
                    () => _swarmClosurePublicationCoordinator.DispatchInOrder(
                        capturedTicket,
                        () => DispatchSwarmClosurePublicationPlan(capturedPlan, capturedSessions)));
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
                }
                continue;
            }

            var preliminarySnapshot = MatchStartGate.GetSnapshot(matchingId);
            if (!preliminarySnapshot.IsKnown ||
                !_swarmCombatPublicationCoordinator.NeedsPeriodicCountdownPublication(
                    matchingId,
                    preliminarySnapshot.RemainingSeconds))
            {
                continue;
            }

            SwarmCombatPublicationCoordinator.PublicationTurn? publicationTurn =
                _swarmCombatPublicationCoordinator.BeginOrderedTurn(matchingId);
            if (publicationTurn == null)
                continue;

            PrepareAndDispatchCombatPublication(
                matchingId,
                publicationTurn,
                () =>
                {
                    var snapshot = MatchStartGate.GetSnapshot(matchingId);
                    if (!snapshot.IsKnown ||
                        !_swarmCombatPublicationCoordinator.TryCommitPeriodicCountdownPublication(
                            publicationTurn,
                            snapshot.RemainingSeconds))
                    {
                        return;
                    }

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
        Action? dispatch = PrepareMatchingLifecyclePublication(subject, playerId, matchingId);
        dispatch?.Invoke();
    }

    private Action? PrepareMatchingLifecyclePublication(string subject, long playerId, long matchingId)
    {
        if (!TryRegisterMatchingLifecycleTerminal(subject, playerId, matchingId))
            return null;

        if (!IsDurableMatchingLifecycleEnabled)
            return PrepareLegacyMatchingLifecyclePublication(subject, playerId, matchingId);

        MatchingLifecycleOutboxWorker? outboxWorker = _matchingLifecycleOutboxWorker;
        if (outboxWorker == null)
        {
            logger.LogError(
                "Matching lifecycle outbox is unavailable: Subject={Subject}, PlayerId={PlayerId}, MatchingId={MatchingId}",
                subject,
                playerId,
                matchingId);
            RecordMatchingLifecyclePersistenceFailure(matchingId);
            return null;
        }

        try
        {
            string messageId = MatchingLifecycleMessageIds.Create(subject, playerId, matchingId);
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
            return RegisterMatchingLifecyclePublish(outboxWorker, record);
        }
        catch (Exception ex)
        {
            logger.LogError(
                ex,
                "Matching lifecycle publication preparation failed: Subject={Subject}, PlayerId={PlayerId}, MatchingId={MatchingId}",
                subject,
                playerId,
                matchingId);
            RecordMatchingLifecyclePersistenceFailure(matchingId);
            return null;
        }
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

    private Action PrepareLegacyMatchingLifecyclePublication(
        string subject,
        long playerId,
        long matchingId)
    {
        Action<bool>? completeMatchPersistence = null;
        TaskCompletionSource<bool>? completion = null;
        long operationId = 0;
        if (subject == MatchingLifecycleSubjects.PlayerCompleted)
        {
            lock (_matchingLifecycleEnqueueGate)
            {
                if (Volatile.Read(ref _acceptingLegacyMatchingLifecyclePublishes) != 0)
                {
                    completeMatchPersistence =
                        TryBeginMatchingLifecyclePersistenceUnderGate(matchingId);
                    if (completeMatchPersistence == null)
                    {
                        logger.LogWarning(
                            "Legacy matching lifecycle completion was prepared after cleanup ordering was sealed; the Core publish remains best effort but can no longer hold the Redis cleanup barrier: Subject={Subject}, PlayerId={PlayerId}, MatchingId={MatchingId}",
                            subject,
                            playerId,
                            matchingId);
                    }
                    else
                    {
                        operationId = Interlocked.Increment(ref _nextMatchingLifecyclePublishId);
                        completion = new TaskCompletionSource<bool>(
                            TaskCreationOptions.RunContinuationsAsynchronously);
                        _pendingMatchingLifecyclePublishTasks.TryAdd(operationId, completion.Task);
                    }
                }
            }
        }

        int dispatchStarted = 0;
        return () =>
        {
            if (Interlocked.Exchange(ref dispatchStarted, 1) != 0)
                return;

            try
            {
                if (completeMatchPersistence != null && completion != null)
                {
                    PublishLegacyMatchingLifecycle(subject, playerId, matchingId);
                }
                else
                {
                    PublishUntrackedLegacyMatchingLifecycle(
                        subject,
                        playerId,
                        matchingId);
                }
            }
            finally
            {
                if (completeMatchPersistence != null && completion != null)
                {
                    CompleteTrackedLegacyMatchingLifecyclePublish(
                        subject,
                        playerId,
                        matchingId,
                        operationId,
                        completeMatchPersistence,
                        completion);
                }
            }
        };
    }

    private void PublishUntrackedLegacyMatchingLifecycle(
        string subject,
        long playerId,
        long matchingId)
    {
        if (!TryReserveUntrackedLegacyMatchingLifecyclePublish(
                subject,
                playerId,
                matchingId,
                out long operationId,
                out TaskCompletionSource<bool>? completion))
        {
            return;
        }

        try
        {
            PublishLegacyMatchingLifecycle(subject, playerId, matchingId);
        }
        finally
        {
            CompleteUntrackedLegacyMatchingLifecyclePublish(operationId, completion!);
        }
    }

    /// <summary>
    /// Atomically checks the legacy shutdown fence and registers an in-flight publication.
    /// The caller publishes outside <see cref="_matchingLifecycleEnqueueGate"/>, while shutdown
    /// drains every operation that was accepted before the fence closed.
    /// </summary>
    private bool TryReserveUntrackedLegacyMatchingLifecyclePublish(
        string subject,
        long playerId,
        long matchingId,
        out long operationId,
        out TaskCompletionSource<bool>? completion)
    {
        operationId = 0;
        completion = null;

        lock (_matchingLifecycleEnqueueGate)
        {
            if (Volatile.Read(ref _acceptingLegacyMatchingLifecyclePublishes) == 0)
            {
                logger.LogWarning(
                    "Legacy matching lifecycle publish was skipped after the shutdown fence closed: Subject={Subject}, PlayerId={PlayerId}, MatchingId={MatchingId}",
                    subject,
                    playerId,
                    matchingId);
                return false;
            }

            operationId = Interlocked.Increment(ref _nextMatchingLifecyclePublishId);
            completion = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
            if (!_pendingMatchingLifecyclePublishTasks.TryAdd(operationId, completion.Task))
            {
                logger.LogCritical(
                    "Legacy matching lifecycle tracker registration failed: Subject={Subject}, PlayerId={PlayerId}, MatchingId={MatchingId}, OperationId={OperationId}",
                    subject,
                    playerId,
                    matchingId,
                    operationId);
                completion = null;
                return false;
            }

            return true;
        }
    }

    private void CompleteUntrackedLegacyMatchingLifecyclePublish(
        long operationId,
        TaskCompletionSource<bool> completion)
    {
        try
        {
            completion.TrySetResult(true);
        }
        finally
        {
            ((ICollection<KeyValuePair<long, Task>>)_pendingMatchingLifecyclePublishTasks)
                .Remove(new KeyValuePair<long, Task>(operationId, completion.Task));
        }
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

    private void CompleteTrackedLegacyMatchingLifecyclePublish(
        string subject,
        long playerId,
        long matchingId,
        long operationId,
        Action<bool> completeMatchPersistence,
        TaskCompletionSource<bool> completion)
    {
        try
        {
            // Legacy Core publication is best effort. Reaching this callback means the attempt
            // completed, so Redis cleanup remains fail-open exactly as it was before deferral.
            completeMatchPersistence(true);
        }
        catch (Exception ex)
        {
            logger.LogCritical(
                ex,
                "Legacy matching lifecycle completion callback failed: Subject={Subject}, PlayerId={PlayerId}, MatchingId={MatchingId}",
                subject,
                playerId,
                matchingId);
        }
        finally
        {
            try
            {
                completion.TrySetResult(true);
            }
            finally
            {
                ((ICollection<KeyValuePair<long, Task>>)_pendingMatchingLifecyclePublishTasks)
                    .Remove(new KeyValuePair<long, Task>(operationId, completion.Task));
            }
        }
    }

    private Action? RegisterMatchingLifecyclePublish(
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
                RecordMatchingLifecyclePersistenceFailureUnderGate(record.MatchingId);
                return null;
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
                RecordMatchingLifecyclePersistenceFailureUnderGate(record.MatchingId);
                return null;
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

        int dispatchStarted = 0;
        return () =>
        {
            if (Interlocked.Exchange(ref dispatchStarted, 1) != 0)
                return;

            try
            {
                _ = RunTrackedMatchingLifecyclePublishAsync(
                    outboxWorker,
                    record,
                    completeMatchPersistence,
                    completion);
            }
            catch
            {
                CompleteTrackedMatchingLifecyclePublish(
                    record,
                    completeMatchPersistence,
                    persistenceProtected: false,
                    completion);
                throw;
            }
        };
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
            CompleteTrackedMatchingLifecyclePublish(
                record,
                completeMatchPersistence,
                persistenceProtected,
                completion);
        }
    }

    private void CompleteTrackedMatchingLifecyclePublish(
        MatchingLifecycleOutboxRecord record,
        Action<bool> completeMatchPersistence,
        bool persistenceProtected,
        TaskCompletionSource<bool> completion)
    {
        try
        {
            completeMatchPersistence(persistenceProtected);
        }
        catch (Exception ex)
        {
            logger.LogCritical(
                ex,
                "Matching lifecycle persistence completion callback failed: Subject={Subject}, PlayerId={PlayerId}, MatchingId={MatchingId}",
                record.Subject,
                record.PlayerId,
                record.MatchingId);
        }
        finally
        {
            // The continuation removes this operation from the shutdown-drain tracker.
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

    private void StartAcceptingLegacyMatchingLifecyclePublishes()
    {
        lock (_matchingLifecycleEnqueueGate)
            Volatile.Write(ref _acceptingLegacyMatchingLifecyclePublishes, 1);
    }

    private void StopAcceptingLegacyMatchingLifecyclePublishes()
    {
        lock (_matchingLifecycleEnqueueGate)
            Volatile.Write(ref _acceptingLegacyMatchingLifecyclePublishes, 0);
    }

    private void RecordMatchingLifecyclePersistenceFailure(long matchingId)
    {
        if (!IsDurableMatchingLifecycleEnabled)
            return;

        lock (_matchingLifecycleEnqueueGate)
            RecordMatchingLifecyclePersistenceFailureUnderGate(matchingId);
    }

    private void RecordMatchingLifecyclePersistenceFailureUnderGate(long matchingId)
    {
        MatchingLifecyclePersistenceState state =
            _matchingLifecyclePersistenceStates.GetOrAdd(
                matchingId,
                static _ => new MatchingLifecyclePersistenceState());
        TaskCompletionSource<bool>? quiesced = null;
        lock (state.SyncRoot)
        {
            state.PersistenceFailed = true;
            if (state.PendingCount == 0 && state.ClosingRequested)
            {
                state.Sealed = true;
                quiesced = state.Quiesced;
            }
        }

        quiesced?.TrySetResult(true);
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
        if (!_matchingLifecyclePersistenceStates.TryGetValue(
                matchingId,
                out MatchingLifecyclePersistenceState? state))
        {
            if (!IsDurableMatchingLifecycleEnabled)
                return true;

            state = _matchingLifecyclePersistenceStates.GetOrAdd(
                matchingId,
                static _ => new MatchingLifecyclePersistenceState());
        }

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
                _swarmCombatPublicationCoordinator.TryCapturePacket,
                PublishOrderedSessionPublication,
                PublishRequiredTerminalAction,
                HandleSwarmGrowthPick,
                HandleSwarmOrbDecision,
                _matchRuntimeRegistry.TryAcquireOperation,
                _matchRuntimeRegistry.TryExecute,
                _matchRuntimeRegistry.TryBindOwnerFence,
                CleanupMatchRuntime,
                (playerId, matchingId) =>
                    PublishMatchingLifecycle(MatchingLifecycleSubjects.PlayerLeft, playerId, matchingId),
                (playerId, matchingId) =>
                    PrepareMatchingLifecyclePublication(
                        MatchingLifecycleSubjects.PlayerCompleted,
                        playerId,
                        matchingId),
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
        bool cleanupAccepted = TryAbortMatchAtTerminalBoundary(
            matchingId,
            () =>
            {
                if (_sessionRegistry.TryGetCurrent(playerId, out GameClientSession? currentSession) &&
                    currentSession != null &&
                    !ReferenceEquals(currentSession, session) &&
                    currentSession.CurrentMapSubId == matchingId)
                {
                    logger.LogDebug(
                        "Skipped admission-failed claim release for superseded session: PlayerId={PlayerId}, MatchingId={MatchingId}",
                        playerId,
                        matchingId);
                    return null;
                }

                List<GameClientSession> affectedSessions = GetSessionsByMatch(matchingId);
                foreach (GameClientSession affectedSession in affectedSessions)
                    affectedSession.TryMarkMatchingLifecycleHandledExternally();

                IReadOnlyCollection<long> affectedPlayerIds =
                    session.HandoffHumanPlayerIds.Count > 0
                        ? session.HandoffHumanPlayerIds.ToArray()
                        : [playerId];
                return new AdmissionFailureTerminalSnapshot(affectedSessions, affectedPlayerIds);
            },
            lifecyclePublications =>
            {
                PrepareAdmissionFailureLifecycle(
                    matchingId,
                    [playerId],
                    lifecyclePublications);
                try
                {
                    PublishAdmissionFailureTerminalDisconnect(matchingId, [session]);
                }
                catch (Exception ex)
                {
                    logger.LogWarning(
                        ex,
                        "Failed to disconnect late admission failure: PlayerId={PlayerId}, MatchingId={MatchingId}",
                        playerId,
                        matchingId);
                }
            },
            () =>
            {
                PublishLateDurableAdmissionFailure(matchingId, playerId);
                try
                {
                    session.DisconnectForAdmissionFailure();
                }
                catch (Exception ex)
                {
                    logger.LogWarning(
                        ex,
                        "Failed to disconnect completed-runtime admission failure: PlayerId={PlayerId}, MatchingId={MatchingId}",
                        playerId,
                        matchingId);
                }
            });

        if (cleanupAccepted)
        {
            logger.LogWarning(
                "Match aborted after client admission failure: MatchingId={MatchingId}, FailedPlayerId={PlayerId}",
                matchingId,
                playerId);
        }
    }

    /// <summary>
    ///     Freezes the admission-abort recipients and lifecycle work only for the caller that wins
    ///     Active -> Finalizing. The registry may attach a late before hook to already-pending work,
    ///     so the winner marker prevents a loser from publishing terminal packets or disconnecting
    ///     a different finalizer's roster. The required publication turn drains after all operation
    ///     leases retire and before component cleanup clears its coordinator entry.
    /// </summary>
    private bool TryAbortMatchAtTerminalBoundary(
        long matchingId,
        Func<AdmissionFailureTerminalSnapshot?> captureWinnerSnapshot,
        Action<List<Action>>? beforeLostFinalization = null,
        Action? afterBeforeSkipped = null)
    {
        AdmissionFailureTerminalSnapshot? winnerSnapshot = null;
        int winnerMarker = 0;
        int beforeHookRan = 0;
        var lifecyclePublications = new List<Action>();

        return TryCleanupMatchRuntime(
            matchingId,
            () =>
            {
                winnerSnapshot = captureWinnerSnapshot();
                if (winnerSnapshot == null)
                    return false;

                Volatile.Write(ref winnerMarker, 1);
                return true;
            },
            () =>
            {
                Volatile.Write(ref beforeHookRan, 1);
                if (Volatile.Read(ref winnerMarker) == 0 || winnerSnapshot == null)
                {
                    try
                    {
                        beforeLostFinalization?.Invoke(lifecyclePublications);
                    }
                    catch (Exception ex)
                    {
                        logger.LogError(
                            ex,
                            "Failed to prepare late admission abort finalization: MatchingId={MatchingId}",
                            matchingId);
                    }
                    return;
                }

                PrepareAdmissionFailureLifecycle(
                    matchingId,
                    winnerSnapshot.PlayerIds,
                    lifecyclePublications);
                PublishAdmissionFailureTerminalDisconnect(matchingId, winnerSnapshot.Sessions);
            },
            () =>
            {
                if (Volatile.Read(ref beforeHookRan) == 0)
                {
                    afterBeforeSkipped?.Invoke();
                    return;
                }

                DispatchPreparedAdmissionFailureLifecycle(matchingId, lifecyclePublications);
            });
    }

    private void PublishAdmissionFailureTerminalDisconnect(
        long matchingId,
        IReadOnlyList<GameClientSession> sessions)
    {
        try
        {
            PublishRequiredTerminalAction(
                matchingId,
                () =>
                {
                    foreach (GameClientSession affectedSession in sessions)
                    {
                        try
                        {
                            affectedSession.DisconnectForAdmissionFailure();
                        }
                        catch (Exception ex)
                        {
                            logger.LogWarning(
                                ex,
                                "Failed to deliver admission failure disconnect: PlayerId={PlayerId}, MatchingId={MatchingId}",
                                affectedSession.PlayerId,
                                matchingId);
                        }
                    }
                });
        }
        catch (Exception ex)
        {
            logger.LogWarning(
                ex,
                "Failed to acquire required terminal publication turn for admission failure: MatchingId={MatchingId}",
                matchingId);
        }
    }

    /// <summary>
    ///     Claims each player/subject during pre-finalization and retains only dispatch work for
    ///     post-commit. This keeps Redis/outbox registration inside the cleanup seal while allowing
    ///     a normal winner's already-prepared terminal subject to win over a late admission abort.
    /// </summary>
    private void PrepareAdmissionFailureLifecycle(
        long matchingId,
        IReadOnlyCollection<long> playerIds,
        List<Action> lifecyclePublications)
    {
        foreach (long playerId in playerIds)
        {
            try
            {
                Action? publication = PrepareMatchingLifecyclePublication(
                    MatchingLifecycleSubjects.PlayerAdmissionFailed,
                    playerId,
                    matchingId);
                if (publication != null)
                    lifecyclePublications.Add(publication);
            }
            catch (Exception ex)
            {
                logger.LogError(
                    ex,
                    "Failed to prepare admission failure lifecycle publication: PlayerId={PlayerId}, MatchingId={MatchingId}",
                    playerId,
                    matchingId);
            }
        }
    }

    private void DispatchPreparedAdmissionFailureLifecycle(
        long matchingId,
        IReadOnlyList<Action> lifecyclePublications)
    {
        foreach (Action publication in lifecyclePublications)
        {
            try
            {
                publication();
            }
            catch (Exception ex)
            {
                logger.LogError(
                    ex,
                    "Failed to dispatch prepared admission failure lifecycle: MatchingId={MatchingId}",
                    matchingId);
            }
        }
    }

    /// <summary>
    ///     Handles an admission callback that arrived after the runtime's before-finalized phase
    ///     was no longer attachable. The normal local registration gate is already sealed here, so
    ///     durable deployments enqueue directly through the existing outbox/abort-fence decision
    ///     path. The process-local terminal-subject claim keeps this from overriding a winner that
    ///     had already prepared Completed, Released, or AdmissionFailed for the same player.
    /// </summary>
    private void PublishLateDurableAdmissionFailure(long matchingId, long playerId)
    {
        if (!TryRegisterMatchingLifecycleTerminal(
                MatchingLifecycleSubjects.PlayerAdmissionFailed,
                playerId,
                matchingId))
        {
            return;
        }

        if (!IsDurableMatchingLifecycleEnabled)
        {
            try
            {
                PrepareLegacyMatchingLifecyclePublication(
                    MatchingLifecycleSubjects.PlayerAdmissionFailed,
                    playerId,
                    matchingId).Invoke();
            }
            catch (Exception ex)
            {
                logger.LogError(
                    ex,
                    "Failed to publish late legacy admission failure: PlayerId={PlayerId}, MatchingId={MatchingId}",
                    playerId,
                    matchingId);
            }
            return;
        }

        MatchingLifecycleOutboxWorker? outboxWorker = _matchingLifecycleOutboxWorker;
        if (outboxWorker == null)
        {
            logger.LogCritical(
                "Late admission failure cannot reach the durable outbox: PlayerId={PlayerId}, MatchingId={MatchingId}",
                playerId,
                matchingId);
            RecordMatchingLifecyclePersistenceFailure(matchingId);
            return;
        }

        try
        {
            string subject = MatchingLifecycleSubjects.PlayerAdmissionFailed;
            string eventId = MatchingLifecycleMessageIds.Create(subject, playerId, matchingId);
            var envelope = new MatchingLifecycleEnvelope
            {
                PlayerId = playerId,
                MatchingId = matchingId,
                OccurredAtUnixMilliseconds = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds(),
                EventId = eventId
            };
            var record = new MatchingLifecycleOutboxRecord
            {
                EventId = eventId,
                EventIdFingerprint = MatchingLifecycleOutboxKeys.FingerprintEventId(eventId),
                Subject = subject,
                Payload = MessagePackSerializer.Serialize(envelope),
                PlayerId = playerId,
                MatchingId = matchingId
            };
            long operationId = Interlocked.Increment(ref _nextMatchingLifecyclePublishId);
            Task persistence = PersistLateAdmissionFailureAsync(outboxWorker, record);
            if (!_pendingMatchingLifecyclePublishTasks.TryAdd(operationId, persistence))
                throw new InvalidOperationException($"Duplicate late lifecycle operation id: {operationId}.");

            _ = persistence.ContinueWith(
                _completedTask => _pendingMatchingLifecyclePublishTasks.TryRemove(operationId, out _),
                CancellationToken.None,
                TaskContinuationOptions.ExecuteSynchronously,
                TaskScheduler.Default);
        }
        catch (Exception ex)
        {
            logger.LogCritical(
                ex,
                "Failed to schedule late durable admission failure: PlayerId={PlayerId}, MatchingId={MatchingId}",
                playerId,
                matchingId);
            RecordMatchingLifecyclePersistenceFailure(matchingId);
        }
    }

    private async Task PersistLateAdmissionFailureAsync(
        MatchingLifecycleOutboxWorker outboxWorker,
        MatchingLifecycleOutboxRecord record)
    {
        bool persistenceProtected = await PersistMatchingLifecycleDecisionAsync(outboxWorker, record);
        if (!persistenceProtected)
        {
            logger.LogCritical(
                "Late durable admission failure has no confirmed persistence protection: PlayerId={PlayerId}, MatchingId={MatchingId}",
                record.PlayerId,
                record.MatchingId);
        }
    }

    /// <summary>
    ///     사람 세션 없이 진행되는 매치(관리자 봇 전용 인스턴스)를 정산한다.
    ///     승리 판정이 사람 세션에 의존해 최후 1인이 남아도 끝나지 않고, 오염도가 한계에
    ///     닿은 봇이 계속 살아 있는 상태로 매치가 무한히 이어지던 것을 막는다.
    ///     The depth-zero before hook captures its event and summary outside SyncRoot but opens no
    ///     required publication turn because there are no human recipients. Component cleanup remains
    ///     under SyncRoot and summary persistence remains post-commit outside it.
    /// </summary>
    public void EndBotOnlyMatchIfSettled(long matchingId, long winnerPlayerId)
    {
        if (HasHumanSessions(matchingId))
            return;

        CleanupMatchingIfNoHumanSessionsRemain(matchingId, "last_survivor_bot_only", winnerPlayerId);
    }

    private void CleanupMatchingIfNoHumanSessionsRemain(long matchingId) =>
        CleanupMatchingIfNoHumanSessionsRemain(matchingId, "last_human_left", 0);

    /// <summary>
    ///     Finalizes a match only when its runtime-guarded predicate still sees no human sessions.
    ///     The depth-zero before hook captures the terminal event and summary outside SyncRoot without
    ///     opening a required publication turn; component cleanup stays under SyncRoot and persistence
    ///     remains an outside post-commit continuation.
    /// </summary>
    private void CleanupMatchingIfNoHumanSessionsRemain(long matchingId, string endReason, long winnerPlayerId)
    {
        if (HasHumanSessions(matchingId))
            return;

        MatchSummaryPersistenceRequest? summaryRequest = null;
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
                summaryRequest = MatchSummaryPersistence.Capture(
                    _gameEventLogManager,
                    logger,
                    matchingId,
                    endReason,
                    winnerPlayerId);
            },
            () =>
            {
                MatchSummaryPersistenceRequest? capturedSummary = summaryRequest;
                if (capturedSummary != null)
                {
                    MatchSummaryPersistence.Persist(
                        capturedSummary,
                        _matchSummaryFileStore,
                        logger);
                }
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
    ///     The claimed before hook runs outside SyncRoot, each isolated component cleanup runs under it,
    ///     and Redis, lifecycle, and summary continuations run after commit outside it. One component
    ///     failure cannot prevent the remaining managers from releasing their matchingId state.
    /// </summary>
    private void CleanupMatchRuntime(long matchingId) =>
        TryCleanupMatchRuntime(matchingId, null, null);

    private void CleanupMatchRuntime(
        long matchingId,
        Action? beforeFinalized,
        Action? afterFinalized)
    {
        TryCleanupMatchRuntime(matchingId, null, beforeFinalized, afterFinalized);
    }

    private bool TryCleanupMatchRuntime(
        long matchingId,
        Func<bool>? canFinalize,
        Action? beforeFinalized,
        Action? afterFinalized = null)
    {
        return _matchRuntimeCleanupCoordinator.TryFinalize(
            matchingId,
            canFinalize,
            beforeFinalized,
            afterFinalized);
    }

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

    private Action PrepareMatchingRedisCleanup(long matchingId)
    {
        long operationId = Interlocked.Increment(ref _nextMatchingRedisCleanupId);
        var completion = new TaskCompletionSource<bool>(
            TaskCreationOptions.RunContinuationsAsynchronously);
        if (!_pendingMatchingRedisCleanupTasks.TryAdd(operationId, completion.Task))
        {
            throw new InvalidOperationException(
                $"Duplicate matching Redis cleanup operation id: {operationId}.");
        }

        int dispatchStarted = 0;
        return () =>
        {
            if (Interlocked.Exchange(ref dispatchStarted, 1) != 0)
                return;

            try
            {
                _ = RunTrackedMatchingRedisCleanupAsync(
                    matchingId,
                    operationId,
                    completion);
            }
            catch (Exception ex)
            {
                logger.LogCritical(
                    ex,
                    "Unexpected matching Redis cleanup dispatch failure: MatchingId={MatchingId}, OperationId={OperationId}",
                    matchingId,
                    operationId);
                CompleteMatchingRedisCleanup(operationId, completion);
            }
        };
    }

    private async Task RunTrackedMatchingRedisCleanupAsync(
        long matchingId,
        long operationId,
        TaskCompletionSource<bool> completion)
    {
        try
        {
            await CleanupAbandonedMatchingRedisAsync(matchingId);
        }
        catch (Exception ex)
        {
            logger.LogCritical(
                ex,
                "Unexpected matching Redis cleanup failure: MatchingId={MatchingId}, OperationId={OperationId}",
                matchingId,
                operationId);
        }
        finally
        {
            CompleteMatchingRedisCleanup(operationId, completion);
        }
    }

    private void CompleteMatchingRedisCleanup(
        long operationId,
        TaskCompletionSource<bool> completion)
    {
        try
        {
            completion.TrySetResult(true);
        }
        finally
        {
            ((ICollection<KeyValuePair<long, Task>>)_pendingMatchingRedisCleanupTasks)
                .Remove(new KeyValuePair<long, Task>(operationId, completion.Task));
        }
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
