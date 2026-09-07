using System.Net;
using game_server.network;
using game_server.services;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using network.common.data.helpers;
using network.core;
using network.gamehandoff;
using network.hosting;
using network.infrastructure.redis;
using network.routing;

namespace game_server;

/// <summary>
///     GameServer의 시작과 종료를 관리하고, 새 TCP 연결마다 GameClientSession을 생성한다.
///     필요한 서비스는 Program.cs에서 DI로 전달받는다.
///
///     시작할 때 게임 데이터를 불러오고 TCP 접속과 매치별 틱 처리를 시작한 뒤,
///     Redis에 노드 정보를 등록해 매치를 배정받을 준비가 되었음을 알린다.
///
///     종료할 때는 새 세션 생성과 매치 배정을 막고, 틱 처리와 연결 정리가 끝나기를 기다린다.
///     이후 매칭 관련 Redis 정리 작업을 기다리고 노드 등록과 NATS 연결을 정리한다.
///     시작 실패와 일반 종료는 같은 정리 절차를 사용한다.
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
    MatchEnvironmentService environmentService,
    GameServerTickService tickService,
    BotMovementService botMovement)
    : IHostedService
{
    private GameServerNodeAdvertiser? _nodeAdvertiser;
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
            environmentService.Process,
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
