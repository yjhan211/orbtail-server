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
using network.core;
using network.gamehandoff;
using network.helpers;
using network.hosting;
using network.infrastructure;
using network.infrastructure.routing;
using network.interfaces;
using network.packets;

namespace game_server;

/// <summary>
///     매치 입장·시뮬레이션·종료 수명을 소유하는 호스트. 매치 하나의 권위 상태 변경은 전부
///     <see cref="MatchRuntime.Sync"/> 잠금 안에서 돌고(타이머 틱·세션 핸들러·종료), 패킷도 그 안에서 큐에
///     넣어 순서가 곧 변경 순서다. 터미널 정리는 최외곽 잠금 탈출에서 한 번 돌고, lifecycle·Redis·요약 후처리는
///     잠금 밖에서 이어진다. 노드는 기동 시 레지스트리에 자기를 광고하고 자기 노드에 결합된 ticket만 받는다 (#339);
///     소유권 fence·durable outbox 계층은 두지 않는다 (태그 pre-stage2-scaling에 보존).
/// </summary>
public partial class GameServer(
    IConfiguration configuration,
    ILogger<GameServer> logger,
    INatsClientFactory natsClientFactory,
    ICacheHelper cacheHelper,
    INetworkService networkService,
    ServerConfig serverConfig,
    IGameHandoffTicketService gameHandoffTicketService,
    ServerReadinessState readinessState,
    IGameServerRegistry gameServerRegistry,
    GameServerNodeOptions nodeOptions)
    : IHostedService
{
    private const int HeartbeatCheckIntervalSeconds = 10;
    private const int MatchingLifecycleTerminalMatchRetention = 4096;
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
    private MatchRuntimeStore? _matchRuntimes;
    private SwarmBotMovementCoordinator _swarmBotMovementCoordinator = null!;
    private readonly ConcurrentDictionary<long, Task> _pendingMatchingRedisCleanupTasks = new();
    // 재시작해도 되감기지 않도록 기동 시각을 섞는다. 고정 시드로 시작하면 서버를 다시
    // 올릴 때마다 같은 matchingId가 나오고, 매치 요약 파일이 같은 이름을 만나
    // 저장이 통째로 건너뛰어진다(기존 파일 우선 규칙).
    private long _adminBotOnlyMatchingIdSeed =
        9_000_000 + DateTime.UtcNow.Ticks / TimeSpan.TicksPerSecond % 900_000;
    private long _adminBotOnlyPlayerIdSeed = -900_000_000;
    private INatsClient? _matchingLifecycleNatsClient;
    private long _nextMatchingRedisCleanupId;
    private int _stopping;

    private CancellationTokenSource _cts = new();
    private Timer? _heartbeatCheckTimer;
    private Timer? _resourceTickTimer;        // 폐쇄 구역 등 주기성 자원 변화
    private Timer? _areaClosureTickTimer;     // 구역 폐쇄 체크
    private Timer? _targetLocationTimer;      // 타겟 위치 전송
    private GameServerNodeAdvertiser? _nodeAdvertiser; // 레지스트리 광고 — Start/Stop 순서 안에서만 만지고 지운다

    private SwarmMatchRuntime GetSwarmMatchRuntime(long matchingId) =>
        _swarmMatchRuntimes.GetOrCreate(matchingId);

    private Random GetItemCombineRandom(long matchingId) =>
        GetSwarmMatchRuntime(matchingId).ItemCombineRandom;

    /// <summary>
    ///     매치별 잠금·수명 색인 (#331). 필드 초기화자는 this를 참조할 수 없어 첫 접근에서 만든다 —
    ///     정리 단계가 매니저 인스턴스를 잡아야 하기 때문이다.
    /// </summary>
    internal MatchRuntimeStore MatchRuntimes =>
        LazyInitializer.EnsureInitialized(ref _matchRuntimes, CreateMatchRuntimeStore)!;

    private MatchRuntimeStore CreateMatchRuntimeStore() =>
        new(logger, CreateMatchRuntime, BuildMatchCleanupSteps(), StartMatchingRedisCleanup);

    /// <summary>새 매치 런타임의 모니터 안에서 한 번 실행되는 fail-closed 컴포넌트 등록.</summary>
    private void CreateMatchRuntime(long matchingId)
    {
        if (!_doorStateManager.RegisterMatching(matchingId))
        {
            throw new InvalidOperationException(
                $"Door state was already registered for match {matchingId}.");
        }
    }

    /// <summary>
    ///     터미널 정리 순서. 최외곽 잠금 탈출에서 한 번 돌고 단계마다 예외를 격리한다 — 한 컴포넌트 실패가
    ///     나머지 매니저의 matchingId 상태 해제를 막지 않는다.
    /// </summary>
    private IReadOnlyList<MatchCleanupStep> BuildMatchCleanupSteps() =>
    [
        new MatchCleanupStep("session runtime", GameClientSession.CleanupAbandonedMatchingRuntime),
        new MatchCleanupStep("session index", _sessionRegistry.RemoveMatch),
        new MatchCleanupStep("settlement", CleanupMatchSettlementState),
        new MatchCleanupStep("swarm arena", CleanupSwarmArenaState),
        new MatchCleanupStep("area closure", matchingId => _areaClosureManager.CleanupMatching(matchingId)),
        new MatchCleanupStep("bots", _botPlayerManager.CleanupMatching),
        new MatchCleanupStep("area item stock", _areaItemStockManager.RemoveMatchingState),
        new MatchCleanupStep("ground items", _groundItemManager.RemoveMatchingState),
        new MatchCleanupStep("monster broadcast slots", CleanupSwarmAfterimageMonsterRuntime),
        new MatchCleanupStep("summon stones", _summonStoneManager.RemoveMatchingState),
        new MatchCleanupStep("inventory", _inGameInventoryManager.RemoveMatchingState),
        new MatchCleanupStep("interactables", _interactableStateManager.RemoveMatchingState),
        new MatchCleanupStep("doors", _doorStateManager.ClearMatching),
        new MatchCleanupStep("roster", _matchRosterManager.CleanupMatching),
        new MatchCleanupStep("encounter reveal", _encounterRevealManager.CleanupMatching),
        new MatchCleanupStep("event log", _gameEventLogManager.Clear)
    ];

    /// <summary>정리가 끝난 매치의 Redis 인계 키를 잠금 밖에서 지운다 (셧다운이 완료를 기다린다).</summary>
    private void StartMatchingRedisCleanup(long matchingId) =>
        PrepareMatchingRedisCleanup(matchingId).Invoke();

    internal const int ResourceTickIntervalSeconds = 5;
    // ClosedAreaStaminaPenaltyPerTick 제거 — v0.1.9 #66: 폐쇄 구역 패널티 → 오염도로 변경

    public async Task StartAsync(CancellationToken cancellationToken)
    {
        Volatile.Write(ref _stopping, 0);
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
            StartProximityAutoCombatTimer();

            // 광고는 리슨·타이머가 모두 선 뒤에 — 배정받은 클라이언트가 바로 접속할 수 있어야 한다.
            _nodeAdvertiser = new GameServerNodeAdvertiser(
                gameServerRegistry,
                nodeOptions,
                serverConfig.GameServerNodeId,
                () => MatchRuntimes.ActiveIds().Count,
                logger);
            await _nodeAdvertiser.StartAsync();

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
        // Publish the stopping state before taking the session snapshot. Sessions accepted at
        // this boundary observe the same penalty-free claim-release policy in OnDisconnect.
        Volatile.Write(ref _stopping, 1);
        readinessState.MarkNotReady("stopping");
        logger.LogInformation("Game server stopping...");

        // 새 배정을 먼저 막는다 — 이 뒤로 발급되는 ticket은 다른 노드를 가리킨다.
        if (_nodeAdvertiser != null)
            await RunShutdownStageAsync(_nodeAdvertiser.StopAcceptingAsync(), "node registry draining");

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
            _proximityAutoCombatTimer
        ];
        _heartbeatCheckTimer = null;
        _resourceTickTimer = null;
        _areaClosureTickTimer = null;
        _targetLocationTimer = null;
        _proximityAutoCombatTimer = null;
        await RunShutdownStageAsync(
            Task.WhenAll(timers.Where(timer => timer != null)
                .Select(timer => timer!.DisposeAsync().AsTask())),
            "timers");

        await RunShutdownStageAsync(
            WaitForPendingMatchingRedisCleanupsAsync(),
            "matching Redis cleanup");

        if (_nodeAdvertiser != null)
        {
            await RunShutdownStageAsync(_nodeAdvertiser.RemoveAsync(), "node registry removal");
            await _nodeAdvertiser.DisposeAsync();
            _nodeAdvertiser = null;
        }

        _cts.Dispose();
        await CloseMatchingLifecycleNatsClientAsync();

        logger.LogInformation("Game server stopped.");
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
            // 서버 환경에서 CSV 파일 경로 설정
            // Dev: 소스 디렉토리에서 직접 읽기 (Docker 볼륨 마운트 대응)
            string networkSourcePath = Path.GetFullPath(Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "..", "..",
                "..", "..", "network"));
            GameDataHelper.SetBasePath(Directory.Exists(Path.Combine(networkSourcePath, "Common", "csv"))
                ? networkSourcePath
                : AppDomain.CurrentDomain.BaseDirectory);
            GameDataHelper.Initialize();
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
            foreach (long matchingId in MatchRuntimes.ActiveIds())
            {
                // #222 M3-2: 폐쇄·오버타임 오염은 이 정산 틱이 적용한다.
                if (!MatchStartGate.IsGameplayActive(matchingId))
                    continue;
                if (!MatchRuntimes.Enter(matchingId, out MatchScope scope))
                    continue;

                using (scope)
                {
                    if (scope.Runtime.IsTerminal)
                        continue;

                    ProcessResourceTickForMatching(matchingId, activeSessions);
                }
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
    // 드롭이 통째로 사라졌다). 스팟 아레나 세대가 #222에서 같은 이유로 8개씩 나눈 전례를 따른다.
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

    /// <summary>
    ///     1초 폐쇄 틱. 매치 잠금 안에서 폐쇄 상태를 확정하고 같은 순서로 송신한다 — 폐쇄는 50ms
    ///     전투 틱보다 드물어 잠금을 기다려도 된다.
    /// </summary>
    private void ProcessAreaClosureTick(object? state)
    {
        try
        {
            foreach (long matchingId in MatchRuntimes.ActiveIds())
            {
                // #272 자기장 폐쇄: 자기장에서 파생한 구역 시간표 하나로만 닫는다 —
                // 필드 오염은 정산 리소스 틱(GetSwarmFieldCorruptionPerTick)이 준다.
                if (!MatchStartGate.IsGameplayActive(matchingId)) continue;
                if (!MatchRuntimes.Enter(matchingId, out MatchScope scope))
                    continue;

                using (scope)
                {
                    if (scope.Runtime.IsTerminal)
                        continue;

                    GameClientSession[] sessionSnapshot = GetSessionsByMatch(matchingId).ToArray();
                    SwarmClosurePublicationPlan? plan =
                        PrepareSwarmScheduledClosureTick(matchingId, sessionSnapshot);
                    if (plan != null)
                        DispatchSwarmClosurePublicationPlan(plan, sessionSnapshot);
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

    /// <summary>
    ///     카운트다운 초 방송 — 봇 이동 워커가 매치 잠금 안에서 부른다(직접 호출도 잠금을 잡는다). 남은 초가
    ///     마지막 방송과 같으면 보내지 않고, 초가 바뀌면 수신자가 없어도 방송한 것으로 기록한다(재시도 없음).
    /// </summary>
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

            if (!MatchRuntimes.Enter(matchingId, out MatchScope scope))
                continue;

            using (scope)
            {
                if (scope.Runtime.IsTerminal)
                    continue;

                var snapshot = MatchStartGate.GetSnapshot(matchingId);
                if (!snapshot.IsKnown)
                    continue;

                SwarmMatchPacingState pacing = GetSwarmMatchRuntime(matchingId).Pacing;
                if (pacing.LastCountdownSecondsPublished == snapshot.RemainingSeconds)
                    continue;

                pacing.LastCountdownSecondsPublished = snapshot.RemainingSeconds;
                var matchingSessions = activeSessions
                    .Where(session => session.CurrentMapSubId == matchingId)
                    .ToList();
                if (matchingSessions.Count == 0)
                    continue;

                using var packet = Packet.Create((int)Protocol.G_TO_C_MATCH_START_COUNTDOWN);
                packet.SetBody(MessagePackSerializer.Serialize(new G_TO_C_MATCH_START_COUNTDOWN
                {
                    MatchingId = matchingId,
                    RemainingSeconds = snapshot.RemainingSeconds,
                    ServerUnixMs = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds()
                }));

                foreach (var session in matchingSessions)
                    session.Send(packet);
            }
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


    /// <summary>
    ///     매칭 수명주기 이벤트를 NATS Core로 즉시 발행한다 (best-effort, at-most-once).
    ///     유실 시 안전망은 user_server의 claim TTL·admission watchdog이다.
    /// </summary>
    private void PublishMatchingLifecycle(string subject, long playerId, long matchingId)
    {
        Action? dispatch = PrepareMatchingLifecyclePublication(subject, playerId, matchingId);
        dispatch?.Invoke();
    }

    /// <summary>
    ///     플레이어별 terminal subject를 지금 선점하고, 실제 Core publish는 한 번만 실행되는 Action으로
    ///     돌려준다. 완료(PlayerCompleted) 이벤트는 match runtime finalization commit 뒤에 dispatch된다.
    /// </summary>
    private Action? PrepareMatchingLifecyclePublication(string subject, long playerId, long matchingId)
    {
        if (!TryRegisterMatchingLifecycleTerminal(subject, playerId, matchingId))
            return null;

        int dispatchStarted = 0;
        return () =>
        {
            if (Interlocked.Exchange(ref dispatchStarted, 1) != 0)
                return;

            PublishMatchingLifecycleCore(subject, playerId, matchingId);
        };
    }

    /// <summary>
    ///     한 매치의 한 플레이어는 terminal subject(left/completed/admission_failed/released)를 하나만
    ///     발행한다. left+completed 이중 발행은 user_server의 이탈 페널티 계산을 깨뜨린다.
    /// </summary>
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

    private void PublishMatchingLifecycleCore(string subject, long playerId, long matchingId)
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
                ticket => gameHandoffTicketService.ConsumeAsync(ticket, serverConfig.GameServerNodeId),
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
                MatchRuntimes,
                HandleSwarmGrowthPick,
                HandleSwarmOrbDecision,
                GetItemCombineRandom,
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

    /// <summary>
    ///     입장 실패로 매치를 중단한다. 터미널 전이를 이긴 호출이 잠금 안에서 로스터 전원의 lifecycle subject
    ///     선점과 FATAL 응답·끊기를 소유하고(발행은 잠금 밖 후처리), 이미 끝난 매치에 늦게 온 호출은
    ///     자기 세션의 admission_failed 발행과 끊기만 한다 — 정상 종료가 먼저 선점한 subject는 중복 제거된다.
    /// </summary>
    private void AbortMatchAfterAdmissionFailure(GameClientSession session)
    {
        if (!session.PlayerId.HasValue || session.CurrentMapSubId <= 0)
            return;

        long playerId = session.PlayerId.Value;
        long matchingId = session.CurrentMapSubId;
        MatchRuntime? runtime = MatchRuntimes.Get(matchingId);
        if (runtime == null)
        {
            PublishLateAdmissionFailure(session, playerId, matchingId);
            return;
        }

        bool wonTerminal = false;
        using (MatchRuntimes.Enter(runtime))
        {
            if (!runtime.IsTerminal)
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
                    return;
                }

                wonTerminal = runtime.TryMarkTerminal();
                List<GameClientSession> affectedSessions = GetSessionsByMatch(matchingId);
                foreach (GameClientSession affectedSession in affectedSessions)
                    affectedSession.TryMarkMatchingLifecycleHandledExternally();

                IReadOnlyCollection<long> affectedPlayerIds =
                    session.HandoffHumanPlayerIds.Count > 0
                        ? session.HandoffHumanPlayerIds.ToArray()
                        : [playerId];
                var lifecyclePublications = new List<Action>();
                PrepareAdmissionFailureLifecycle(matchingId, affectedPlayerIds, lifecyclePublications);
                foreach (GameClientSession affectedSession in affectedSessions)
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

                runtime.AfterRelease.Add(
                    () => DispatchPreparedAdmissionFailureLifecycle(matchingId, lifecyclePublications));
            }
        }

        if (wonTerminal)
        {
            logger.LogWarning(
                "Match aborted after client admission failure: MatchingId={MatchingId}, FailedPlayerId={PlayerId}",
                matchingId,
                playerId);
            return;
        }

        PublishLateAdmissionFailure(session, playerId, matchingId);
    }

    /// <summary>이미 끝난 매치에 늦게 도착한 입장 실패 — 이 세션 한 명만 발행·끊는다.</summary>
    private void PublishLateAdmissionFailure(GameClientSession session, long playerId, long matchingId)
    {
        PublishMatchingLifecycle(
            MatchingLifecycleSubjects.PlayerAdmissionFailed,
            playerId,
            matchingId);
        try
        {
            session.DisconnectForAdmissionFailure();
        }
        catch (Exception ex)
        {
            logger.LogWarning(
                ex,
                "Failed to disconnect late admission failure: PlayerId={PlayerId}, MatchingId={MatchingId}",
                playerId,
                matchingId);
        }
    }

    /// <summary>
    ///     잠금 안에서 플레이어별 subject를 선점하고 발행 작업만 남긴다 — 정상 종료가 먼저 선점한 subject는
    ///     늦은 입장 중단이 덮어쓰지 못한다.
    /// </summary>    /// <summary>
    ///     Claims each player/subject during pre-finalization and retains only dispatch work for
    ///     post-commit, so a normal winner's already-prepared terminal subject wins over a late
    ///     admission abort.
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
    ///     사람 세션 없이 진행되는 매치(관리자 봇 전용 인스턴스)를 정산한다.
    ///     승리 판정이 사람 세션에 의존해 최후 1인이 남아도 끝나지 않고, 오염도가 한계에
    ///     닿은 봇이 계속 살아 있는 상태로 매치가 무한히 이어지던 것을 막는다.
    ///     정산 틱 안에서 불리면 재진입이라 정리는 그 틱이 끝난 뒤에 돈다.
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
    ///     사람 세션이 하나도 남지 않은 매치를 잠금 안에서 터미널로 표시한다. 마지막 이벤트·요약은 잠금 안에서
    ///     캡처하고 파일 쓰기는 잠금 밖 후처리로 돈다. 사람 수신자가 없으므로 결과 패킷은 없다.
    /// </summary>
    private void CleanupMatchingIfNoHumanSessionsRemain(long matchingId, string endReason, long winnerPlayerId)
    {
        if (HasHumanSessions(matchingId))
            return;

        MatchRuntime? runtime = MatchRuntimes.Get(matchingId);
        if (runtime == null)
            return;

        using MatchScope scope = MatchRuntimes.Enter(runtime);
        if (runtime.IsTerminal || HasHumanSessions(matchingId))
            return;

        runtime.TryMarkTerminal();
        if (_gameEventLogManager.TryBeginFinalization(matchingId))
        {
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
            MatchSummaryPersistenceRequest? summaryRequest = MatchSummaryPersistence.Capture(
                _gameEventLogManager,
                logger,
                matchingId,
                endReason,
                winnerPlayerId);
            if (summaryRequest != null)
            {
                runtime.AfterRelease.Add(() => MatchSummaryPersistence.Persist(
                    summaryRequest,
                    _matchSummaryFileStore,
                    logger));
            }
        }

        logger.LogInformation(
            "Removed matching without human sessions: MatchingId={MatchingId}, EndReason={EndReason}",
            matchingId, endReason);
    }

    private async Task CleanupAbandonedMatchingRedisAsync(long matchingId)
    {
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

        MatchRuntime runtime = MatchRuntimes.GetOrCreate(matchingId);
        using (MatchRuntimes.Enter(runtime))
        {
            if (runtime.IsTerminal)
                return null;

            // 사람이 없으므로 카운트다운 없이 즉시 활성 — 미등록 매치는 게이트가 막는다 (#335).
            MatchStartGate.RegisterBotOnlyMatch(matchingId);
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
        }

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
