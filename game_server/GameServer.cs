using System.Net;
using game_server.network;
using game_server.services;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using network.common;
using network.common.data.helpers;
using network.core;
using network.gamehandoff;
using network.hosting;
using network.infrastructure.redis;
using network.routing;

namespace game_server;

/// <summary>
///     GameServer의 시작과 종료를 관리하고, 게임 세션과 매치 처리에 필요한 구성 요소를 연결한다.
///     TCP 연결을 받고 게임 진행용 타이머를 실행하며, 노드의 접속 정보와 수용 상태를 등록한다.
///     매치 상태 변경은 매치별 잠금 안에서 처리한다.
///     입장 실패는 MatchEntryFailureHandler에, 세션 퇴장은 GameSessionLeaveHandler에 맡긴다.
///     종료 알림과 Redis 정리는 MatchingLifecycleService에 위임한다.
/// </summary>
internal partial class GameServer(
    NetworkService networkService,
    ILogger<GameServer> logger,
    ILogger<GameClientSession> sessionLogger,
    IConfiguration configuration,
    IRedisOperations redisOperations,
    MatchingLifecycleService matchingLifecycle,
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
    GameSessionLeaveHandler sessionLeaveHandler,
    MatchCleanupService matchCleanup,
    BotEliminationService botEliminations,
    MatchCountdownService countdown,
    GameServerTickService tickService,
    BotMovementService botMovement)
    : IHostedService
{
    private GameServerNodeAdvertiser? _nodeAdvertiser;
    private SwarmMatchRuntime GetSwarmMatchRuntime(long matchingId) => matchRuntimes.GetRequired(matchingId).Swarm;
    private readonly object _shutdownLock = new();
    private Task? _shutdownTask;
    private int _stopping;

    public async Task StartAsync(CancellationToken cancellationToken)
    {
        readinessState.MarkNotReady("starting");
        try
        {
            logger.LogInformation("Game server starting...");
            cancellationToken.ThrowIfCancellationRequested();
            int port = ResolveServicePort(configuration);
            InitializeServices(cancellationToken);
            StartNetworkService(port);
            StartGameTicks();
            await StartNodeAdvertisementAsync();
            readinessState.MarkReady();
            logger.LogInformation("Game server started successfully.");
        }
        catch (Exception ex)
        {
            readinessState.MarkNotReady("startup_failed");
            logger.LogError(ex, "Game server starting failed.");
            await StopAsync(CancellationToken.None);
            throw;
        }
    }

    private async Task StartNodeAdvertisementAsync()
    {
        _nodeAdvertiser = new GameServerNodeAdvertiser(gameServerRegistry, nodeOptions, () => matchRuntimes.ActiveIds().Count, logger);
        await _nodeAdvertiser.StartAsync();
    }

    public Task StopAsync(CancellationToken cancellationToken)
    {
        lock (_shutdownLock)
        {
            _shutdownTask ??= StopCoreAsync();
            return _shutdownTask;
        }
    }

    private async Task StopCoreAsync()
    {
        Volatile.Write(ref _stopping, 1);
        readinessState.MarkNotReady("stopping");
        logger.LogInformation("Game server stopping...");

        if (_nodeAdvertiser != null)
        {
            try
            {
                await _nodeAdvertiser.StopAsync();
            }
            catch (Exception ex)
            {
                logger.LogWarning(ex, "Game server node advertisement shutdown failed.");
            }
            finally
            {
                _nodeAdvertiser = null;
            }
        }

        try
        {
            await tickService.StopAsync();
        }
        catch (Exception ex)
        {
            logger.LogWarning(ex, "Game server tick shutdown failed.");
        }

        foreach (var session in sessions.SnapshotAll())
        {
            session.MarkServerInitiatedDisconnect();
        }

        try
        {
            await networkService.StopAsync(CancellationToken.None);
        }
        catch (Exception ex)
        {
            logger.LogWarning(ex, "Game server network shutdown failed.");
        }

        try
        {
            await matchingLifecycle.DrainAsync();
        }
        catch (Exception ex)
        {
            logger.LogWarning(ex, "Game server matching Redis cleanup failed.");
        }

        await matchingLifecycle.CloseAsync();

        logger.LogInformation("Game server stopped.");
    }

    private void InitializeServices(CancellationToken cancellationToken)
    {
        var enabledDevFlags = devOptions.EnabledVariableNames();
        if (enabledDevFlags.Count > 0)
            logger.LogWarning("[DEV] Game Server flags enabled: {Flags}", string.Join(", ", enabledDevFlags));

        GameDataHelper.SetBasePath(AppDomain.CurrentDomain.BaseDirectory);
        GameDataHelper.Initialize(
            message => logger.LogDebug("{Message}", message),
            message => logger.LogError("{Message}", message));
        cancellationToken.ThrowIfCancellationRequested();

        logger.LogInformation("Services initialized successfully");
    }

    internal static int ResolveServicePort(IConfiguration configuration)
    {
        string? configuredPort = configuration["clientPort"];
        if (configuredPort == null)
        {
            return 9001;
        }
        if (!int.TryParse(configuredPort, out int port) || port is < 1 or > 65535)
        {
            throw new InvalidOperationException("clientPort must be configured as an integer between 1 and 65535.");
        }
        return port;
    }

    private void StartNetworkService(int port)
    {
        networkService.SessionFactory = CreateClientSession;
        networkService.Listen(IPAddress.Any, port);
        logger.LogInformation("Listening on port {Port}", port);
    }

    private void StartGameTicks()
    {
        var tickRunner = new MatchTickRunner(
            matchRuntimes, sessions, logger,
            countdown.Broadcast,
            ProcessSwarmArenaForMatching,
            ProcessEnvironmentalTickForMatching,
            runtime => botMovement.Process(runtime, ResolveSwarmBotDirective),
            ProcessAreaClosureForMatching);
        tickService.Start(tickRunner.Run);
    }

    private void ProcessAreaClosureForMatching(long matchingId, GameClientSession[] sessionSnapshot)
    {
        var plan = PrepareSwarmScheduledClosureTick(matchingId, sessionSnapshot);
        if (plan != null)
        {
            DispatchSwarmClosurePublicationPlan(plan, sessionSnapshot);
        }
    }

    private GameClientSession? CreateClientSession(TcpConnection connection)
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
                sessionLogger,
                redisOperations,
                ticket => gameHandoffTicketService.ConsumeAsync(ticket, nodeOptions.NodeId),
                sessionLeaveHandler,
                sessions.Register,
                sessions.GetByInstance,
                eventLogs,
                summaryFileStore,
                matchRuntimes,
                HandleSwarmGrowthPick,
                HandleSwarmOrbDecision,
                matchingLifecycle,
                () => Volatile.Read(ref _stopping) != 0,
                entryFailureHandler,
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

}
