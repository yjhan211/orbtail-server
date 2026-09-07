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
internal partial class GameServer(
    IConfiguration configuration,
    ILogger<GameServer> logger,
    MatchingLifecycleService matchingLifecycle,
    IRedisOperations redisOperations,
    NetworkService networkService,
    GameHandoffTicketService gameHandoffTicketService,
    ServerReadinessState readinessState,
    IGameServerRegistry gameServerRegistry,
    GameServerNodeOptions nodeOptions,
    GameServerDevOptions devOptions,
    GameSessionRegistry sessions,
    MatchRuntimeStore matchRuntimes,
    GameEventLogManager eventLogs,
    MatchSummaryFileStore summaryFileStore,
    MatchEntryFailureHandler entryFailureHandler,
    MatchCleanupService matchCleanup,
    BotEliminationService botEliminations,
    MatchCountdownService countdown)
    : IHostedService
{
    private static readonly TimeSpan ShutdownWarningThreshold = TimeSpan.FromSeconds(5);

    // 서버 수명과 주기 작업
    private const int ProximityAutoCombatTickIntervalMs = 50;
    private Timer? _proximityAutoCombatTimer;
    private Timer? _areaClosureTickTimer;
    private GameServerNodeAdvertiser? _nodeAdvertiser;
    private int _stopping;

    private SwarmMatchRuntime GetSwarmMatchRuntime(long matchingId) =>
        matchRuntimes.GetRequired(matchingId).Swarm;

    public async Task StartAsync(CancellationToken cancellationToken)
    {
        Volatile.Write(ref _stopping, 0);
        readinessState.MarkNotReady("starting");

        try
        {
            logger.LogInformation("Game server starting...");
            IReadOnlyList<string> enabledDevFlags = devOptions.EnabledVariableNames();
            if (enabledDevFlags.Count > 0)
                logger.LogWarning("[DEV] Game Server flags enabled: {Flags}",
                    string.Join(", ", enabledDevFlags));

            InitializeServices();

            StartTcpServer();
            StartAreaClosureTickTimer();
            StartProximityAutoCombatTimer();

            // 광고는 리슨·타이머가 모두 선 뒤에 — 배정받은 클라이언트가 바로 접속할 수 있어야 한다.
            _nodeAdvertiser = new GameServerNodeAdvertiser(
                gameServerRegistry,
                nodeOptions,
                () => matchRuntimes.ActiveIds().Count,
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
        foreach (var session in sessions.SnapshotAll())
            session.MarkServerInitiatedDisconnect();

        // Host cancellation must not skip later cleanup stages. Log slow stages at a fixed
        // threshold, but keep dependencies alive until the stage actually quiesces.
        await RunShutdownStageAsync(
            networkService.StopAsync(CancellationToken.None),
            "network connections");

        Timer?[] timers =
        [
            _areaClosureTickTimer,
            _proximityAutoCombatTimer
        ];
        _areaClosureTickTimer = null;
        _proximityAutoCombatTimer = null;
        await RunShutdownStageAsync(
            Task.WhenAll(timers.Where(timer => timer != null)
                .Select(timer => timer!.DisposeAsync().AsTask())),
            "timers");

        await RunShutdownStageAsync(
            matchingLifecycle.DrainAsync(),
            "matching Redis cleanup");

        if (_nodeAdvertiser != null)
        {
            await RunShutdownStageAsync(_nodeAdvertiser.RemoveAsync(), "node registry removal");
            await _nodeAdvertiser.DisposeAsync();
            _nodeAdvertiser = null;
        }

        await matchingLifecycle.CloseAsync();

        logger.LogInformation("Game server stopped.");
    }

    private async Task RunShutdownStageAsync(Task operation, string stage)
    {
        try
        {
            await operation.WaitAsync(ShutdownWarningThreshold);
        }
        catch (TimeoutException ex)
        {
            logger.LogWarning(ex, "Shutdown stage exceeded warning threshold: Stage={Stage}, Timeout={Timeout}",
                stage, ShutdownWarningThreshold);
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
        try
        {
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

    private void StartProximityAutoCombatTimer()
    {
        var tickRunner = new MatchTickRunner(
            matchRuntimes, sessions, logger,
            countdown.Broadcast,
            ProcessSwarmArenaForMatching,
            ProcessEnvironmentalTickForMatching,
            ProcessBotMovementForMatching);
        _proximityAutoCombatTimer = new Timer(
            _ => tickRunner.Run(),
            null,
            TimeSpan.FromMilliseconds(ProximityAutoCombatTickIntervalMs),
            TimeSpan.FromMilliseconds(ProximityAutoCombatTickIntervalMs));
        logger.LogInformation("Match tick timer started: TickMs={TickMs}", ProximityAutoCombatTickIntervalMs);
    }

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
            foreach (long matchingId in matchRuntimes.ActiveIds())
            {
                // #272 자기장 폐쇄: 자기장에서 파생한 구역 시간표 하나로만 닫는다 —
                // 필드 오염은 정산 리소스 틱(GetSwarmFieldCorruptionPerTick)이 준다.
                if (!MatchStartGate.IsGameplayActive(matchingId)) continue;
                if (!matchRuntimes.Enter(matchingId, out MatchScope scope))
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

                    eventLogs,
                summaryFileStore,
                matchRuntimes,
                HandleSwarmGrowthPick,
                HandleSwarmOrbDecision,
                (playerId, matchingId) =>
                    matchingLifecycle.Publish(MatchingLifecycleSubjects.PlayerLeft, playerId, matchingId),
                (playerId, matchingId) =>
                    matchingLifecycle.PreparePublication(
                        MatchingLifecycleSubjects.PlayerCompleted,
                        playerId,
                        matchingId),
                (playerId, matchingId) =>
                    matchingLifecycle.Publish(MatchingLifecycleSubjects.PlayerReleased, playerId, matchingId),
                () => Volatile.Read(ref _stopping) != 0,
                entryFailureHandler.Handle,
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
            bool removed = sessions.Remove(session);

            if (!removed)
            {
                logger.LogDebug(
                    "Ignored removal from a superseded game session: PlayerId={PlayerId}, MatchingId={MatchingId}",
                    session.PlayerId.Value,
                    session.MatchingId);
                if (session.MatchingId > 0)
                    matchCleanup.CleanupIfNoHumanSessionsRemain(session.MatchingId);
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
                matchCleanup.CleanupIfNoHumanSessionsRemain(session.MatchingId);
        }
    }

    private Action? RegisterClientSession(long playerId, GameClientSession session)
    {
        GameClientSession? existingSession = sessions.Register(playerId, session, out bool added);
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
        return sessions.GetByMatch(matchingId);
    }

    private List<GameClientSession> GetSessionsByInstance(MapId mapId, long mapSubId)
    {
        return sessions.GetByInstance(mapId, mapSubId);
    }

    // ===== 내부 매치 조회 및 개발 도구 =====

    private List<long> GetActiveMatchingIds()
    {
        var ids = sessions.GetActiveMatchingIds().ToHashSet();
        foreach (long matchingId in matchRuntimes.ActiveIds())
        {
            if (matchingId > 0)
            {
                ids.Add(matchingId);
            }
        }

        return ids.OrderBy(id => id).ToList();
    }
}
