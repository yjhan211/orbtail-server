using System.Net;
using game_server.network;
using game_server.services;
using MessagePack;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using network.common;
using network.common.data.helpers;
using network.common.data.models;
using network.core;
using network.gamehandoff;
using network.hosting;
using network.infrastructure.messaging;
using network.infrastructure.redis;
using network.packets;
using network.routing;

namespace game_server;

/// <summary>
///     GameServer의 시작과 종료를 관리하고, 게임 세션과 매치 처리에 필요한 구성 요소를 연결한다.
///     TCP 연결을 받고 게임 진행용 타이머를 실행하며, 노드의 접속 정보와 수용 상태를 등록한다.
///
///     매치 상태 변경은 매치별 잠금 안에서 처리한다.
///     입장 실패 처리는 MatchEntryFailureHandler에,
///     종료 알림과 Redis 정리는 MatchingLifecycleService에 위임한다.
/// </summary>
public partial class GameServer(
    IConfiguration configuration,
    ILogger<GameServer> logger,
    NatsClientFactory natsClientFactory,
    IRedisOperations redisOperations,
    NetworkService networkService,
    GameHandoffTicketService gameHandoffTicketService,
    ServerReadinessState readinessState,
    IGameServerRegistry gameServerRegistry,
    GameServerNodeOptions nodeOptions,
    GameServerDevOptions devOptions)
    : IHostedService
{
    private const int ResourceTickIntervalSeconds = 5;
    private static readonly TimeSpan ShutdownStageTimeout = TimeSpan.FromSeconds(5);

    private readonly GameSessionRegistry _sessionRegistry = new();
    private readonly MatchRosterManager _matchRosterManager = new(logger);
    private readonly SwarmMatchRuntimeStore _swarmMatchRuntimes = new();
    private MatchRuntimeStore? _matchRuntimes;
    private MatchEntryFailureHandler? _entryFailureHandler;

    private readonly InGameInventoryManager _inGameInventoryManager = new();
    private readonly InteractableStateManager _interactableStateManager = new();
    private readonly GroundItemManager _groundItemManager = new();
    private readonly SummonStoneManager _summonStoneManager = new();
    private readonly EncounterRevealManager _encounterRevealManager = new();
    private AreaClosureManager _areaClosureManager = null!;

    // 봇과 몬스터
    private readonly BotPlayerManager _botPlayerManager = new(logger);
    private readonly SwarmMonsterDirector _swarmMonsterDirector =
        new(monsterSpawnEnabled: devOptions.MonsterSpawnEnabled);
    private SwarmBotMovementCoordinator _swarmBotMovementCoordinator = null!;

    // 매치 기록
    private readonly GameEventLogManager _gameEventLogManager = new();
    private readonly MatchSummaryFileStore _matchSummaryFileStore = new(
        configuration["MATCH_SUMMARY_DIRECTORY"],
        configuration.GetValue<int>("MATCH_SUMMARY_MAX_FILES", MatchSummaryFileStore.DefaultMaxSummaries));

    // 서버 수명과 주기 작업
    private CancellationTokenSource _cts = new();
    private Timer? _resourceTickTimer;
    private Timer? _areaClosureTickTimer;
    private GameServerNodeAdvertiser? _nodeAdvertiser;
    private int _stopping;

    internal MatchingLifecycleService MatchingLifecycle { get; } = new(redisOperations, logger);

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

    internal MatchEntryFailureHandler EntryFailureHandler =>
        LazyInitializer.EnsureInitialized(ref _entryFailureHandler,
            () => new MatchEntryFailureHandler(MatchRuntimes, _sessionRegistry, MatchingLifecycle, logger))!;

    private MatchRuntimeStore CreateMatchRuntimeStore() =>
        new(logger, cleanupSteps: BuildMatchCleanupSteps(), afterCleanup: StartMatchingRedisCleanup);

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
        new MatchCleanupStep("ground items", _groundItemManager.RemoveMatchingState),
        new MatchCleanupStep("monster broadcast slots", CleanupSwarmAfterimageMonsterRuntime),
        new MatchCleanupStep("summon stones", _summonStoneManager.RemoveMatchingState),
        new MatchCleanupStep("inventory", _inGameInventoryManager.RemoveMatchingState),
        new MatchCleanupStep("interactables", _interactableStateManager.RemoveMatchingState),
        new MatchCleanupStep("roster", _matchRosterManager.CleanupMatching),
        new MatchCleanupStep("encounter reveal", _encounterRevealManager.CleanupMatching),
        new MatchCleanupStep("event log", _gameEventLogManager.Clear)
    ];

    /// <summary>정리가 끝난 매치의 Redis 인계 키를 잠금 밖에서 지운다 (셧다운이 완료를 기다린다).</summary>
    private void StartMatchingRedisCleanup(long matchingId) =>
        MatchingLifecycle.PrepareRedisCleanup(matchingId).Invoke();

    public async Task StartAsync(CancellationToken cancellationToken)
    {
        Volatile.Write(ref _stopping, 0);
        readinessState.MarkNotReady("starting");
        _cts.Dispose();
        _cts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);

        try
        {
            logger.LogInformation("Game server starting...");
            IReadOnlyList<string> enabledDevFlags = devOptions.EnabledVariableNames();
            if (enabledDevFlags.Count > 0)
                logger.LogWarning("[DEV] Game Server flags enabled: {Flags}",
                    string.Join(", ", enabledDevFlags));

            InitializeServices();

            StartTcpServer();
            StartResourceTickTimer();
            StartAreaClosureTickTimer();
            StartProximityAutoCombatTimer();

            // 광고는 리슨·타이머가 모두 선 뒤에 — 배정받은 클라이언트가 바로 접속할 수 있어야 한다.
            _nodeAdvertiser = new GameServerNodeAdvertiser(
                gameServerRegistry,
                nodeOptions,
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
        // this boundary follow the same reservation-release policy in OnDisconnect.
        Volatile.Write(ref _stopping, 1);
        readinessState.MarkNotReady("stopping");
        logger.LogInformation("Game server stopping...");

        // 새 배정을 먼저 막는다 — 이 뒤로 발급되는 ticket은 다른 노드를 가리킨다.
        if (_nodeAdvertiser != null)
            await RunShutdownStageAsync(_nodeAdvertiser.StopAcceptingAsync(), "node registry draining");

        // 서버 셧다운 시 모든 세션을 서버 주도 종료로 마킹 → released terminal로 reservation 해제
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
            _resourceTickTimer,
            _areaClosureTickTimer,
            _proximityAutoCombatTimer
        ];
        _resourceTickTimer = null;
        _areaClosureTickTimer = null;
        _proximityAutoCombatTimer = null;
        await RunShutdownStageAsync(
            Task.WhenAll(timers.Where(timer => timer != null)
                .Select(timer => timer!.DisposeAsync().AsTask())),
            "timers");

        await RunShutdownStageAsync(
            MatchingLifecycle.DrainAsync(),
            "matching Redis cleanup");

        if (_nodeAdvertiser != null)
        {
            await RunShutdownStageAsync(_nodeAdvertiser.RemoveAsync(), "node registry removal");
            await _nodeAdvertiser.DisposeAsync();
            _nodeAdvertiser = null;
        }

        _cts.Dispose();
        await MatchingLifecycle.CloseAsync();

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
        _areaClosureManager = new AreaClosureManager(logger);
        _swarmBotMovementCoordinator = new SwarmBotMovementCoordinator(
            _botPlayerManager,
            _areaClosureManager,
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
            MatchingLifecycle.Start(natsClientFactory.Create());
            // 서버 환경에서 CSV 파일 경로 설정
            // Dev: 소스 디렉토리에서 직접 읽기 (Docker 볼륨 마운트 대응)
            string networkSourcePath = Path.GetFullPath(Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "..", "..",
                "..", "..", "network"));
            GameDataHelper.SetBasePath(Directory.Exists(Path.Combine(networkSourcePath, "Common", "csv"))
                ? networkSourcePath
                : AppDomain.CurrentDomain.BaseDirectory);
            GameDataHelper.Initialize(
                message => logger.LogDebug("{Message}", message),
                message => logger.LogError("{Message}", message));
            Action<string> log = msg => logger.LogInformation(msg);
            // 문 상태는 페이즈와 별개다 (2026-08-16). 위 제공자는 ROOM_COMBAT에서만 채워져
            // 군집 모드에서는 항상 비었고, 그래서 봇이 잠긴 문을 그냥 통과했다.
            _botPlayerManager.SetDoorOpenResolver((matchingId, doorId) =>
                MatchRuntimes.Get(matchingId)?.Doors.IsDoorOpen(doorId) == true);
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
        networkService.SessionFactory = CreateClientSession;
        networkService.Listen(IPAddress.Any, port);
        logger.LogInformation($"TCP server listening on port {port}");
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
                foreach (var s in matchingSessions) s.TrySend(eliminatedPacket);
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
        BroadcastGroundItemSpawn(
            matchingId, bot.CurrentArea, drop.SpawnedItems,
            matchingSessions.Where(session => session.CurrentArea == bot.CurrentArea));

        logger.LogInformation(
            "Bot elimination inventory scattered: MatchingId={MatchingId}, BotId={BotId}, Area={Area}, ItemCount={ItemCount}",
            matchingId, botPlayerId, bot.CurrentArea, drop.DroppedItemIds.Count);
    }
    private void BroadcastGroundItemSpawn(
        long matchingId, AreaType area, IReadOnlyList<GroundItemInfo> spawned,
        IEnumerable<GameClientSession> targets)
    {
        if (spawned == null || spawned.Count == 0 || area == AreaType.None)
            return;

        var receivers = targets.ToList();
        if (receivers.Count == 0)
            return;

        using var packet = PacketMaker.G_TO_C_GROUND_ITEM_SPAWN((int)area, spawned.ToList());
        foreach (var session in receivers)
            session.TrySend(packet);
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
                .Where(s => s.PlayerId.HasValue && s.MatchingId == matchingId && s.CurrentArea == area)
                .ToList();
            if (sameAreaSessions.Count == 0) continue;

            var msg = new G_TO_C_EXPLORE_START { PlayerId = botId, InteractId = interactId };
            var body = MessagePackSerializer.Serialize(msg);
            foreach (var session in sameAreaSessions)
            {
                using var packet = Packet.Create((int)Protocol.G_TO_C_EXPLORE_START, session.PlayerId!.Value);
                packet.SetBody(body);
                session.TrySend(packet);
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
                .Where(s => s.PlayerId.HasValue && s.MatchingId == matchingId && s.CurrentArea == area)
                .ToList();
            if (sameAreaSessions.Count == 0) continue;

            var msg = new G_TO_C_EXPLORE_END { PlayerId = botId };
            var body = MessagePackSerializer.Serialize(msg);
            foreach (var session in sameAreaSessions)
            {
                using var packet = Packet.Create((int)Protocol.G_TO_C_EXPLORE_END, session.PlayerId!.Value);
                packet.SetBody(body);
                session.TrySend(packet);
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
                session.TrySend(packet);
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
            if (MatchStartGate.IsEntryTimedOut(matchingId, DateTime.UtcNow))
            {
                var anchorSession = activeSessions.FirstOrDefault(
                    session => session.MatchingId == matchingId && session.PlayerId.HasValue);
                if (anchorSession != null)
                {
                    logger.LogWarning(
                        "Match entry deadline expired before every human became ready: MatchingId={MatchingId}",
                        matchingId);
                    EntryFailureHandler.Handle(anchorSession);
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
                    .Where(session => session.MatchingId == matchingId)
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
                    session.TrySend(packet);
            }
        }
    }


    private IConnectionSession? CreateClientSession(TcpConnection connection)
    {
        if (Volatile.Read(ref _stopping) != 0)
        {
            connection.Disconnect();
            return null;
        }

        try
        {
            var session = new GameClientSession(
                connection,
                logger,
                redisOperations,
                ticket => gameHandoffTicketService.ConsumeAsync(ticket, nodeOptions.NodeId),
                OnClientSessionLeave,
                RegisterClientSession,
                GetSessionsByInstance,
                _interactableStateManager,
                _inGameInventoryManager,
                _groundItemManager,
                _summonStoneManager,
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
                    MatchingLifecycle.Publish(MatchingLifecycleSubjects.PlayerLeft, playerId, matchingId),
                (playerId, matchingId) =>
                    MatchingLifecycle.PreparePublication(
                        MatchingLifecycleSubjects.PlayerCompleted,
                        playerId,
                        matchingId),
                (playerId, matchingId) =>
                    MatchingLifecycle.Publish(MatchingLifecycleSubjects.PlayerReleased, playerId, matchingId),
                () => Volatile.Read(ref _stopping) != 0,
                EntryFailureHandler.Handle,
                devOptions: devOptions);

            logger.LogInformation("Game client session created");
            return session;
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "Failed to create game client session");
            connection.Disconnect();
            return null;
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
                    session.MatchingId);
                if (session.MatchingId > 0)
                    CleanupMatchingIfNoHumanSessionsRemain(session.MatchingId);
                return;
            }

            logger.LogInformation("Game client session removed: PlayerId={SessionPlayerId}", session.PlayerId.Value);

            if (session.MatchingId > 0 && session.CurrentArea != AreaType.None)
            {
                using var leavePacket = PacketMaker.G_TO_C_AREA_PLAYER_LEAVE(session.PlayerId.Value);
                var sameAreaSessions = GetSessionsByMatch(session.MatchingId)
                    .Where(other =>
                        !ReferenceEquals(other, session) &&
                        other.CurrentArea == session.CurrentArea)
                    .ToList();
                foreach (var other in sameAreaSessions) other.TrySend(leavePacket);

                logger.LogInformation(
                    "Broadcasted disconnected player leave: PlayerId={PlayerId}, MatchingId={MatchingId}, Area={Area}, Receivers={ReceiverCount}",
                    session.PlayerId.Value,
                    session.MatchingId,
                    session.CurrentArea,
                    sameAreaSessions.Count);
            }

            if (session.MatchingId > 0)
                CleanupMatchingIfNoHumanSessionsRemain(session.MatchingId);
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

    // ===== 내부 매치 조회 및 개발 도구 =====

    private List<long> GetActiveMatchingIds()
    {
        var ids = _sessionRegistry.GetActiveMatchingIds().ToHashSet();
        foreach (long matchingId in _botPlayerManager.GetActiveMatchingIds())
        {
            if (matchingId > 0)
            {
                ids.Add(matchingId);
            }
        }

        return ids.OrderBy(id => id).ToList();
    }
}
